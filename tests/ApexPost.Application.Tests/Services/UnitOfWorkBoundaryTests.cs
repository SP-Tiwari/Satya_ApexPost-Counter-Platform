using ApexPost.Application.Caching;
using ApexPost.Application.Events;
using ApexPost.Application.Validation;

namespace ApexPost.Application.Tests.Services;

public sealed partial class UnitOfWorkBoundaryTests
{
	private const string ClerkId = "clerk-1";
	private const string ManagerId = "manager-1";
	private const string AdministratorId = "admin-1";
	private const string PaymentMethod = "Cash";
	private static readonly BranchCode Branch = new("BR01");
	private static readonly Sku ProductSku = new("BOX-01");

	[Fact]
	public async Task Sale_CommitsOnce_AndRecordsItsPayment()
	{
		var (service, store, unitOfWork, _) = CreateSales();

		await service.CreateAndProcessAsync(ClerkId, Sale(quantity: 2));

		Assert.Equal(1, unitOfWork.Commits);
		var payment = Assert.Single(store.Payments);
		Assert.Equal(PaymentEntryType.Payment, payment.Type);
		Assert.Equal(PaymentMethod, payment.PaymentMethod);
		Assert.Equal(20m, payment.Amount.Amount);
	}

	[Fact]
	public async Task Sale_WithInsufficientStock_NeverCommits()
	{
		var (service, store, unitOfWork, _) = CreateSales();

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(ClerkId, Sale(quantity: 999)));

