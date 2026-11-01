using System.Globalization;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static ApexPost.Infrastructure.Tests.Sql.SqlTestHost;

namespace ApexPost.Infrastructure.Tests.Sql;

// Reads that no other test ran on SQL Server, and translation details that a
// fake cannot show: value-object filters, date bounds, ordering and parameters.
// The repositories and query services have no paging, so there is none to test.
[Collection(SqlCollection.Name)]
public sealed class RepositoryTranslationTests(SqlTestHost host) : IAsyncLifetime
{
	private const string OtherClerkId = "clerk-2";
	private static readonly DateOnly SaleDate = new(2026, 3, 10);

	public Task InitializeAsync() => host.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	// A multiplier sent with the column's type would be rounded: 1.125 to 1.13
	// gave 37.66, and 0.975 to 0.98 gave 32.66.
	[Theory]
	[InlineData("1.125", "37.50")]
	[InlineData("0.975", "32.50")]
	[InlineData("1.05", "35.00")]
	[InlineData("2", "66.66")]
	[InlineData("1.000100", "33.33")]
	public async Task RevisePrices_UsesEveryDecimalOfTheMultiplier(
		string multiplier,
		string expected)
	{
		var rows = await host.InRequestAsync(services => services
			.GetRequiredService<IProductRepository>()
			.RevisePricesAsync(
				ProductCategory.Stationery,
				decimal.Parse(multiplier, CultureInfo.InvariantCulture)));

		var price = await host.QueryAsync(context => context.Products
			.AsNoTracking()
			.Where(product => product.Sku == Stationery)
			.Select(product => product.UnitPrice.Amount)
			.SingleAsync());

		Assert.Equal(1, rows);
		Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), price);
	}

	[Fact]
	public async Task GetForProducts_FiltersByValueObjectList_InSkuOrder()
	{
		await host.InRequestAsync(async services =>
		{
			var stock = services.GetRequiredService<IStockRepository>();

			var rows = await stock.GetForProductsAsync(
				SqlTestHost.Branch, [Stationery, Packaging, new Sku("MISSING")]);

			Assert.Equal([Packaging, Stationery], rows.Select(row => row.ProductSku));
			Assert.Empty(await stock.GetForProductsAsync(OtherBranch, [Stationery]));
			Assert.Empty(await stock.GetForProductsAsync(SqlTestHost.Branch, []));
		});
	}

	[Fact]
	public async Task FindBySkus_ReturnsOnlyTheNamedProducts()
	{
		await host.InRequestAsync(async services =>
		{
			var products = services.GetRequiredService<IProductRepository>();

			var found = await products.FindBySkusAsync([Packaging, new Sku("MISSING")]);

			Assert.Equal(Packaging, Assert.Single(found).Sku);
			Assert.Empty(await products.FindBySkusAsync([]));
		});
	}

	[Fact]
	public async Task GetBranches_RestoresCodesAndCounters_WithoutTracking()
	{
		await host.InRequestAsync(async services =>
		{
			var branches = await services.GetRequiredService<IBranchRepository>()
				.GetBranchesAsync();

			Assert.Equal(
				[(SqlTestHost.Branch, 1), (OtherBranch, 2)],
				branches
					.Select(branch => (branch.Code, branch.CounterTerminals.Count))
					.OrderBy(row => row.Code.Value));
			Assert.Empty(services.GetRequiredService<ApexPostDbContext>().ChangeTracker.Entries());
		});
	}

	[Fact]
	public async Task GetMoneyOrdersForBranch_ReturnsOnlyThatBranch_WithVerifications()
	{
		await AddOtherBranchClerkAsync();
		await host.InRequestAsync(services => host.Sales(services).CreateAndProcessAsync(
			ClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true)));
		await host.InRequestAsync(services => host.Sales(services).CreateAndProcessAsync(
			OtherClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true)));

		var orders = await host.InRequestAsync(services => services
			.GetRequiredService<ICounterTransactionRepository>()
			.GetMoneyOrdersForBranchAsync(OtherBranch));

		var order = Assert.Single(orders);
		Assert.Equal(OtherBranch, order.ProcessingBranchCode);
		Assert.Equal("****1234", order.IdentityVerification?.MaskedDocumentIdentifier);
	}

	[Fact]
	public async Task GetPaidSaleLines_KeepsTheWholeUtcDay_NewestFirst_PaidOnly()
	{
		var start = SaleDate.ToDateTime(TimeOnly.MinValue);
		var first = await SellAtAsync(Stationery, start);
		var last = await SellAtAsync(Packaging, start.AddDays(1).AddMilliseconds(-1));
		await SellAtAsync(Packaging, start.AddDays(1));
		await SellAtAsync(Packaging, start.AddMilliseconds(-1));
		var voided = await SellAtAsync(Packaging, start.AddHours(9));
		await host.InRequestAsync(services =>
			host.Transactions(services).VoidAsync(voided, ManagerId, "Mistake"));

		var lines = await PaidSaleLinesAsync(SqlTestHost.Branch, SaleDate);

		Assert.Equal([last, first], lines.Select(line => line.TransactionId));
		Assert.Equal([0m, 0.18m], lines.Select(line => line.TaxRate));
		Assert.Empty(await PaidSaleLinesAsync(OtherBranch, SaleDate));
	}

	[Fact]
	public async Task GetPaidSaleLines_AcceptsTheLastSupportedDate()
	{
		await SellAtAsync(Packaging, SaleDate.ToDateTime(TimeOnly.MinValue));

		Assert.Empty(await PaidSaleLinesAsync(SqlTestHost.Branch, DateOnly.MaxValue));
	}

	[Fact]
	public async Task FindDashboardBranch_CountsAllAndActiveCounters()
	{
		await host.QueryAsync(async context =>
		{
			var branch = await context.Branches
				.Include(entry => entry.CounterTerminals)
				.SingleAsync(entry => entry.Code == OtherBranch);
			branch.CounterTerminals.First().Deactivate();
			return await context.SaveChangesAsync();
		});

		var row = await host.InRequestAsync(services => services
			.GetRequiredService<IScreenQueryService>()
			.FindDashboardBranchAsync(OtherBranch));

		Assert.Equal(
			new DashboardBranch(OtherBranch.Value, "Two", "South", "9-5", true, 2, 1),
			row);
		Assert.Null(await host.InRequestAsync(services => services
			.GetRequiredService<IScreenQueryService>()
			.FindDashboardBranchAsync(new BranchCode("NONE"))));
	}

	[Fact]
	public async Task AdministrationLists_ProjectInTheirDocumentedOrder()
	{
		await host.InRequestAsync(async services =>
		{
			var screens = services.GetRequiredService<IScreenQueryService>();

			Assert.Equal(
				[
					new AdministrationBranchRow("BR01", "One", "North", true),
					new AdministrationBranchRow("BR02", "Two", "South", true)
				],
				await screens.GetBranchesAsync());
			Assert.Equal(
				[AdministratorId, ClerkId, ManagerId],
				(await screens.GetStaffAsync()).Select(row => row.Id));
			Assert.Equal(
				new AdministrationStaffRow(ClerkId, "Clerk", StaffRole.CounterClerk, "BR01", true),
				(await screens.GetStaffAsync()).Single(row => row.Id == ClerkId));
			Assert.Equal(
				[
					new AdministrationProductRow("STAT-1", "Pen", ProductCategory.Stationery, 33.33m, "INR"),
					new AdministrationProductRow("PACK-1", "Box", ProductCategory.Packaging, 10m, "INR")
				],
				await screens.GetProductsAsync());
		});
	}

	[Fact]
	public async Task GetStock_FiltersByBranch_InSkuOrder()
	{
		var rows = await host.InRequestAsync(services => services
			.GetRequiredService<IScreenQueryService>()
			.GetStockAsync(SqlTestHost.Branch));

		Assert.Equal(
			[new StockRow(Packaging.Value, 10), new StockRow(Stationery.Value, 10)],
			rows);
	}

	[Fact]
	public async Task Queries_SendBranchCodesAndSkuListsAsVarchar()
	{
		var branch = new BranchCode(SqlTestHost.Branch.Value);
		Sku[] skus = [Packaging];

		var sql = await host.QueryAsync(context => Task.FromResult(
			context.BranchStock
				.Where(stock => stock.BranchCode == branch && skus.Contains(stock.ProductSku))
				.ToQueryString()));

		Assert.Contains("varchar(10)", sql, StringComparison.Ordinal);
		Assert.Contains("varchar(30)", sql, StringComparison.Ordinal);
		Assert.DoesNotContain("nvarchar", sql, StringComparison.Ordinal);
	}

	// SQL Server returns these rows in key order even with no ORDER BY, so the
	// rows alone cannot show that the query asks for an order.
	[Fact]
	public async Task OrderedReads_SendTheirOrderToSqlServer()
	{
		var stockRead = await CaptureSqlAsync(context =>
			new SqlStockRepository(context).GetForProductsAsync(SqlTestHost.Branch, [Packaging]));
		var stockScreen = await CaptureSqlAsync(context =>
			new SqlScreenQueryService(context).GetStockAsync(SqlTestHost.Branch));
		var branches = await CaptureSqlAsync(context =>
			new SqlScreenQueryService(context).GetBranchesAsync());
		var staff = await CaptureSqlAsync(context =>
			new SqlScreenQueryService(context).GetStaffAsync());
		var products = await CaptureSqlAsync(context =>
			new SqlScreenQueryService(context).GetProductsAsync());

		Assert.Matches(@"ORDER BY \[\w+\]\.\[Sku\]", stockRead);
		Assert.Matches(@"ORDER BY \[\w+\]\.\[Sku\]", stockScreen);
		Assert.Matches(@"ORDER BY \[\w+\]\.\[BranchId\]", branches);
		Assert.Matches(@"ORDER BY \[\w+\]\.\[StaffId\]", staff);
		Assert.Matches(@"ORDER BY \[\w+\]\.\[ProductId\]", products);
	}

	private async Task<string> CaptureSqlAsync(Func<ApexPostDbContext, Task> read)
	{
		var commands = new List<string>();
		await using var context = new ApexPostDbContext(
			new DbContextOptionsBuilder<ApexPostDbContext>()
				.UseSqlServer(host.ConnectionString)
				.LogTo(commands.Add, [RelationalEventId.CommandExecuting])
				.Options);

		await read(context);

		return Assert.Single(commands);
	}

	private Task<IReadOnlyList<PaidSaleLine>> PaidSaleLinesAsync(
		BranchCode branch,
		DateOnly date) =>
		host.InRequestAsync(services => services
			.GetRequiredService<IReportQueryService>()
			.GetPaidSaleLinesAsync(branch, date));

	private async Task<Guid> SellAtAsync(Sku sku, DateTime occurredAtUtc)
	{
		var request = Sale(sku, 1);
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));
		await host.QueryAsync(context => context.Database.ExecuteSqlAsync(
			$"UPDATE dbo.CounterTransactions SET OccurredAtUtc = {occurredAtUtc} WHERE TransactionId = {request.TransactionId}"));
		return request.TransactionId;
	}

	private Task AddOtherBranchClerkAsync() =>
		host.QueryAsync(context =>
		{
			context.Staff.Add(new Staff(
				OtherClerkId, "Clerk Two", "clerk2@example.invalid", StaffRole.CounterClerk, OtherBranch));
			context.BranchStock.Add(new BranchStock(OtherBranch, Stationery, 10));
			return context.SaveChangesAsync();
		});
}
