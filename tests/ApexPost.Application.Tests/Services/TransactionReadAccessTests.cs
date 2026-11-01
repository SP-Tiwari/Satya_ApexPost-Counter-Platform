using ApexPost.Application.Tests.TestDoubles;

namespace ApexPost.Application.Tests.Services;

// Who may read one transaction: managers see their branch; a clerk sees only
// the sales they processed at their branch (TransactionAccessRule.CanView).
public sealed class TransactionReadAccessTests
{
	private static readonly BranchCode HomeBranch = new("BR01");
	private static readonly BranchCode OtherBranch = new("BR02");

	[Fact]
	public async Task Clerk_CanReadOwnSaleAtHomeBranch()
	{
		var sale = CreateSale(HomeBranch, "clerk-1");
		var service = CreateService(sale);

		var details = await service.GetTransactionBasketAsync(sale.Id, "clerk-1");

		Assert.Equal(sale.Id, details.Id);
		Assert.Equal("clerk-1", details.SaleDetails!.ClerkStaffId);
	}

	[Fact]
	public async Task Clerk_CannotReadAnotherClerksSale()
	{
		var sale = CreateSale(HomeBranch, "clerk-2");
		var service = CreateService(sale);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.GetTransactionBasketAsync(sale.Id, "clerk-1"));
	}

	[Fact]
	public async Task Clerk_CannotReadSaleWithoutRecordedClerk()
	{
		var sale = CreateSale(HomeBranch, clerkId: null);
		var service = CreateService(sale);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.GetTransactionBasketAsync(sale.Id, "clerk-1"));
	}

	[Fact]
	public async Task Clerk_ReassignedToAnotherBranchLosesAccessToEarlierSale()
	{
		var sale = CreateSale(OtherBranch, "clerk-1");
		var service = CreateService(sale);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.GetTransactionBasketAsync(sale.Id, "clerk-1"));
	}

	[Fact]
	public async Task InactiveClerk_IsDenied()
	{
		var sale = CreateSale(HomeBranch, "clerk-1");
		var staff = CreateStaff();
		staff.Single(person => person.Id == "clerk-1").Deactivate();
		var service = TestServices.Transactions(
			new FakeApexPostRepository(CreateSnapshot([sale], staff)));

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.GetTransactionBasketAsync(sale.Id, "clerk-1"));
	}

	[Theory]
	[InlineData("auditor-1")]
	[InlineData("admin-1")]
	[InlineData("unknown")]
	public async Task OtherRoles_AreDenied(string staffId)
	{
		var sale = CreateSale(HomeBranch, "clerk-1");
		var service = CreateService(sale);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.GetTransactionBasketAsync(sale.Id, staffId));
	}

	[Fact]
	public async Task Manager_CanReadAnySaleAtHomeBranchOnly()
	{
		var local = CreateSale(HomeBranch, "clerk-2");
		var foreign = CreateSale(OtherBranch, "clerk-3");
		var service = CreateService(local, foreign);

		Assert.Equal(
			local.Id,
			(await service.GetTransactionBasketAsync(local.Id, "manager-1")).Id);
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.GetTransactionBasketAsync(foreign.Id, "manager-1"));
	}

	[Fact]
	public async Task Clerk_CannotListVoidOrRefundEvenOwnSale()
	{
		var sale = CreateSale(HomeBranch, "clerk-1");
		var repository = new FakeApexPostRepository(
			CreateSnapshot([sale], CreateStaff()));
		var service = TestServices.Transactions(repository);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.GetTransactionBasketsAsync("clerk-1"));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.VoidAsync(sale.Id, "clerk-1", "Customer changed mind"));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.RefundAsync(sale.Id, "clerk-1", "Damaged"));
		Assert.Equal(
			TransactionPaymentStatus.Paid,
			Assert.Single(repository.Current.TransactionBaskets).PaymentStatus);
	}

	private static TransactionApplicationService CreateService(
		params TransactionBasket[] baskets) =>
		TestServices.Transactions(new FakeApexPostRepository(CreateSnapshot(baskets, CreateStaff())));

	private static TransactionBasket CreateSale(
		BranchCode branchCode,
		string? clerkId) =>
		TransactionBasket.Reconstitute(
			Guid.NewGuid(),
			"INR",
			new Dictionary<TaxCategory, decimal> { [TaxCategory.Standard] = 0m },
			[new TransactionLineItem(
				"Counter service",
				TransactionItemType.Service,
				new Money(10m, "INR"),
				1,
				TaxCategory.Standard)],
			TransactionPaymentStatus.Paid,
			[],
			branchCode,
			new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero),
			clerkId is null
				? null
				: new CounterSaleDetails($"COUNTER-{branchCode}", clerkId, "Cash"));

	private static Staff[] CreateStaff() =>
	[
		new("manager-1", "Manager", "m@example.test", StaffRole.BranchManager, HomeBranch),
		new("clerk-1", "Clerk one", "c1@example.test", StaffRole.CounterClerk, HomeBranch),
		new("clerk-2", "Clerk two", "c2@example.test", StaffRole.CounterClerk, HomeBranch),
		new("clerk-3", "Clerk three", "c3@example.test", StaffRole.CounterClerk, OtherBranch),
		new("auditor-1", "Auditor", "a@example.test", StaffRole.Auditor, HomeBranch),
		new("admin-1", "Admin", "ad@example.test", StaffRole.Administrator, HomeBranch)
	];

	private static ApexPostSnapshot CreateSnapshot(
		IEnumerable<TransactionBasket> baskets,
		IEnumerable<Staff> staff) =>
		new(
			[
				new Branch(HomeBranch, "Home", "Region", "09:00-17:00",
					[new CounterTerminal("COUNTER-BR01", "Counter")]),
				new Branch(OtherBranch, "Other", "Region", "09:00-17:00",
					[new CounterTerminal("COUNTER-BR02", "Counter")])
			],
			staff,
			[],
			[],
			[],
			[],
			[],
			[],
			baskets);
}