		Assert.Equal(0, unitOfWork.Commits);
		Assert.Empty(store.Payments);
	}

	[Fact]
	public async Task Sale_RunTwiceByARetry_PublishesEventsOnce()
	{
		var events = new DomainEventPublisher();
		var published = 0;
		events.TransactionCompleted += (_, _) => published++;
		var (service, _, unitOfWork, _) = CreateSales(
			events, conflictsToSimulate: 1);

		await service.CreateAndProcessAsync(ClerkId, Sale(quantity: 1));

		Assert.Equal(2, unitOfWork.Attempts);
		Assert.Equal(1, published);
	}

	[Fact]
	public async Task Sale_WithOverLongPaymentMethod_IsRejectedBeforeAnyWork()
	{
		var (service, _, unitOfWork, _) = CreateSales();
		var request = Sale(quantity: 1) with
		{
			PaymentMethod = new string('x', InputLimits.PaymentMethod + 1)
		};

		await Assert.ThrowsAsync<ArgumentException>(() =>
			service.CreateAndProcessAsync(ClerkId, request));

		Assert.Equal(0, unitOfWork.Attempts);
	}

	[Fact]
	public async Task Sale_WithWeightTooLargeForItsColumn_IsRejectedBeforeAnyWork()
	{
		var (service, _, unitOfWork, _) = CreateSales();
		var request = Sale(quantity: 1) with
		{
			Shipments =
			[
				new ShippingBasketRequest(
					ShippingItemType.Letter,
					InputLimits.WeightKilograms + 1m,
					10m,
					10m,
					10m,
					"ZONE-1",
					ShippingServiceLevel.Standard)
			]
		};

		await Assert.ThrowsAsync<ArgumentException>(() =>
			service.CreateAndProcessAsync(ClerkId, request));

		Assert.Equal(0, unitOfWork.Attempts);
	}

	// These two pin implemented behaviour that is pending trainer confirmation
	// (ADR 0020). They are not evidence of an approved rule.
	[Fact]
	public async Task VoidOfPaidSale_WritesOneRefundEntry()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await service.VoidAsync(request.TransactionId, ManagerId, "Mistake");

		Assert.Equal(1, unitOfWork.Commits);
		Assert.Single(store.Payments, payment => payment.Type == PaymentEntryType.Refund);
	}

	[Fact]
	public async Task Refund_WritesOneRefundEntry()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await service.RefundAsync(request.TransactionId, ManagerId, "Damaged");

		Assert.Equal(1, unitOfWork.Commits);
		Assert.Single(store.Payments, payment => payment.Type == PaymentEntryType.Refund);
	}

	// The stored entry deliberately disagrees with the basket total and method,
	// so only a copy of that entry can pass.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Reversal_CopiesTheStoredPayment(bool voided)
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		store.Payments.Clear();
		store.AddPayment(new Payment(
			Guid.NewGuid(),
			request.TransactionId,
			PaymentEntryType.Payment,
			new Money(7.77m, "INR"),
			"Stored method",
			DateTimeOffset.UtcNow));
		var (service, _) = CreateTransactions(repository, store);

		await ReverseAsync(service, request.TransactionId, voided);

		var refund = Assert.Single(
			store.Payments, payment => payment.Type == PaymentEntryType.Refund);
		Assert.Equal(new Money(7.77m, "INR"), refund.Amount);
		Assert.Equal("Stored method", refund.PaymentMethod);
		Assert.Equal(request.TransactionId, refund.TransactionId);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Reversal_WithNoStoredPayment_WritesNoRefundEntry(bool voided)
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		store.Payments.Clear();
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await ReverseAsync(service, request.TransactionId, voided);

		Assert.Equal(1, unitOfWork.Commits);
		Assert.Empty(store.Payments);
	}

	[Fact]
	public async Task VoidOfOpenBasket_WritesNoRefundEntry()
	{
		var (_, store, _, repository) = CreateSales();
		var basket = TransactionBasket.Reconstitute(
			Guid.NewGuid(),
			"INR",
			new Dictionary<TaxCategory, decimal> { [TaxCategory.ZeroRated] = 0m },
			[],
			TransactionPaymentStatus.Open,
			[],
			Branch);
		await store.ExecuteInTransactionAsync(async token =>
		{
			store.Add(basket);
			await store.SaveChangesAsync(token);
			return true;
		});
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await service.VoidAsync(basket.Id, ManagerId, "Abandoned");

		Assert.Equal(1, unitOfWork.Commits);
		Assert.Empty(store.Payments);
	}

	[Fact]
	public async Task Void_WithOverLongReason_NeverCommits()
	{
		var (sales, store, _, repository) = CreateSales();
		var request = Sale(quantity: 1);
		await sales.CreateAndProcessAsync(ClerkId, request);
		var (service, unitOfWork) = CreateTransactions(repository, store);

		await Assert.ThrowsAsync<ArgumentException>(() => service.VoidAsync(
			request.TransactionId,
			ManagerId,
			new string('x', InputLimits.ReversalReason + 1)));

		Assert.Equal(0, unitOfWork.Commits);
	}

	[Fact]
	public async Task StockAdjustment_CommitsOnce_AndWritesAHistoryRow()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var store = new SnapshotStore(repository);
		var unitOfWork = new SpyUnitOfWork(store);
		var service = new StockAdjustmentApplicationService(
			store, store, store, store, unitOfWork);

		await service.AdjustStockAsync(ManagerId, ProductSku.Value, -3);

		Assert.Equal(1, unitOfWork.Commits);
		var adjustment = Assert.Single(store.Adjustments);
		Assert.Equal(-3, adjustment.Delta);
		Assert.Equal(ManagerId, adjustment.ChangedByStaffId);
	}

	[Fact]
	public async Task RevisePrices_ChangesOnlyTheCategory_AndInvalidatesNothingWhenRejected()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var store = new SnapshotStore(repository);
		var unitOfWork = new SpyUnitOfWork(store);
		var cache = new ReferenceDataCache(
			store, TimeProvider.System, TimeSpan.FromMinutes(5));
		var service = new AdministrationApplicationService(
			store, store, store, unitOfWork, cache);

		var changed = await service.RevisePricesAsync(
			AdministratorId, ProductCategory.Packaging, 1.5m);

		Assert.Equal(1, changed);
		Assert.Equal(15m, repository.Current.Products.Single().UnitPrice.Amount);
		await Assert.ThrowsAsync<ArgumentException>(() => service.RevisePricesAsync(
			AdministratorId, ProductCategory.Packaging, decimal.MaxValue));
		Assert.Equal(1, unitOfWork.Commits);
	}

	private static Task ReverseAsync(
		TransactionApplicationService service,
		Guid transactionId,
		bool voided) =>
		voided
			? service.VoidAsync(transactionId, ManagerId, "Mistake")
			: service.RefundAsync(transactionId, ManagerId, "Damaged");

	private static CounterBasketRequest Sale(int quantity) => new(
		Guid.NewGuid(),
		"C1",
		PaymentMethod,
		[new GoodsBasketRequest(ProductSku.Value, quantity)],
		[],
		[]);

	private static (
		MixedBasketApplicationService Service,
		SnapshotStore Store,
		SpyUnitOfWork UnitOfWork,
		FakeApexPostRepository Repository) CreateSales(
		DomainEventPublisher? events = null,
		int conflictsToSimulate = 0)
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var store = new SnapshotStore(repository);
		var unitOfWork = new SpyUnitOfWork(store)
		{
			ConflictsToSimulate = conflictsToSimulate
		};
		var service = new MixedBasketApplicationService(
			store,
			store,
			store,
			store,
			store,
			unitOfWork,
			new Dictionary<TaxCategory, decimal> { [TaxCategory.ZeroRated] = 0m },
			new Dictionary<ShippingServiceLevel, Weight>
			{
				[ShippingServiceLevel.Standard] = new Weight(30m)
			},
			TaxCategory.ZeroRated,
			events: events,
			paymentMethods: [PaymentMethod]);
		return (service, store, unitOfWork, repository);
	}

	private static (TransactionApplicationService Service, SpyUnitOfWork UnitOfWork)
		CreateTransactions(FakeApexPostRepository repository, SnapshotStore store)
	{
		_ = repository;
		var unitOfWork = new SpyUnitOfWork(store);
		return (
			new TransactionApplicationService(
				store, store, store, store, unitOfWork),
			unitOfWork);
	}

	private static ApexPostSnapshot CreateSnapshot()
	{
		Branch[] branches =
		[
			new Branch(
				UnitOfWorkBoundaryTests.Branch,
				"Branch",
				"Region",
				"9-5",
				[new CounterTerminal("C1", "Counter 1")])
		];
		Staff[] staff =
		[
			new Staff(ClerkId, "Clerk", "clerk@example.invalid", StaffRole.CounterClerk, UnitOfWorkBoundaryTests.Branch),
			new Staff(ManagerId, "Manager", "manager@example.invalid", StaffRole.BranchManager, UnitOfWorkBoundaryTests.Branch),
			new Staff(AdministratorId, "Admin", "admin@example.invalid", StaffRole.Administrator, UnitOfWorkBoundaryTests.Branch)
		];
		Product[] products =
		[
			new Product(
				ProductSku,
				"Box",
				ProductCategory.Packaging,
				new Money(10m, "INR"),
				TaxCategory.ZeroRated)
		];
		return new ApexPostSnapshot(
			branches,
			staff,
			products,
			[new BranchStock(UnitOfWorkBoundaryTests.Branch, ProductSku, 10)],
			[],
			[new ShippingRate("ZONE-1", ShippingServiceLevel.Standard, new Money(1m, "INR"), new Money(1m, "INR"))],
			[],
			[],
			[]);
	}
}
