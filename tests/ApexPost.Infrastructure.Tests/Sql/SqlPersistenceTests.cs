using ApexPost.Application.Auditing;
using ApexPost.Application.Caching;
using ApexPost.Application.Persistence;
using ApexPost.Application.Security;
using ApexPost.Application.Services;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using ApexPost.Infrastructure.Persistence;
using ApexPost.Infrastructure.Persistence.Sql;
using ApexPost.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using static ApexPost.Infrastructure.Tests.Sql.SqlTestHost;

namespace ApexPost.Infrastructure.Tests.Sql;

[Collection(SqlCollection.Name)]
public sealed class SqlPersistenceTests(SqlTestHost host) : IAsyncLifetime
{
	public Task InitializeAsync() => host.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task Sale_SavesEveryPartTogether()
	{
		var request = Sale(Stationery, 3, withShippingAndMoneyOrder: true);

		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		await host.QueryAsync(async context =>
		{
			Assert.Equal(1, await context.CounterTransactions.CountAsync());
			Assert.Equal(4, await context.TransactionLines.CountAsync());
			Assert.Equal(1, await context.Shipments.CountAsync());
			Assert.Equal(1, await context.MoneyOrders.CountAsync());
			Assert.Equal(7, (await context.BranchStock
				.SingleAsync(stock => stock.ProductSku == Stationery)).Quantity);
			var payment = await context.Payments.SingleAsync();
			Assert.Equal(PaymentEntryType.Payment, payment.Type);
			Assert.Equal(PaymentMethod, payment.PaymentMethod);
			Assert.Equal(request.TransactionId, payment.TransactionId);
			return true;
		});
	}

	[Fact]
	public async Task Sale_StoresOnlyTheFixedDocumentMask()
	{
		await host.InRequestAsync(services => host.Sales(services)
			.CreateAndProcessAsync(ClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true)));

		var record = await host.QueryAsync(context =>
			context.IdentityVerifications.SingleAsync());

