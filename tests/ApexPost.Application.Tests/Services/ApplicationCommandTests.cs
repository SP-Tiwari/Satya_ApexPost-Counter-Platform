using ApexPost.Application.Events;

namespace ApexPost.Application.Tests.Services;

// Part F: input model, application command, entity. Each test proves one command
// reaches the entity it should, field by field, in one commit. The rule tests prove
// the overloads add no path around the existing validation.
public sealed partial class UnitOfWorkBoundaryTests
{
	private const string NewBranchCode = "BR02";

	[Fact]
	public async Task CreateStaffCommand_MapsEveryFieldToTheStaffEntity()
	{
		var (service, repository, unitOfWork) = CreateAdministration();

		await service.AddStaffAsync(
			AdministratorId,
			new CreateStaffCommand(
				"new-staff", "New Staff", "new.staff@example.invalid",
				StaffRole.Auditor, Branch.Value));

		var staff = Assert.Single(
			repository.Current.Staff, entry => entry.Id == "new-staff");
		Assert.Equal("New Staff", staff.Name);
		Assert.Equal("new.staff@example.invalid", staff.Email);
		Assert.Equal(StaffRole.Auditor, staff.Role);
		Assert.Equal(Branch, staff.HomeBranchCode);
		Assert.True(staff.IsActive);
		Assert.Equal(1, unitOfWork.Commits);
	}

	[Fact]
	public async Task UpdateStaffCommand_MapsEveryFieldToTheStaffEntity()
	{
		var (service, repository, unitOfWork) = CreateAdministration();
		await service.AddBranchAsync(AdministratorId, NewBranch());

		await service.UpdateStaffAsync(
			AdministratorId,
			new UpdateStaffCommand(
				ClerkId, StaffRole.Auditor, NewBranchCode, IsActive: false));

		var staff = Assert.Single(repository.Current.Staff, entry => entry.Id == ClerkId);
		Assert.Equal(StaffRole.Auditor, staff.Role);
		Assert.Equal(new BranchCode(NewBranchCode), staff.HomeBranchCode);
		Assert.False(staff.IsActive);
		Assert.Equal(2, unitOfWork.Commits);
	}

	[Fact]
	public async Task CreateBranchCommand_MapsEveryFieldToTheBranchEntity()
	{
		var (service, repository, unitOfWork) = CreateAdministration();

		await service.AddBranchAsync(AdministratorId, NewBranch());

		var branch = Assert.Single(
			repository.Current.Branches,
			entry => entry.Code == new BranchCode(NewBranchCode));
		Assert.Equal("New Branch", branch.Name);
		Assert.Equal("North", branch.Region);
		Assert.Equal("09:00-17:00", branch.OpeningHours);
		var terminal = Assert.Single(branch.CounterTerminals);
		Assert.Equal("C2", terminal.Id);
		Assert.Equal("Counter 2", terminal.Name);
		Assert.Equal(1, unitOfWork.Commits);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SetBranchStatusCommand_ReachesTheBranchEntity(bool isActive)
	{
		var (service, repository, unitOfWork) = CreateAdministration();
		await service.SetBranchStatusAsync(AdministratorId, Branch.Value, !isActive);

		await service.SetBranchStatusAsync(
			AdministratorId, new SetBranchStatusCommand(Branch.Value, isActive));

		Assert.Equal(isActive, repository.Current.Branches.Single().IsActive);
		Assert.Equal(2, unitOfWork.Commits);
	}

	[Fact]
	public async Task CreateProductCommand_MapsEveryFieldToTheProductEntity()
	{
		var (service, repository, unitOfWork) = CreateAdministration();

		await service.AddProductAsync(
			AdministratorId,
			new CreateProductCommand(
				"NEW-01", "New Product", ProductCategory.Stationery,
				12.5m, "INR", TaxCategory.Standard));

		var product = Assert.Single(
			repository.Current.Products, entry => entry.Sku == new Sku("NEW-01"));
		Assert.Equal("New Product", product.Name);
		Assert.Equal(ProductCategory.Stationery, product.Category);
		Assert.Equal(new Money(12.5m, "INR"), product.UnitPrice);
		Assert.Equal(TaxCategory.Standard, product.TaxCategory);
		Assert.Equal(1, unitOfWork.Commits);
	}

	[Fact]
	public async Task AdjustStockCommand_ReachesTheStockAndItsHistoryRow()
	{
		var (service, store, repository, unitOfWork) = CreateStockAdjustment();
		var before = repository.Current.BranchStocks.Single().Quantity;

		await service.AdjustStockAsync(
			ManagerId, new AdjustStockCommand(ProductSku.Value, 3));

		Assert.Equal(before + 3, repository.Current.BranchStocks.Single().Quantity);
		var adjustment = Assert.Single(store.Adjustments);
		Assert.Equal(ProductSku, adjustment.ProductSku);
		Assert.Equal(3, adjustment.Delta);
		Assert.Equal(ManagerId, adjustment.ChangedByStaffId);
		Assert.Equal(1, unitOfWork.Commits);
	}

	[Fact]
	public async Task VoidTransactionCommand_ReachesTheBasketWithItsReason()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var events = new DomainEventPublisher();
		var voided = new List<(Guid Id, string Reason)>();
		events.TransactionVoided += (_, entry) => voided.Add((entry.TransactionId, entry.Reason));
		var unitOfWork = new SpyUnitOfWork(store);
		var service = new TransactionApplicationService(
			store, store, store, store, unitOfWork, events: events);

		await service.VoidAsync(
			ManagerId, new VoidTransactionCommand(request.TransactionId, "Mistake"));

		Assert.Equal(
			TransactionPaymentStatus.Voided,
			Assert.Single(repository.Current.TransactionBaskets).PaymentStatus);
		Assert.Equal((request.TransactionId, "Mistake"), Assert.Single(voided));
		Assert.Equal(1, unitOfWork.Commits);
	}

