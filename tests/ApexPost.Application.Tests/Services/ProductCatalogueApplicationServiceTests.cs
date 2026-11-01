using ApexPost.Application.Caching;
using ApexPost.Application.Tests.TestDoubles;

namespace ApexPost.Application.Tests.Services;

public sealed class ProductCatalogueApplicationServiceTests
{
	private static readonly BranchCode HomeBranch = new("BR01");
	private static readonly BranchCode OtherBranch = new("BR02");
	private static readonly Sku Box = new("BOX-01");
	private static readonly Sku Pen = new("PEN-01");
	private static readonly Sku Stapler = new("STP-01");

	[Fact]
	public async Task Catalogue_ListsEveryProductWithHomeBranchStock()
	{
		var service = CreateService(CreateSnapshot(), out _);

		var catalogue = await service.GetCatalogueAsync("clerk-1");

		Assert.Equal(HomeBranch, catalogue.Branch.Code);
		Assert.Equal(3, catalogue.Rows.Count);
		Assert.Equal(7, Row(catalogue, Box).QuantityOnHand);
		Assert.Equal(0, Row(catalogue, Pen).QuantityOnHand);
		// No stock record at the home branch is shown as unknown, not zero;
		// the other branch's 50 staplers are not counted.
		Assert.Null(Row(catalogue, Stapler).QuantityOnHand);
	}

	[Fact]
	public async Task Catalogue_IsOrderedByCategoryThenName()
	{
		var service = CreateService(CreateSnapshot(), out _);

		var catalogue = await service.GetCatalogueAsync("clerk-1");

		Assert.Equal(
			[Box, Pen, Stapler],
			catalogue.Rows.Select(row => row.Product.Sku));
	}

	[Fact]
	public async Task Catalogue_ProductsComeFromCacheButStockIsAlwaysCurrent()
	{
		var service = CreateService(CreateSnapshot(), out var repository);
		await service.GetCatalogueAsync("clerk-1");

		// A sale changes stock; a renamed product would wait for the cache.
		var stock = repository.Current.BranchStocks.Single(
			item => item.BranchCode == HomeBranch && item.ProductSku == Box);
		stock.RemoveStock(2);
		var second = await service.GetCatalogueAsync("clerk-1");

		Assert.Equal(5, Row(second, Box).QuantityOnHand);
	}

	[Theory]
	[InlineData("inactive-1")]
	[InlineData("unknown")]
	[InlineData(" ")]
	public async Task Catalogue_RequiresActiveStaff(string staffId)
	{
		var service = CreateService(CreateSnapshot(), out _);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.GetCatalogueAsync(staffId));
	}

	[Fact]
	public async Task Catalogue_DuplicateStockRecordsFailClosed()
	{
		var snapshot = CreateSnapshot(
			extraStock: [new BranchStock(HomeBranch, Box, 1)]);
		var service = CreateService(snapshot, out _);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.GetCatalogueAsync("clerk-1"));
	}

	private static ProductCatalogueRow Row(ProductCatalogue catalogue, Sku sku) =>
		catalogue.Rows.Single(row => row.Product.Sku == sku);

	private static ProductCatalogueApplicationService CreateService(
		ApexPostSnapshot snapshot,
		out FakeApexPostRepository repository)
	{
		repository = new FakeApexPostRepository(snapshot);
		return TestServices.ProductCatalogue(
			repository,
			new ReferenceDataCache(
				new SnapshotStore(repository), TimeProvider.System, TimeSpan.FromMinutes(5)));
	}

	private static ApexPostSnapshot CreateSnapshot(
		IEnumerable<BranchStock>? extraStock = null)
	{
		var inactive = new Staff(
			"inactive-1", "Inactive", "i@example.test",
			StaffRole.CounterClerk, HomeBranch);
		inactive.Deactivate();
		return new ApexPostSnapshot(
			[
				new Branch(HomeBranch, "Home", "Region", "09:00-17:00",
					[new CounterTerminal("C1", "Counter")]),
				new Branch(OtherBranch, "Other", "Region", "09:00-17:00",
					[new CounterTerminal("C2", "Counter")])
			],
			[
				new Staff("clerk-1", "Clerk", "c@example.test",
					StaffRole.CounterClerk, HomeBranch),
				inactive
			],
			[
				new Product(Pen, "Pen", ProductCategory.Stationery,
					new Money(5m, "INR"), TaxCategory.Standard),
				new Product(Stapler, "Stapler", ProductCategory.OfficeSupplies,
					new Money(150m, "INR"), TaxCategory.Standard),
				new Product(Box, "Box", ProductCategory.Packaging,
					new Money(20m, "INR"), TaxCategory.Standard)
			],
			[
				new BranchStock(HomeBranch, Box, 7),
				new BranchStock(HomeBranch, Pen, 0),
				new BranchStock(OtherBranch, Stapler, 50),
				.. extraStock ?? []
			],
			[],
			[],
			[],
			[],
			[]);
	}
}