		Assert.Equal("****1234", record.MaskedDocumentIdentifier);
	}

	[Fact]
	public async Task FailedSale_RollsBackEverything()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			host.InRequestAsync(services => host.Sales(services)
				.CreateAndProcessAsync(ClerkId, Sale(Stationery, 999, withShippingAndMoneyOrder: true))));

		await host.QueryAsync(async context =>
		{
			Assert.Equal(0, await context.CounterTransactions.CountAsync());
			Assert.Equal(0, await context.TransactionLines.CountAsync());
			Assert.Equal(0, await context.Payments.CountAsync());
			Assert.Equal(0, await context.MoneyOrders.CountAsync());
			Assert.Equal(10, (await context.BranchStock
				.SingleAsync(stock => stock.ProductSku == Stationery)).Quantity);
			return true;
		});
	}

	[Fact]
	public async Task RepeatedTransactionId_IsRejectedWithoutASecondSale()
	{
		var request = Sale(Packaging, 1);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			host.InRequestAsync(services =>
				host.Sales(services).CreateAndProcessAsync(ClerkId, request)));

		Assert.Equal(1, await host.QueryAsync(context =>
			context.CounterTransactions.CountAsync()));
	}

	[Fact]
	public async Task LoadedTransaction_RestoresItsWholeAggregate()
	{
		var request = Sale(Stationery, 2, withShippingAndMoneyOrder: true);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		var basket = await host.InRequestAsync(services => host.Transactions(services)
			.GetTransactionBasketAsync(request.TransactionId, ManagerId));

		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(SqlTestHost.Branch, basket.BranchCode);
		Assert.Equal(new CounterSaleDetails("C1", ClerkId, PaymentMethod), basket.SaleDetails);
		Assert.Equal(0.18m, basket.TaxRates[TaxCategory.Standard]);
		var order = basket.LineItems.Single(line => line.MoneyOrder is not null).MoneyOrder!;
		Assert.Equal("****1234", order.IdentityVerification?.MaskedDocumentIdentifier);
		var shipment = basket.LineItems.Single(line => line.ShippingItem is not null);
		Assert.Equal(1.255m, shipment.ShippingItem!.Weight.Kilograms);
		Assert.Equal(new Dimensions(10m, 20m, 30m), shipment.ShippingItem.Dimensions);
	}

	[Fact]
	public async Task StoredDecimals_ReadBackWithoutTrailingZeros()
	{
		var product = await host.QueryAsync(context =>
			context.Products.SingleAsync(entry => entry.Sku == Packaging));

		Assert.Equal("10", product.UnitPrice.Amount.ToString(
			System.Globalization.CultureInfo.InvariantCulture));
	}

	[Fact]
	public async Task MoneyAboveTwoDecimals_IsRoundedAtTheColumn()
	{
		var request = Sale(Stationery, 1, withShippingAndMoneyOrder: true);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		var shipping = await host.QueryAsync(context => context.TransactionLines
			.Where(line => line.ShippingItem != null)
			.Select(line => line.UnitPrice.Amount)
			.SingleAsync());

		Assert.Equal(11.26m, shipping);
	}

	// The sale from ADR 0017: reloading its rounded shipping line gives a total
	// that rounds differently, so copying the stored payment is what makes these equal.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Reversal_OfASaleWithShipping_RefundsExactlyTheStoredPayment(bool voided)
	{
		var request = Sale(Stationery, 3, withShippingAndMoneyOrder: true);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));
		var reloaded = await host.InRequestAsync(services => host.Transactions(services)
			.GetTransactionBasketAsync(request.TransactionId, ManagerId));

		await host.InRequestAsync(services => voided
			? host.Transactions(services).VoidAsync(request.TransactionId, ManagerId, "Mistake")
			: host.Transactions(services).RefundAsync(request.TransactionId, ManagerId, "Damaged"));

		var entries = await host.QueryAsync(context => context.Payments
			.Select(payment => new { payment.Type, payment.Amount.Amount })
			.ToArrayAsync());
		var paid = entries.Single(entry => entry.Type == PaymentEntryType.Payment).Amount;
		var refunded = entries.Single(entry => entry.Type == PaymentEntryType.Refund).Amount;
		Assert.Equal(paid, refunded);
		Assert.NotEqual(
			paid, Math.Round(reloaded.Total.Amount, 2, MidpointRounding.AwayFromZero));
	}

	[Fact]
	public async Task FindPayment_ReturnsThePaymentEntryNotItsRefund()
	{
		var request = Sale(Stationery, 1);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));
		await host.InRequestAsync(services => host.Transactions(services)
			.RefundAsync(request.TransactionId, ManagerId, "Damaged"));

		var (found, missing) = await host.InRequestAsync(async services =>
		{
			var repository = services.GetRequiredService<ICounterTransactionRepository>();
			return (
				await repository.FindPaymentAsync(request.TransactionId),
				await repository.FindPaymentAsync(Guid.NewGuid()));
		});

		Assert.Equal(PaymentEntryType.Payment, found?.Type);
		Assert.Null(missing);
	}

	[Fact]
	public async Task Void_ReturnsStockCancelsMoneyOrderAndKeepsTheReason()
	{
		var request = Sale(Stationery, 3, withShippingAndMoneyOrder: true);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		await host.InRequestAsync(services => host.Transactions(services)
			.VoidAsync(request.TransactionId, ManagerId, "Entered in error"));

		var basket = await host.InRequestAsync(services => host.Transactions(services)
			.GetTransactionBasketAsync(request.TransactionId, ManagerId));
		Assert.Equal(TransactionPaymentStatus.Voided, basket.PaymentStatus);
		Assert.Single(basket.Notifications);
		await host.QueryAsync(async context =>
		{
			Assert.Equal(10, (await context.BranchStock
				.SingleAsync(stock => stock.ProductSku == Stationery)).Quantity);
			Assert.Equal(MoneyOrderStatus.Cancelled, (await context.MoneyOrders.SingleAsync()).Status);
			return true;
		});
	}

	[Fact]
	public async Task TwoClerks_SecondWithStaleStockGetsAConcurrencyException()
	{
		await using var firstScope = host.CreateScope();
		await using var secondScope = host.CreateScope();
		var first = firstScope.ServiceProvider.GetRequiredService<ApexPostDbContext>();
		var second = secondScope.ServiceProvider.GetRequiredService<ApexPostDbContext>();
		var firstStock = await first.BranchStock.SingleAsync(stock => stock.ProductSku == Packaging);
		var secondStock = await second.BranchStock.SingleAsync(stock => stock.ProductSku == Packaging);

		firstStock.RemoveStock(10);
		await first.SaveChangesAsync();
		secondStock.RemoveStock(10);

		await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
		Assert.Equal(0, (await host.QueryAsync(context => context.BranchStock
			.SingleAsync(stock => stock.ProductSku == Packaging))).Quantity);
	}

	[Fact]
	public async Task Retry_ReloadsCurrentStateAndRunsTheFollowOnOnce()
	{
		var quantities = new List<int>();
		var followOn = 0;

		await host.InRequestAsync(async services =>
		{
			var unitOfWork = services.GetRequiredService<IUnitOfWork>();
			var stocks = services.GetRequiredService<IStockRepository>();
			await unitOfWork.ExecuteInTransactionAsync(async token =>
			{
				var stock = (await stocks.FindAsync(SqlTestHost.Branch, Packaging, token))!;
				quantities.Add(stock.Quantity);
				if (quantities.Count == 1)
					await ChangeStockElsewhereAsync(-1);
				stock.RemoveStock(1);
				await unitOfWork.SaveChangesAsync(token);
			});
			followOn++;
		});

		Assert.Equal([10, 9], quantities);
		Assert.Equal(1, followOn);
		Assert.Equal(8, (await host.QueryAsync(context => context.BranchStock
			.SingleAsync(stock => stock.ProductSku == Packaging))).Quantity);
	}

	[Fact]
	public async Task RepeatedConflicts_EndWithConcurrencyConflictException()
	{
		var attempts = 0;

		await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
			host.InRequestAsync(async services =>
			{
				var unitOfWork = services.GetRequiredService<IUnitOfWork>();
				var stocks = services.GetRequiredService<IStockRepository>();
				await unitOfWork.ExecuteInTransactionAsync(async token =>
				{
					attempts++;
					var stock = (await stocks.FindAsync(SqlTestHost.Branch, Packaging, token))!;
					await ChangeStockElsewhereAsync(1);
					stock.AddStock(1);
					await unitOfWork.SaveChangesAsync(token);
				});
			}));

		Assert.Equal(5, attempts);
	}

	[Fact]
	public async Task ConcurrentSales_NeverOversellAndPublishOneEventPerSale()
	{
		var published = 0;
		host.Events.TransactionCompleted += (_, _) => Interlocked.Increment(ref published);

		var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
		{
			try
			{
				await host.InRequestAsync(services => host.Sales(services)
					.CreateAndProcessAsync(ClerkId, Sale(Packaging, 1)));
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}));

		var sold = results.Count(result => result);
		var remaining = (await host.QueryAsync(context => context.BranchStock
			.SingleAsync(stock => stock.ProductSku == Packaging))).Quantity;
		Assert.True(remaining >= 0);
		Assert.Equal(10 - remaining, sold);
		Assert.Equal(sold, published);
		Assert.Equal(sold, await host.QueryAsync(context =>
			context.CounterTransactions.CountAsync()));
	}

	[Fact]
	public async Task FailedUseCase_LeavesNothingTracked_SuccessKeepsItsEntities()
	{
		await host.InRequestAsync(async services =>
		{
			var context = services.GetRequiredService<ApexPostDbContext>();
			var unitOfWork = services.GetRequiredService<IUnitOfWork>();
			var stocks = services.GetRequiredService<IStockRepository>();

			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				unitOfWork.ExecuteInTransactionAsync(async token =>
				{
					var stock = (await stocks.FindAsync(SqlTestHost.Branch, Packaging, token))!;
					stock.RemoveStock(4);
					throw new InvalidOperationException("failed after a change");
				}));

			Assert.Empty(context.ChangeTracker.Entries());
			Assert.Equal(10, (await stocks.FindAsync(SqlTestHost.Branch, Packaging))!.Quantity);

			await unitOfWork.ExecuteInTransactionAsync(async token =>
			{
				var stock = (await stocks.FindAsync(SqlTestHost.Branch, Packaging, token))!;
				stock.RemoveStock(1);
				await unitOfWork.SaveChangesAsync(token);
			});
			Assert.NotEmpty(context.ChangeTracker.Entries());
		});
	}

	[Fact]
	public async Task DuplicateBusinessKey_BecomesDuplicateRecordException()
	{
		await Assert.ThrowsAsync<DuplicateRecordException>(() =>
			host.InRequestAsync(async services =>
			{
				var unitOfWork = services.GetRequiredService<IUnitOfWork>();
				var products = services.GetRequiredService<IProductRepository>();
				await unitOfWork.ExecuteInTransactionAsync(async token =>
				{
					products.Add(new Product(
						Packaging, "Copy", ProductCategory.Packaging, new Money(1m, "INR"), TaxCategory.ZeroRated));
					await unitOfWork.SaveChangesAsync(token);
				});
			}));
	}

	[Fact]
	public async Task AuditColumns_AreStampedOnInsertAndUpdate()
	{
		var request = Sale(Packaging, 1);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		await host.QueryAsync(async context =>
		{
			Assert.Equal("stamped-staff", await context.CounterTransactions
				.Select(sale => EF.Property<string>(sale, "CreatedBy"))
				.SingleAsync());
			Assert.NotNull(await context.BranchStock
				.Where(stock => stock.ProductSku == Packaging)
				.Select(stock => EF.Property<DateTime?>(stock, "ModifiedAtUtc"))
				.SingleAsync());
			return true;
		});
	}

	[Fact]
	public async Task StaffLookup_IsCaseSensitive_AndEmailIsNot()
	{
		await host.InRequestAsync(async services =>
		{
			var staff = services.GetRequiredService<IStaffRepository>();
			Assert.NotNull(await staff.FindByIdAsync(ManagerId));
			Assert.Null(await staff.FindByIdAsync(ManagerId.ToUpperInvariant()));
			Assert.True(await staff.EmailExistsAsync("MANAGER@EXAMPLE.INVALID"));
		});
	}

	[Fact]
	public async Task Queries_SendCodesAsVarchar()
	{
		var sku = new Sku(Packaging.Value);
		var sql = await host.QueryAsync(context => Task.FromResult(
			context.BranchStock
				.Where(stock => stock.ProductSku == sku)
				.ToQueryString()));

		Assert.Contains("varchar(30)", sql, StringComparison.Ordinal);
		Assert.DoesNotContain("nvarchar", sql, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RepositoryReads_FindShipmentsMoneyOrdersAndParcels()
	{
		var request = Sale(Stationery, 1, withShippingAndMoneyOrder: true);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		await host.InRequestAsync(async services =>
		{
			var transactions = services.GetRequiredService<ICounterTransactionRepository>();
			var shipment = Assert.Single(await transactions.GetShipmentsAsync());
			var order = Assert.Single(await transactions.GetMoneyOrdersAsync());
			var parcel = await transactions.FindByShipmentIdAsync(shipment.Id);
			Assert.Equal("****1234", order.IdentityVerification?.MaskedDocumentIdentifier);
			Assert.Equal(request.TransactionId, parcel?.Id);
			Assert.Null(await transactions.FindByShipmentIdAsync("missing"));
		});
	}

	[Fact]
	public async Task RevisePrices_ChangesOnlyTheCategoryThroughExecuteUpdate()
	{
		var changed = await host.InRequestAsync(services =>
			new AdministrationApplicationService(
				services.GetRequiredService<IStaffRepository>(),
				services.GetRequiredService<IBranchRepository>(),
				services.GetRequiredService<IProductRepository>(),
				services.GetRequiredService<IUnitOfWork>())
			.RevisePricesAsync(AdministratorId, ProductCategory.Stationery, 1.1m));

		Assert.Equal(1, changed);
		await host.QueryAsync(async context =>
		{
			Assert.Equal(36.66m, (await context.Products
				.SingleAsync(product => product.Sku == Stationery)).UnitPrice.Amount);
			Assert.Equal(10m, (await context.Products
				.SingleAsync(product => product.Sku == Packaging)).UnitPrice.Amount);
			return true;
		});
	}

	[Fact]
	public async Task AuditStore_CommitsInItsOwnScope()
	{
		await host.InRequestAsync(async services =>
		{
			var context = services.GetRequiredService<ApexPostDbContext>();
			var stocks = services.GetRequiredService<IStockRepository>();
			var stock = await stocks.FindAsync(SqlTestHost.Branch, Packaging);
			stock!.RemoveStock(1);

			await services.GetRequiredService<IAuditStore>().AppendAsync(new AuditRecord(
				DateTimeOffset.UtcNow,
				"Transactions",
				"Create",
				"Executed",
				"Rejected",
				409,
				1.5,
				new Dictionary<string, string> { ["id"] = "1" },
				ManagerId,
				"BranchManager",
				"BR01",
				"correlation"));

			// The request's unsaved change is untouched by the audit commit.
			Assert.Contains(
				context.ChangeTracker.Entries(),
				entry => entry.State == EntityState.Modified);
		});

		await host.QueryAsync(async context =>
		{
			var entry = await context.AuditEntries.SingleAsync();
			Assert.Equal(409, entry.StatusCode);
			Assert.Equal("1", entry.RouteValues["id"]);
			Assert.Equal(10, (await context.BranchStock
				.SingleAsync(stock => stock.ProductSku == Packaging)).Quantity);
			return true;
		});
	}

	[Fact]
	public async Task Credentials_AddOnlyMissingStaff_AndNeverReplace()
	{
		var credential = new StaffCredential(ManagerId, 1, 600000, "c2FsdA==", "aGFzaA==");

		var (first, second, stored) = await host.InRequestAsync(async services =>
		{
			var provisioning = services.GetRequiredService<IStaffCredentialProvisioning>();
			var added = await provisioning.AddMissingAsync([credential]);
			var repeated = await provisioning.AddMissingAsync(
				[credential with { PasswordHash = "b3RoZXI=" }]);
			var found = await services.GetRequiredService<IStaffCredentialRepository>()
				.FindAsync(ManagerId);
			return (added, repeated, found);
		});

		Assert.Equal(1, first);
		Assert.Equal(0, second);
		Assert.Equal(credential, stored);
	}

	[Fact]
	public async Task ReferenceDataSource_LoadsOutsideAnyRequest()
	{
		await using var scope = host.CreateScope();
		var source = scope.ServiceProvider.GetRequiredService<IReferenceDataSource>();

		var (products, rates) = await source.LoadAsync();

		Assert.Equal(2, products.Count);
		Assert.Single(rates);
	}

	[Fact]
	public async Task DemoSeed_MeetsTheBriefsVolumes_AndDoesNotRunTwice()
	{
		await DeleteSeededRowsAsync();

		var first = await host.InRequestAsync(services =>
			services.GetRequiredService<SqlDemoDataSeeder>().InitializeAsync());
		var second = await host.InRequestAsync(services =>
			services.GetRequiredService<SqlDemoDataSeeder>().InitializeAsync());

		Assert.True(first);
		Assert.False(second);
		await host.QueryAsync(async context =>
		{
			Assert.Equal(5, await context.Branches.CountAsync());
			Assert.Equal(20, await context.Staff.CountAsync());
			Assert.Equal(30, await context.Products.CountAsync());
			Assert.Equal(10, await context.ShippingZones.CountAsync());
			Assert.Equal(16, await context.ShippingRates.CountAsync());
			Assert.True(await context.CounterTransactions.CountAsync() >= 200);
			Assert.Equal(
				await context.CounterTransactions.CountAsync(),
				await context.Payments.CountAsync());
			Assert.False(await context.BranchStock.AnyAsync(stock => stock.Quantity < 0));
			return true;
		});
	}

	[Fact]
	public async Task DemoSeed_CreatesShipmentsAcrossZonesServiceLevelsAndTypes()
	{
		await SeedDemoAsync();

		await host.QueryAsync(async context =>
		{
			var shipments = await context.Shipments.AsNoTracking().ToArrayAsync();
			Assert.Equal(40, shipments.Length);
			Assert.Equal(10, shipments.Select(entry => entry.DestinationZone).Distinct().Count());
			Assert.Equal(
				Enum.GetValues<ShippingServiceLevel>().Length,
				shipments.Select(entry => entry.ServiceLevel).Distinct().Count());
			Assert.Equal(
				Enum.GetValues<ShippingItemType>().Length,
				shipments.Select(entry => entry.Type).Distinct().Count());
			return true;
		});
	}

	[Fact]
	public async Task DemoSeed_CreatesMoneyOrdersAtEveryBranchWithinTheVerificationRule()
	{
		await SeedDemoAsync();

		await host.QueryAsync(async context =>
		{
			var orders = await context.MoneyOrders.AsNoTracking().ToArrayAsync();
			Assert.Equal(20, orders.Length);
			Assert.Equal(
				5, orders.Select(entry => entry.ProcessingBranchCode).Distinct().Count());
			Assert.All(orders, entry =>
			{
				Assert.StartsWith("DEMO Sender ", entry.Sender, StringComparison.Ordinal);
				Assert.StartsWith("DEMO Recipient ", entry.Recipient, StringComparison.Ordinal);
			});
			Assert.Equal(0, await ScalarAsync(context, $"""
				SELECT COUNT(*) AS Value
				FROM dbo.MoneyOrders AS o
				LEFT JOIN dbo.IdentityVerifications AS v ON v.MoneyOrderId = o.MoneyOrderId
				WHERE o.Amount > 10000 AND ISNULL(v.IsSuccessful, 0) = 0
				"""));
			return true;
		});
	}

	[Fact]
	public async Task DemoSeed_RunningAgain_DuplicatesNothing()
	{
		await SeedDemoAsync();
		var before = await CountDemoRowsAsync();

		var second = await host.InRequestAsync(services =>
			services.GetRequiredService<SqlDemoDataSeeder>().InitializeAsync());

		Assert.False(second);
		Assert.Equal(before, await CountDemoRowsAsync());
		Assert.Equal((270, 40, 20, 15, 270), before);
	}

	[Fact]
	public async Task DemoSeed_ShippingVolumeReport_CountsEverySeededShipment()
	{
		await SeedDemoAsync();

		var rows = await host.InRequestAsync(services =>
			services.GetRequiredService<IReportQueryService>().GetShippingVolumeAsync());

		Assert.Equal(16, rows.Count);
		Assert.Equal(40, rows.Sum(row => row.ShipmentCount));
		Assert.Contains(rows, row => row.ServiceLevel == ShippingServiceLevel.Express);
		Assert.Contains(rows, row => row.ServiceLevel == ShippingServiceLevel.Registered);
	}

	[Fact]
	public async Task DemoSeed_BranchAuditorReport_CountsEverySeededMoneyOrder()
	{
		await SeedDemoAsync();

		var rows = await host.InRequestAsync(services =>
			services.GetRequiredService<IReportQueryService>().GetBranchAuditorReportAsync());

		Assert.Equal(5, rows.Count);
		Assert.Equal(20, rows.Sum(row => row.MoneyOrderCount));
		Assert.Equal(15, rows.Sum(row => row.IdentityVerificationCount));
		Assert.Equal(10, rows.Sum(row => row.SuccessfulIdentityVerificationCount));
		Assert.Equal(5, rows.Sum(row => row.FailedIdentityVerificationCount));
	}

	[Fact]
	public async Task DemoSeed_StoresOnlyMaskedDocumentIdentifiers()
	{
		await SeedDemoAsync();

		await host.QueryAsync(async context =>
		{
			var identifiers = await context.Database
				.SqlQuery<string>(
					$"SELECT MaskedDocumentIdentifier AS Value FROM dbo.IdentityVerifications")
				.ToArrayAsync();
			Assert.Equal(15, identifiers.Length);
			Assert.All(identifiers, identifier => Assert.Matches(@"\A\*{4}[0-9]{4}\z", identifier));
			Assert.Equal(0, await ScalarAsync(context, $"""
				SELECT COUNT(*) AS Value
				FROM dbo.TransactionLines
				WHERE Description LIKE '%DEMO00%'
				"""));
			return true;
		});
	}

	[Fact]
	public async Task DemoSeed_RecordsReferenceValidBranchesAndStaff()
	{
		await SeedDemoAsync();

		await host.QueryAsync(async context =>
		{
			Assert.Equal(0, await ScalarAsync(context, $"""
				SELECT COUNT(*) AS Value
				FROM dbo.CounterTransactions AS t
				LEFT JOIN dbo.Staff AS s ON s.StaffId = t.ClerkStaffId
				WHERE s.StaffId IS NULL OR s.HomeBranchCode <> t.BranchCode
				"""));
			Assert.Equal(0, await ScalarAsync(context, $"""
				SELECT COUNT(*) AS Value
				FROM dbo.MoneyOrders AS o
				INNER JOIN dbo.TransactionLines AS l ON l.MoneyOrderId = o.MoneyOrderId
				INNER JOIN dbo.CounterTransactions AS t ON t.TransactionId = l.TransactionId
				WHERE t.BranchCode <> o.ProcessingBranchCode
				"""));
			Assert.Equal(40, await ScalarAsync(context, $"""
				SELECT COUNT(*) AS Value
				FROM dbo.Shipments AS s
				INNER JOIN dbo.TransactionLines AS l ON l.ShipmentId = s.ShipmentId
				INNER JOIN dbo.ShippingRates AS r
					ON r.ZoneCode = s.ZoneCode AND r.ServiceLevel = s.ServiceLevel
				"""));
			return true;
		});
	}

	private async Task SeedDemoAsync()
	{
		await DeleteSeededRowsAsync();
		Assert.True(await host.InRequestAsync(services =>
			services.GetRequiredService<SqlDemoDataSeeder>().InitializeAsync()));
	}

	private Task<(int Transactions, int Shipments, int MoneyOrders, int Verifications, int Payments)>
		CountDemoRowsAsync() =>
		host.QueryAsync(async context => (
			await context.CounterTransactions.CountAsync(),
			await context.Shipments.CountAsync(),
			await context.MoneyOrders.CountAsync(),
			await context.IdentityVerifications.CountAsync(),
			await context.Payments.CountAsync()));

	private static Task<int> ScalarAsync(ApexPostDbContext context, FormattableString sql) =>
		context.Database.SqlQuery<int>(sql).SingleAsync();

	[Fact]
	public async Task DemoCredentials_ProvisionEachDemoStaffMemberOnce()
	{
		await DeleteSeededRowsAsync();
		await host.InRequestAsync(services =>
			services.GetRequiredService<SqlDemoDataSeeder>().InitializeAsync());

		var added = await host.InRequestAsync(services =>
			services.GetRequiredService<DemoCredentialProvisioner>().ProvisionAsync("test-password"));
		var again = await host.InRequestAsync(services =>
			services.GetRequiredService<DemoCredentialProvisioner>().ProvisionAsync("test-password"));

		Assert.Equal(20, added);
		Assert.Equal(0, again);
	}

	[Fact]
	public async Task Model_HasNoPendingChanges_AndInitialCreateIsApplied()
	{
		await host.QueryAsync(async context =>
		{
			Assert.False(context.Database.HasPendingModelChanges());
			Assert.Empty(await context.Database.GetPendingMigrationsAsync());
			Assert.Contains(
				await context.Database.GetAppliedMigrationsAsync(),
				migration => migration.EndsWith("_InitialCreate", StringComparison.Ordinal));
			return true;
		});
	}

	[Fact]
	public async Task Migrations_RevertToNothingAndReapply()
	{
		using var database = DisposableSqlDatabase.Create();
		await using var context = database.CreateContext();
		var migrator = context.Database.GetService<IMigrator>();

		await migrator.MigrateAsync(Migration.InitialDatabase);
		Assert.Equal(1, await CountTablesAsync(context));

		await migrator.MigrateAsync();
		Assert.Equal(20, await CountTablesAsync(context));
	}

	[Theory]
	[InlineData("UPDATE BranchStock SET Quantity = -1", "CK_BranchStock_Quantity")]
	[InlineData("UPDATE Shipments SET WeightKg = 0", "CK_Shipments_Weight")]
	[InlineData("UPDATE Products SET UnitPrice = -1", "CK_Products_UnitPrice")]
	[InlineData("INSERT Staff (StaffId, Name, Email, RoleId, HomeBranchCode) VALUES ('x', 'x', 'CLERK@EXAMPLE.INVALID', 0, 'BR01')", "UQ_Staff_Email")]
	[InlineData("INSERT Branches (BranchCode, Name, Region, OpeningHours) VALUES ('BR01', 'x', 'x', 'x')", "UQ_Branches_BranchCode")]
	[InlineData("INSERT Products (Sku, Name, ProductCategoryId, TaxCategory, UnitPrice, Currency) VALUES ('PACK-1', 'x', 0, 'Standard', 1, 'INR')", "UQ_Products_Sku")]
	[InlineData("DELETE FROM Branches", "REFERENCE constraint")]
	[InlineData("INSERT CounterTransactions (TransactionId, Currency, OccurredAtUtc, BranchCode, Status) VALUES (NEWID(), 'INR', SYSUTCDATETIME(), 'BR01', 'Bogus')", "CK_CounterTransactions_")]
	[InlineData("INSERT CounterTransactions (TransactionId, Currency, OccurredAtUtc, BranchCode, Status, ReversalReason) VALUES (NEWID(), 'INR', SYSUTCDATETIME(), 'BR01', 'Paid', N'x')", "CK_CounterTransactions_ReversalReason")]
	[InlineData("INSERT CounterTransactions (TransactionId, Currency, OccurredAtUtc, BranchCode, CounterId, ClerkStaffId, PaymentMethod) VALUES (NEWID(), 'INR', SYSUTCDATETIME(), 'BR01', 'C2', 'clerk-1', N'Cash')", "FK_CounterTransactions_Counters_BranchCode_CounterId")]
	public async Task Database_RejectsInvalidRows(string sql, string constraint)
	{
		await host.InRequestAsync(services => host.Sales(services)
			.CreateAndProcessAsync(ClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true)));

		await using var connection = new SqlConnection(host.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		var exception = await Assert.ThrowsAsync<SqlException>(
			() => command.ExecuteNonQueryAsync());

		Assert.Contains(constraint, exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("ABCD1234")]
	[InlineData("***12345")]
	public async Task Database_RejectsAnUnmaskedDocument(string value)
	{
		await host.InRequestAsync(services => host.Sales(services)
			.CreateAndProcessAsync(ClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true)));

		await using var connection = new SqlConnection(host.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"UPDATE IdentityVerifications SET MaskedDocumentIdentifier = @value";
		command.Parameters.AddWithValue("@value", value);

		var exception = await Assert.ThrowsAsync<SqlException>(
			() => command.ExecuteNonQueryAsync());
		Assert.Contains(
			"CK_IdentityVerifications_MaskedDocumentIdentifier",
			exception.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task HealthCheck_ReportsHealthyForAReachableDatabase()
	{
		var check = new SqlServerConnectivityHealthCheck(host.ConnectionString);

		var result = await check.CheckHealthAsync(new HealthCheckContext
		{
			Registration = new HealthCheckRegistration(
				"sql-server", check, HealthStatus.Unhealthy, tags: null)
		});

		Assert.Equal(HealthStatus.Healthy, result.Status);
	}

	private Task ChangeStockElsewhereAsync(int delta) =>
		host.InRequestAsync(async services =>
		{
			var context = services.GetRequiredService<ApexPostDbContext>();
			var stock = await context.BranchStock.SingleAsync(entry => entry.ProductSku == Packaging);
			if (delta > 0)
				stock.AddStock(delta);
			else
				stock.RemoveStock(-delta);
			await context.SaveChangesAsync();
		});

	private Task DeleteSeededRowsAsync() =>
		host.QueryAsync(async context =>
		{
			await context.Database.ExecuteSqlRawAsync(
				"DELETE FROM BranchStock; DELETE FROM ShippingRates; DELETE FROM ShippingZones; " +
				"DELETE FROM Products; DELETE FROM Staff; DELETE FROM Counters; DELETE FROM Branches;");
			return true;
		});

	private static async Task<int> CountTablesAsync(ApexPostDbContext context)
	{
		var connection = context.Database.GetDbConnection();
		await context.Database.OpenConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM sys.tables";
		var count = (int)(await command.ExecuteScalarAsync())!;
		await context.Database.CloseConnectionAsync();
		return count;
	}
}
