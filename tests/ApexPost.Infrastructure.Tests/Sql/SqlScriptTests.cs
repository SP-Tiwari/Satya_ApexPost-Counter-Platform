using System.Diagnostics;
using System.Text.RegularExpressions;
using ApexPost.Application.Persistence;
using ApexPost.Application.Services;
using ApexPost.Domain.Enums;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static ApexPost.Infrastructure.Tests.Sql.SqlTestHost;

namespace ApexPost.Infrastructure.Tests.Sql;

// Runs the files in /sql against the disposable database, and compares each
// report with the application's own calculation over the same rows.
[Collection(SqlCollection.Name)]
public sealed class SqlScriptTests(SqlTestHost host) : IAsyncLifetime
{
	private const string DemoBranch = "DM01";
	private const string DemoSku = "DEMO-SKU-001";
	private const string DemoManager = "demo-staff-02";
	private const string ProbeUser = "apexpost_probe";

	public Task InitializeAsync() => host.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task DailySalesProcedure_MatchesTheApplicationReport()
	{
		await SellAsync(Stationery, 3, withShippingAndMoneyOrder: true);
		await SellAsync(Packaging, 2);
		var today = DateOnly.FromDateTime(DateTime.UtcNow);

		var expected = Assert.Single(await host.InRequestAsync(services =>
			new DailyBranchSalesReportApplicationService(
				services.GetRequiredService<IStaffRepository>(),
				services.GetRequiredService<ICounterTransactionRepository>())
				.GetReportAsync(ManagerId, today)));
		var actual = await ProceduresAsync(procedures =>
			procedures.GetDailyBranchSalesAsync(Branch.Value, today));

		Assert.NotNull(actual);
		Assert.Equal(Branch.Value, actual.BranchCode);
		Assert.Equal(today, actual.BusinessDate);
		Assert.Equal(expected.RetailSales.Currency, actual.Currency);
		Assert.Equal(TwoPlaces(expected.RetailSales.Amount), actual.RetailSales);
		Assert.Equal(TwoPlaces(expected.ServiceSales.Amount), actual.ServiceSales);
		Assert.Equal(TwoPlaces(expected.TotalSales.Amount), actual.TotalSales);
	}