	[Fact]
	public async Task RefundTransactionCommand_ReachesTheBasketWithItsReason()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var events = new DomainEventPublisher();
		var refunded = new List<(Guid Id, string Reason)>();
		events.TransactionRefunded += (_, entry) => refunded.Add((entry.TransactionId, entry.Reason));
		var unitOfWork = new SpyUnitOfWork(store);
		var service = new TransactionApplicationService(
			store, store, store, store, unitOfWork, events: events);

		await service.RefundAsync(
			ManagerId, new RefundTransactionCommand(request.TransactionId, "Damaged"));

		Assert.Equal(
			TransactionPaymentStatus.Refunded,
			Assert.Single(repository.Current.TransactionBaskets).PaymentStatus);
		Assert.Equal((request.TransactionId, "Damaged"), Assert.Single(refunded));
		Assert.Equal(1, unitOfWork.Commits);
	}

	[Fact]
	public async Task CreateStaffCommand_KeepsTheDuplicateRule()
	{
		var (service, _, unitOfWork) = CreateAdministration();

		await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddStaffAsync(
			AdministratorId,
			new CreateStaffCommand(
				ClerkId, "Copy", "copy@example.invalid", StaffRole.Auditor, Branch.Value)));

		Assert.Equal(0, unitOfWork.Commits);
	}

	[Fact]
	public async Task AdministrationCommand_ByANonAdministrator_IsRefused()
	{
		var (service, _, unitOfWork) = CreateAdministration();

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddBranchAsync(
			ClerkId, NewBranch()));

		Assert.Equal(0, unitOfWork.Commits);
	}

	[Fact]
	public async Task AdjustStockCommand_KeepsTheNonZeroRule()
	{
		var (service, store, _, unitOfWork) = CreateStockAdjustment();

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AdjustStockAsync(
			ManagerId, new AdjustStockCommand(ProductSku.Value, 0)));

		Assert.Empty(store.Adjustments);
		Assert.Equal(0, unitOfWork.Commits);
	}

	[Fact]
	public async Task VoidTransactionCommand_KeepsTheBranchManagerRule()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.VoidAsync(
			ClerkId, new VoidTransactionCommand(request.TransactionId, "Mistake")));

		Assert.Equal(
			TransactionPaymentStatus.Paid,
			Assert.Single(repository.Current.TransactionBaskets).PaymentStatus);
		Assert.Equal(0, unitOfWork.Commits);
	}

	[Fact]
	public async Task RefundTransactionCommand_KeepsTheReasonLimit()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await Assert.ThrowsAsync<ArgumentException>(() => service.RefundAsync(
			ManagerId,
			new RefundTransactionCommand(request.TransactionId, new string('a', 501))));

		Assert.Equal(0, unitOfWork.Commits);
	}

	[Fact]
	public async Task EveryOverload_RefusesANullCommand()
	{
		var (administration, _, _) = CreateAdministration();
		var (stock, _, _, _) = CreateStockAdjustment();
		var (sales, store, _, repository) = CreateSales();
		_ = sales;
		var (transactions, _) = CreateTransactions(repository, store);

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			administration.AddStaffAsync(AdministratorId, (CreateStaffCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			administration.UpdateStaffAsync(AdministratorId, (UpdateStaffCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			administration.AddBranchAsync(AdministratorId, (CreateBranchCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			administration.SetBranchStatusAsync(AdministratorId, (SetBranchStatusCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			administration.AddProductAsync(AdministratorId, (CreateProductCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			stock.AdjustStockAsync(ManagerId, (AdjustStockCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			transactions.VoidAsync(ManagerId, (VoidTransactionCommand)null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			transactions.RefundAsync(ManagerId, (RefundTransactionCommand)null!));
	}

	private static CreateBranchCommand NewBranch() =>
		new(NewBranchCode, "New Branch", "North", "09:00-17:00", "C2", "Counter 2");

	private static (
		AdministrationApplicationService Service,
		FakeApexPostRepository Repository,
		SpyUnitOfWork UnitOfWork) CreateAdministration()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var store = new SnapshotStore(repository);
		var unitOfWork = new SpyUnitOfWork(store);
		return (
			new AdministrationApplicationService(store, store, store, unitOfWork),
			repository,
			unitOfWork);
	}

	private static (
		StockAdjustmentApplicationService Service,
		SnapshotStore Store,
		FakeApexPostRepository Repository,
		SpyUnitOfWork UnitOfWork) CreateStockAdjustment()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var store = new SnapshotStore(repository);
		var unitOfWork = new SpyUnitOfWork(store);
		return (
			new StockAdjustmentApplicationService(store, store, store, store, unitOfWork),
			store,
			repository,
			unitOfWork);
	}
}
