namespace ApexPost.Application.Tests.Services;

public sealed class StockAdjustmentApplicationServiceTests
{
	private static readonly BranchCode HomeBranch = new("BR01");
	private static readonly BranchCode OtherBranch = new("BR02");
	private static readonly Sku ExistingSku = new("BOX-01");
	private const string ManagerId = "manager-1";

	[Fact]
	public async Task GetCatalogAsync_ReturnsOnlyPersistedHomeBranchStock()
	{
		var repository = CreateRepository();
		var service = TestServices.StockAdjustment(repository);

		var catalog = await service.GetCatalogAsync(ManagerId);

		Assert.Equal(HomeBranch, catalog.Branch.Code);
		var stock = Assert.Single(catalog.BranchStocks);
		Assert.Equal(HomeBranch, stock.BranchCode);
		Assert.Equal(ExistingSku, stock.ProductSku);
	}

	[Fact]
	public async Task AdjustStockAsync_AddsStockAndPersistsTheChange()
	{
		var repository = CreateRepository();
		var service = TestServices.StockAdjustment(repository);

		await service.AdjustStockAsync(ManagerId, ExistingSku.Value, 3);

		var snapshot = await repository.LoadAsync();
		Assert.Equal(
			7,
			Assert.Single(
				snapshot.BranchStocks,
				stock => stock.BranchCode == HomeBranch).Quantity);
	}

	[Fact]
	public async Task AdjustStockAsync_RemovesStockWithoutGoingBelowZero()
	{
		var repository = CreateRepository();
		var service = TestServices.StockAdjustment(repository);

		await service.AdjustStockAsync(ManagerId, ExistingSku.Value, -2);

		Assert.Equal(
			2,
			Assert.Single(
				(await repository.LoadAsync()).BranchStocks,
				stock => stock.BranchCode == HomeBranch).Quantity);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(int.MinValue)]
	public async Task AdjustStockAsync_RejectsUnsupportedQuantityWithoutMutation(
		int quantity)
	{
		var repository = CreateRepository();
		var service = TestServices.StockAdjustment(repository);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			service.AdjustStockAsync(ManagerId, ExistingSku.Value, quantity));