	[Fact]
	public async Task DailySalesProcedure_LeavesOutUnpaidSalesAndOtherDates()
	{
		var sale = Sale(Stationery, 1);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, sale));
		await host.InRequestAsync(services =>
			host.Transactions(services).VoidAsync(sale.TransactionId, ManagerId, "Mistake"));
		var today = DateOnly.FromDateTime(DateTime.UtcNow);
		await SellAsync(Packaging, 1);

		var voidedExcluded = await ProceduresAsync(procedures =>
			procedures.GetDailyBranchSalesAsync(Branch.Value, today));
		var otherDate = await ProceduresAsync(procedures =>
			procedures.GetDailyBranchSalesAsync(Branch.Value, today.AddDays(-1)));

		Assert.Equal(10m, voidedExcluded!.RetailSales);
		Assert.Null(otherDate);
	}

	[Theory]
	[InlineData("", "@BranchCode is required.")]
	[InlineData("BR01", "@Date must be a valid business date.")]
	public async Task DailySalesProcedure_RejectsInvalidParameters(string branch, string message)
	{
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			ProceduresAsync(procedures =>
				procedures.GetDailyBranchSalesAsync(branch, DateOnly.MaxValue)));

		Assert.Equal(message, exception.Message);
	}

	[Fact]
	public async Task DailySalesProcedure_ReportsAnUnknownBranchAsNotFound()
	{
		var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			ProceduresAsync(procedures => procedures.GetDailyBranchSalesAsync(
				"NOPE", new DateOnly(2026, 1, 1))));

		Assert.Equal("Branch 'NOPE' was not found.", exception.Message);
	}

	[Fact]
	public async Task LowStockProcedure_MatchesTheApplicationReport()
	{
		await SellAsync(Stationery, 8);

		var expected = await host.InRequestAsync(services =>
			new LowStockReportApplicationService(
				services.GetRequiredService<IStaffRepository>(),
				services.GetRequiredService<IStockRepository>(),
				2).GetReportAsync(ManagerId));
		var actual = await ProceduresAsync(procedures =>
			procedures.GetLowStockAsync(Branch.Value, 2));

		Assert.Equal(
			expected.Select(row => (row.BranchCode.Value, row.ProductSku.Value, row.Quantity, row.Threshold)),
			actual.Select(row => (row.BranchCode, row.Sku, row.Quantity, row.Threshold)));
		Assert.Equal(Stationery.Value, Assert.Single(actual).Sku);
	}

	[Fact]
	public async Task LowStockProcedure_NullBranchCoversEveryBranch()
	{
		await host.QueryAsync(async context =>
		{
			context.BranchStock.Add(new ApexPost.Domain.Entities.BranchStock(
				OtherBranch, Stationery, 1));
			return await context.SaveChangesAsync();
		});

		var rows = await ProceduresAsync(procedures =>
			procedures.GetLowStockAsync(null, 10));

		Assert.Equal(
			[(Branch.Value, Packaging.Value), (Branch.Value, Stationery.Value), (OtherBranch.Value, Stationery.Value)],
			rows.Select(row => (row.BranchCode, row.Sku)));
	}

	[Fact]
	public async Task LowStockProcedure_RejectsANegativeThreshold()
	{
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			ProceduresAsync(procedures => procedures.GetLowStockAsync(null, -1)));

		Assert.Equal("Low-stock threshold cannot be negative.", exception.Message);
	}

	[Fact]
	public async Task LowStockProcedure_TreatsMaliciousInputAsAValue()
	{
		await Assert.ThrowsAsync<KeyNotFoundException>(() => ProceduresAsync(
			procedures => procedures.GetLowStockAsync("x';DROP--", 1)));

		Assert.Equal(2, await host.QueryAsync(context => context.BranchStock.CountAsync()));
	}

	[Fact]
	public async Task AdjustStockProcedure_ChangesStockAndWritesHistoryTogether()
	{
		var result = await ProceduresAsync(procedures => procedures.AdjustStockAsync(
			Branch.Value, Stationery.Value, -3, "  Damaged in store  ", ManagerId));

		Assert.Equal(7, result.NewQuantity);
		await host.QueryAsync(async context =>
		{
			var stock = await context.BranchStock
				.SingleAsync(row => row.ProductSku == Stationery);
			Assert.Equal(7, stock.Quantity);
			var history = await context.StockAdjustments.SingleAsync();
			Assert.Equal(result.StockAdjustmentId, history.Id);
			Assert.Equal(-3, history.Delta);
			Assert.Equal("Damaged in store", history.Reason);
			Assert.Equal(ManagerId, history.ChangedByStaffId);
			return true;
		});
	}

	[Fact]
	public async Task AdjustStockProcedure_RejectsMoreThanIsInStock()
	{
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			ProceduresAsync(procedures => procedures.AdjustStockAsync(
				Branch.Value, Stationery.Value, -11, "Count", ManagerId)));

		Assert.Equal("Insufficient stock available.", exception.Message);
		await AssertStockUnchangedAsync();
	}

	[Theory]
	[InlineData(ClerkId)]
	[InlineData("nobody")]
	public async Task AdjustStockProcedure_RequiresABranchManager(string staffId)
	{
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			ProceduresAsync(procedures => procedures.AdjustStockAsync(
				Branch.Value, Stationery.Value, 1, "Count", staffId)));

		await AssertStockUnchangedAsync();
	}

	[Fact]
	public async Task AdjustStockProcedure_RequiresAManagerOfThatBranch()
	{
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			ProceduresAsync(procedures => procedures.AdjustStockAsync(
				OtherBranch.Value, Stationery.Value, 1, "Count", ManagerId)));
	}

	[Theory]
	[InlineData(0, "Count")]
	[InlineData(int.MinValue, "Count")]
	[InlineData(1, "   ")]
	public async Task AdjustStockProcedure_RejectsInvalidParameters(int delta, string reason)
	{
		await Assert.ThrowsAsync<ArgumentException>(() =>
			ProceduresAsync(procedures => procedures.AdjustStockAsync(
				Branch.Value, Stationery.Value, delta, reason, ManagerId)));

		await AssertStockUnchangedAsync();
	}

	[Fact]
	public async Task AdjustStockProcedure_RejectsAReasonLongerThanTheColumn()
	{
		await Assert.ThrowsAsync<ArgumentException>(() =>
			ProceduresAsync(procedures => procedures.AdjustStockAsync(
				Branch.Value, Stationery.Value, 1, new string('x', 501), ManagerId)));
	}

	[Fact]
	public async Task AdjustStockProcedure_ReportsMissingStockAsNotFound()
	{
		await host.QueryAsync(context => context.Database.ExecuteSqlAsync(
			$"UPDATE dbo.Staff SET HomeBranchCode = 'BR02' WHERE StaffId = 'manager-1'"));

		var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			ProceduresAsync(procedures => procedures.AdjustStockAsync(
				OtherBranch.Value, Stationery.Value, 1, "Count", ManagerId)));

		Assert.Equal(
			"Stock for product 'STAT-1' at branch 'BR02' was not found.",
			exception.Message);
	}

	[Fact]
	public async Task AdjustStockProcedure_RollsStockBackWhenTheHistoryRowFails()
	{
		await WithConstraintAsync(
			"StockAdjustments", "CK_Test_RefuseHistory", "[Delta] <> -3",
			async () =>
			{
				await Assert.ThrowsAsync<SqlException>(() =>
					ProceduresAsync(procedures => procedures.AdjustStockAsync(
						Branch.Value, Stationery.Value, -3, "Count", ManagerId)));
				await AssertStockUnchangedAsync();
			});
	}

	[Fact]
	public async Task AdjustStockProcedure_StopsConcurrentCallsGoingBelowZero()
	{
		var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
		{
			try
			{
				await ProceduresAsync(procedures => procedures.AdjustStockAsync(
					Branch.Value, Stationery.Value, -1, "Count", ManagerId));
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}));

		Assert.Equal(10, results.Count(succeeded => succeeded));
		await host.QueryAsync(async context =>
		{
			Assert.Equal(0, (await context.BranchStock
				.SingleAsync(row => row.ProductSku == Stationery)).Quantity);
			Assert.Equal(10, await context.StockAdjustments.CountAsync());
			return true;
		});
	}

	[Fact]
	public async Task AdjustStockProcedure_MakesAnEarlierReadOfTheRowStale()
	{
		await host.InRequestAsync(async services =>
		{
			var context = services.GetRequiredService<ApexPostDbContext>();
			var stock = await context.BranchStock
				.SingleAsync(row => row.ProductSku == Stationery);
			stock.RemoveStock(1);

			await ProceduresAsync(procedures => procedures.AdjustStockAsync(
				Branch.Value, Stationery.Value, -1, "Count", ManagerId));

			await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
				context.SaveChangesAsync());
		});
	}

	[Fact]
	public async Task Procedures_MatchTheRepositoryFilesAfterAllMigrations()
	{
		foreach (var name in new[] { "usp_DailyBranchSales", "usp_LowStockReport", "usp_AdjustStock" })
		{
			var deployed = await ScalarAsync(
				$"SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.{name}'))");
			var file = ReadScript("procedures", $"{name}.sql");

			Assert.Equal(Normalize(file), Normalize((string)deployed!));
		}
	}

	[Fact]
	public void IdempotentScript_CreatesEachProcedureThroughExec()
	{
		var script = ReadScript("schema", "ApexPostDb-idempotent.sql");

		Assert.Equal(3, Regex.Count(script, @"CREATE OR ALTER PROCEDURE"));
		Assert.Equal(3, Regex.Count(script, @"EXEC\(N'--"));

		// A plain "ALTER PROCEDURE" would also match inside CREATE OR ALTER PROCEDURE.
		var alter = Assert.Single(Regex.Matches(script, @"(?<!OR )ALTER PROCEDURE (dbo\.\w+)"));
		Assert.Equal("dbo.usp_DailyBranchSales", alter.Groups[1].Value);
		Assert.Equal(1, Regex.Count(script, @"EXEC sp_executesql N'--"));

		var recompile = ProcedureMigrationTests.MigrationBlock(
			script, ProcedureMigrationTests.RecompileId);
		Assert.Contains("EXEC sp_executesql N'--", recompile);
		Assert.Contains(alter.Value, recompile);
		Assert.Contains("OPTION (RECOMPILE)", recompile);
	}

	[Fact]
	public async Task DailySalesReportScript_SplitsRetailAndServices()
	{
		await RunFileAsync("demos", "demo-fixture.sql");

		var row = Assert.Single(await QueryFileAsync("reports", "01-daily-branch-sales.sql"));

		Assert.Equal(DemoBranch, row["BranchCode"]);
		Assert.Equal(1, row["PaidTransactions"]);
		Assert.Equal(25.96m, row["RetailSales"]);
		Assert.Equal(17.70m, row["ServiceSales"]);
	}

	[Fact]
	public async Task NoSalesTodayScript_ListsOnlyBranchesWithoutAPaidSaleToday()
	{
		await RunFileAsync("demos", "demo-fixture.sql");

		var rows = await QueryFileAsync("reports", "02-branches-with-no-sales-today.sql");

		Assert.Equal(
			[Branch.Value, OtherBranch.Value, "DM02", "DM03"],
			rows.Select(row => (string)row["BranchCode"]));
	}

	[Fact]
	public async Task TopProductsScript_KeepsTheTopTenOfEachRegion()
	{
		await host.QueryAsync(async context =>
		{
			foreach (var index in Enumerable.Range(1, 12))
			{
				context.Products.Add(new ApexPost.Domain.Entities.Product(
					new ApexPost.Domain.ValueObjects.Sku($"RANK-{index:00}"),
					$"Ranked {index}",
					ProductCategory.Stationery,
					new ApexPost.Domain.ValueObjects.Money(index, "INR"),
					TaxCategory.ZeroRated));
				context.BranchStock.Add(new ApexPost.Domain.Entities.BranchStock(
					Branch, new ApexPost.Domain.ValueObjects.Sku($"RANK-{index:00}"), 5));
			}

			return await context.SaveChangesAsync();
		});
		foreach (var index in Enumerable.Range(1, 12))
			await SellAsync(new ApexPost.Domain.ValueObjects.Sku($"RANK-{index:00}"), 1);

		var rows = await QueryFileAsync(
			"reports", "03-top-10-products-by-revenue-per-region.sql");

		Assert.Equal(10, rows.Count);
		Assert.Equal("RANK-12", rows[0]["Sku"]);
		Assert.Equal(12m, rows[0]["Revenue"]);
		Assert.Equal("RANK-03", rows[9]["Sku"]);
		Assert.Equal(10L, rows[9]["RevenueRank"]);
	}

	[Fact]
	public async Task ShippingVolumeScript_MatchesTheApplicationReport()
	{
		await SellAsync(Stationery, 1, withShippingAndMoneyOrder: true);
		await SellAsync(Stationery, 1, withShippingAndMoneyOrder: true);

		var expected = await host.InRequestAsync(services =>
			new ShippingVolumeReportApplicationService(
				services.GetRequiredService<IStaffRepository>(),
				services.GetRequiredService<ICounterTransactionRepository>())
				.GetReportAsync(AdministratorId));
		var rows = await QueryFileAsync(
			"reports", "04-shipping-volume-by-zone-and-service-level.sql");

		Assert.Equal(
			expected.Select(row => (row.DestinationZone, row.ServiceLevel.ToString(), row.ShipmentCount)),
			rows.Select(row => ((string)row["ZoneCode"], (string)row["ServiceLevel"], (int)row["ShipmentCount"])));
	}

	[Fact]
	public async Task MoneyOrderScript_MatchesTheApplicationReport()
	{
		await SellAsync(Stationery, 1, withShippingAndMoneyOrder: true);
		await RunFileAsync("demos", "demo-fixture.sql");

		var expected = await host.InRequestAsync(services =>
			new BranchAuditorReportApplicationService(
				services.GetRequiredService<IStaffRepository>(),
				services.GetRequiredService<ICounterTransactionRepository>())
				.GetReportAsync(AdministratorId));
		var rows = await QueryFileAsync(
			"reports", "05-money-orders-and-identity-verifications-per-branch.sql");

		Assert.Equal(
			expected.Select(row => (row.BranchCode.Value, row.MoneyOrderCount, row.CancelledMoneyOrderCount, row.IdentityVerificationCount, row.SuccessfulIdentityVerificationCount, row.FailedIdentityVerificationCount)),
			rows.Select(row => (
				(string)row["BranchCode"],
				(int)row["MoneyOrderCount"],
				(int)row["CancelledMoneyOrderCount"],
				(int)row["IdentityVerificationCount"],
				(int)row["SuccessfulIdentityVerificationCount"],
				(int)row["FailedIdentityVerificationCount"])));
	}

	[Fact]
	public async Task AtomicSaleScript_CommitsEveryPartTogether()
	{
		await RunFileAsync("demos", "demo-fixture.sql");

		var outcome = Assert.Single(await QueryFileAsync("transactions", "atomic-sale.sql"));

		var id = (Guid)outcome["TransactionId"];
		Assert.Equal(38, await ScalarAsync(StockSql));
		Assert.Equal(39, await ScalarAsync(StockSql.Replace("001", "002")));
		Assert.Equal(2, await CountAsync("TransactionLines", id));
		Assert.Equal(3, await CountAsync("TransactionTaxRates", id));
		Assert.Equal(37.96m, await ScalarAsync(
			$"SELECT Amount FROM dbo.Payments WHERE TransactionId = '{id}'"));
	}

	[Fact]
	public async Task AtomicSaleScript_StopsWhenStockIsShort()
	{
		await RunFileAsync("demos", "demo-fixture.sql");
		await ExecuteAsync(
			"UPDATE dbo.BranchStock SET Quantity = 0 WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-002'");
		var before = await ScalarAsync("SELECT COUNT(*) FROM dbo.CounterTransactions");

		var exception = await Assert.ThrowsAsync<SqlException>(() =>
			QueryFileAsync("transactions", "atomic-sale.sql"));

		Assert.Equal(51003, exception.Number);
		Assert.Equal(40, await ScalarAsync(StockSql));
		Assert.Equal(before, await ScalarAsync("SELECT COUNT(*) FROM dbo.CounterTransactions"));
	}

	[Fact]
	public async Task AtomicSaleScript_RollsEverythingBackWhenTheLastInsertFails()
	{
		await RunFileAsync("demos", "demo-fixture.sql");
		var before = await ScalarAsync("SELECT COUNT(*) FROM dbo.TransactionLines");
		var payments = await ScalarAsync("SELECT COUNT(*) FROM dbo.Payments");

		await WithConstraintAsync(
			"TransactionTaxRates", "CK_Test_RefuseRate", "[Rate] < 0.1",
			async () =>
			{
				await Assert.ThrowsAsync<SqlException>(() =>
					QueryFileAsync("transactions", "atomic-sale.sql"));

				Assert.Equal(40, await ScalarAsync(StockSql));
				Assert.Equal(before, await ScalarAsync("SELECT COUNT(*) FROM dbo.TransactionLines"));
				Assert.Equal(payments, await ScalarAsync("SELECT COUNT(*) FROM dbo.Payments"));
				Assert.Equal(0, await ScalarAsync("SELECT @@TRANCOUNT"));
			},
			nocheck: true);
	}

	[Fact]
	public async Task DemoScripts_RefuseAnyDatabaseOutsideTheTestPrefix()
	{
		await using var connection = new SqlConnection(MasterConnectionString());
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = ReadScript("demos", "demo-fixture.sql");

		var exception = await Assert.ThrowsAsync<SqlException>(
			() => command.ExecuteNonQueryAsync());

		Assert.Contains("disposable", exception.Message);
	}

	[Fact]
	public async Task EfSale_WritesTablesInTheDocumentedOrder()
	{
		var commands = await CaptureCommandsAsync(() => SellAsync(Packaging, 1));

		var save = Assert.Single(commands, sql => sql.Contains("UPDATE [BranchStock]"));
		var positions = new[]
		{
			"UPDATE [BranchStock]",
			"INSERT INTO [CounterTransactions]",
			"INSERT INTO [Payments]",
			"INSERT INTO [TransactionLines]",
			"[TransactionTaxRates]"
		}.Select(table => save.IndexOf(table, StringComparison.Ordinal)).ToArray();

		Assert.DoesNotContain(-1, positions);
		Assert.Equal(positions.Order(), positions);
	}

	[Fact]
	public async Task EfSave_UpdatesStockRowsInSkuOrderWhateverOrderTheyChanged()
	{
		var commands = await CaptureCommandsAsync(() => host.QueryAsync(async context =>
		{
			var stationery = await context.BranchStock
				.SingleAsync(row => row.ProductSku == Stationery);
			var packaging = await context.BranchStock
				.SingleAsync(row => row.ProductSku == Packaging);
			stationery.RemoveStock(1);
			packaging.RemoveStock(1);
			return await context.SaveChangesAsync();
		}), includeParameters: true);

		var save = Assert.Single(commands, sql => sql.Contains("UPDATE [BranchStock]"));
		Assert.True(
			save.IndexOf(Packaging.Value, StringComparison.Ordinal) <
			save.IndexOf(Stationery.Value, StringComparison.Ordinal),
			save);
	}

	[Fact]
	public async Task LeastPrivilegeRole_CanSellAndRunProceduresButCannotDeleteOrRewriteHistory()
	{
		await RunFileAsync("security", "least-privilege-role.sql");
		await ExecuteAsync(
			$"""
			IF DATABASE_PRINCIPAL_ID('{ProbeUser}') IS NULL
				CREATE USER {ProbeUser} WITHOUT LOGIN;
			ALTER ROLE apexpost_app ADD MEMBER {ProbeUser};
			""");

		await host.InRequestAsync(async services =>
		{
			var context = services.GetRequiredService<ApexPostDbContext>();
			await context.Database.OpenConnectionAsync();
			await context.Database.ExecuteSqlAsync($"EXECUTE AS USER = 'apexpost_probe'");
			try
			{
				await host.Sales(services).CreateAndProcessAsync(
					ClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true));
				var result = await services.GetRequiredService<IStoredProcedures>()
					.AdjustStockAsync(Branch.Value, Stationery.Value, 1, "Count", ManagerId);
				Assert.Equal(10, result.NewQuantity);

				foreach (var forbidden in new[]
				{
					"DELETE FROM dbo.Payments",
					"UPDATE dbo.AuditEntries SET Action = 'x'",
					"UPDATE dbo.Payments SET Amount = 1",
					"DROP TABLE dbo.AuditEntries"
				})
				{
					var exception = await Assert.ThrowsAsync<SqlException>(
						() => context.Database.ExecuteSqlRawAsync(forbidden));
					Assert.True(
						exception.Number is 229 or 3701,
						$"{forbidden}: {exception.Number} {exception.Message}");
				}
			}
			finally
			{
				await context.Database.ExecuteSqlAsync($"REVERT");
				await context.Database.CloseConnectionAsync();
			}
		});
	}

	private const string StockSql =
		"SELECT Quantity FROM dbo.BranchStock WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-001'";

	private static decimal TwoPlaces(decimal value) =>
		Math.Round(value, 2, MidpointRounding.AwayFromZero);

	internal static string Normalize(string sql) =>
		Regex.Replace(sql.ReplaceLineEndings("\n").Trim(), @"CREATE\s+(OR ALTER\s+)?PROCEDURE", "CREATE PROCEDURE");

	internal static string ReadScript(params string[] path) =>
		File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "sql", .. path]));

	private string MasterConnectionString() =>
		new SqlConnectionStringBuilder(host.ConnectionString)
		{
			InitialCatalog = "master"
		}.ConnectionString;

	private async Task AssertStockUnchangedAsync()
	{
		await host.QueryAsync(async context =>
		{
			Assert.Equal(10, (await context.BranchStock
				.SingleAsync(row => row.ProductSku == Stationery)).Quantity);
			Assert.Equal(0, await context.StockAdjustments.CountAsync());
			return true;
		});
	}

	private Task<T> ProceduresAsync<T>(Func<IStoredProcedures, Task<T>> call) =>
		host.InRequestAsync(services =>
			call(services.GetRequiredService<IStoredProcedures>()));

	private Task SellAsync(
		ApexPost.Domain.ValueObjects.Sku sku,
		int quantity,
		bool withShippingAndMoneyOrder = false) =>
		host.InRequestAsync(services => host.Sales(services).CreateAndProcessAsync(
			ClerkId, Sale(sku, quantity, withShippingAndMoneyOrder)));

	private async Task<int> CountAsync(string table, Guid transactionId) =>
		(int)(await ScalarAsync(
			$"SELECT COUNT(*) FROM dbo.{table} WHERE TransactionId = '{transactionId}'"))!;

	private async Task WithConstraintAsync(
		string table,
		string name,
		string condition,
		Func<Task> work,
		bool nocheck = false)
	{
		await ExecuteAsync(
			$"ALTER TABLE dbo.{table} WITH {(nocheck ? "NOCHECK" : "CHECK")} ADD CONSTRAINT {name} CHECK ({condition})");
		try
		{
			await work();
		}
		finally
		{
			await ExecuteAsync($"ALTER TABLE dbo.{table} DROP CONSTRAINT {name}");
		}
	}

	private async Task<SqlConnection> OpenAsync()
	{
		var connection = new SqlConnection(host.ConnectionString);
		await connection.OpenAsync();
		return connection;
	}

	private async Task ExecuteAsync(string sql)
	{
		await using var connection = await OpenAsync();
		await using var command = new SqlCommand(sql, connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<object?> ScalarAsync(string sql)
	{
		await using var connection = await OpenAsync();
		await using var command = new SqlCommand(sql, connection);
		return await command.ExecuteScalarAsync();
	}

	private Task RunFileAsync(string folder, string file) =>
		ExecuteAsync(ReadScript(folder, file));

	private async Task<List<Dictionary<string, object>>> QueryFileAsync(
		string folder,
		string file)
	{
		await using var connection = await OpenAsync();
		await using var command = new SqlCommand(ReadScript(folder, file), connection);
		await using var reader = await command.ExecuteReaderAsync();
		var rows = new List<Dictionary<string, object>>();
		while (await reader.ReadAsync())
		{
			rows.Add(Enumerable.Range(0, reader.FieldCount)
				.ToDictionary(reader.GetName, reader.GetValue));
		}

		// Errors raised after the first result set surface here.
		while (await reader.NextResultAsync())
		{
		}

		return rows;
	}

	private async Task<List<string>> CaptureCommandsAsync(
		Func<Task> work,
		bool includeParameters = false)
	{
		var commands = new List<string>();
		var database = new SqlConnectionStringBuilder(host.ConnectionString).InitialCatalog;
		using var listener = new CommandListener(commands, database, includeParameters);
		using var subscription = DiagnosticListener.AllListeners.Subscribe(listener);
		await work();
		return commands;
	}

	private sealed class CommandListener(
		List<string> commands,
		string database,
		bool includeParameters)
		: IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
	{
		private readonly List<IDisposable> _subscriptions = [];

		public void OnNext(DiagnosticListener value)
		{
			if (value.Name == DbLoggerCategory.Name)
			{
				lock (_subscriptions)
					_subscriptions.Add(value.Subscribe(this));
			}
		}

		public void OnNext(KeyValuePair<string, object?> value)
		{
			if (value.Key == RelationalEventId.CommandExecuting.Name &&
				value.Value is CommandEventData data &&
				data.Command.Connection?.Database == database)
			{
				lock (commands)
					commands.Add(Describe(data.Command));
			}
		}

		private string Describe(System.Data.Common.DbCommand command) => includeParameters
			? string.Join(' ', command.Parameters.Cast<SqlParameter>().Select(parameter => parameter.Value)) + command.CommandText
			: command.CommandText;

		public void OnCompleted()
		{
		}

		public void OnError(Exception error)
		{
		}

		public void Dispose()
		{
			lock (_subscriptions)
				_subscriptions.ForEach(subscription => subscription.Dispose());
		}
	}
}