		Assert.Equal(
			4,
			Assert.Single(
				(await repository.LoadAsync()).BranchStocks,
				stock => stock.BranchCode == HomeBranch).Quantity);
	}

	[Fact]
	public async Task AdjustStockAsync_RejectsRemovalBeyondAvailableStock()
	{
		var repository = CreateRepository();
		var service = TestServices.StockAdjustment(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.AdjustStockAsync(ManagerId, ExistingSku.Value, -5));

		Assert.Equal(
			4,
			Assert.Single(
				(await repository.LoadAsync()).BranchStocks,
				stock => stock.BranchCode == HomeBranch).Quantity);
	}

	[Fact]
	public async Task AdjustStockAsync_RejectsQuantityOverflowWithoutMutation()
	{
		var repository = CreateRepository(homeStockQuantity: int.MaxValue);
		var service = TestServices.StockAdjustment(repository);

		await Assert.ThrowsAsync<OverflowException>(() =>
			service.AdjustStockAsync(ManagerId, ExistingSku.Value, 1));

		Assert.Equal(
			int.MaxValue,
			Assert.Single(
				(await repository.LoadAsync()).BranchStocks,
				stock => stock.BranchCode == HomeBranch).Quantity);
	}

	[Fact]
	public async Task AdjustStockAsync_RejectsMissingProductWithoutMutation()
	{
		var repository = CreateRepository();
		var service = TestServices.StockAdjustment(repository);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.AdjustStockAsync(ManagerId, "MISSING", 1));

		Assert.Equal(
			4,
			Assert.Single(
				(await repository.LoadAsync()).BranchStocks,
				stock => stock.BranchCode == HomeBranch).Quantity);
	}

	[Fact]
	public async Task AdjustStockAsync_RejectsMissingHomeBranchStockWithoutMutation()
	{
		var repository = CreateRepository(includeHomeStock: false);
		var service = TestServices.StockAdjustment(repository);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.AdjustStockAsync(ManagerId, ExistingSku.Value, 1));

		Assert.Single((await repository.LoadAsync()).BranchStocks);
	}

	[Fact]
	public async Task AdjustStockAsync_RejectsInactiveOrUnknownManagers()
	{
		var inactiveManager = CreateManager();
		inactiveManager.Deactivate();
		var inactiveService = TestServices.StockAdjustment(
			CreateRepository(staff: inactiveManager));
		var unknownService = TestServices.StockAdjustment(
			CreateRepository());

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			inactiveService.AdjustStockAsync(
				ManagerId, ExistingSku.Value, 1));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			unknownService.AdjustStockAsync(
				"unknown-manager", ExistingSku.Value, 1));
	}

	[Fact]
	public async Task AdjustStockAsync_RejectsCounterClerk()
	{
		var clerk = new Staff(
			"clerk-1",
			"Test Clerk",
			"clerk@example.test",
			StaffRole.CounterClerk,
			HomeBranch);
		var service = TestServices.StockAdjustment(
			CreateRepository(staff: clerk));

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.AdjustStockAsync(
				clerk.Id, ExistingSku.Value, 1));
	}

	private static InMemoryApexPostRepository CreateRepository(
		Staff? staff = null,
		bool includeHomeStock = true,
		int homeStockQuantity = 4)
	{
		var stocks = new List<BranchStock>
		{
			new(OtherBranch, ExistingSku, 20)
		};
		if (includeHomeStock)
			stocks.Insert(
				0,
				new BranchStock(
					HomeBranch, ExistingSku, homeStockQuantity));

		return new InMemoryApexPostRepository(new ApexPostSnapshot(
			[CreateBranch(HomeBranch), CreateBranch(OtherBranch)],
			[staff ?? CreateManager()],
			[
				new Product(
					ExistingSku,
					"Box",
					ProductCategory.Packaging,
					new Money(10m, "INR"),
					TaxCategory.Standard)
			],
			stocks,
			[],
			[],
			[],
			[],
			[]));
	}

	private static Staff CreateManager() =>
		new(
			ManagerId,
			"Test Manager",
			"manager@example.test",
			StaffRole.BranchManager,
			HomeBranch);

	private static Branch CreateBranch(BranchCode code) =>
		new(
			code,
			$"Branch {code}",
			"Test Region",
			"09:00-17:00",
			[new CounterTerminal($"COUNTER-{code}", "Counter")]);

	private sealed class InMemoryApexPostRepository : IApexPostRepository
	{
		private ApexPostSnapshot _snapshot;

		public InMemoryApexPostRepository(ApexPostSnapshot snapshot) =>
			_snapshot = Clone(snapshot);

		public Task<ApexPostSnapshot> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(Clone(_snapshot));
		}

		public Task SaveAsync(
			ApexPostSnapshot snapshot,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_snapshot = Clone(snapshot);
			return Task.CompletedTask;
		}

		public Task UpdateAsync(
			Func<ApexPostSnapshot, ApexPostSnapshot> update,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var updated = update(Clone(_snapshot));
			cancellationToken.ThrowIfCancellationRequested();
			_snapshot = Clone(updated);
			return Task.CompletedTask;
		}

		private static ApexPostSnapshot Clone(ApexPostSnapshot snapshot) =>
			new(
				snapshot.Branches,
				snapshot.Staff,
				snapshot.Products,
				snapshot.BranchStocks.Select(stock =>
					new BranchStock(
						stock.BranchCode, stock.ProductSku, stock.Quantity)),
				snapshot.ShippingItems,
				snapshot.ShippingRates,
				snapshot.MoneyOrders,
				snapshot.IdentityVerifications,
				snapshot.TransactionBaskets);
	}
}
