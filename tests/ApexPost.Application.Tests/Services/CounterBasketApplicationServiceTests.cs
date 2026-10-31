using ApexPost.Application.Tests.TestDoubles;
using ApexPost.Application.Verification;

namespace ApexPost.Application.Tests.Services;

// Counter sales through MixedBasketApplicationService: several lines in one
// basket, configured payment methods, and money orders as basket lines with a
// configured fee and a system identity-verification decision.
public sealed class CounterBasketApplicationServiceTests
{
	private static readonly BranchCode HomeBranch = new("BR01");
	private static readonly BranchCode OtherBranch = new("BR02");
	private const string ClerkId = "clerk-1";
	private const string HomeCounter = "COUNTER-BR01";
	private const string Passport = "Passport";
	private const string DocumentNumber = "P1234567";
	private static readonly Sku BoxSku = new("BOX-01");
	private static readonly Sku PenSku = new("PEN-01");
	private static readonly MoneyOrderBasketSettings MoneyOrders = new(
		highValueThreshold: new Money(1000m, "INR"),
		fee: new Money(25m, "INR"),
		feeTaxCategory: TaxCategory.ZeroRated);
	private static readonly DateTimeOffset Now =
		new(2026, 3, 2, 9, 15, 0, TimeSpan.Zero);

	[Fact]
	public async Task CounterSale_SeveralGoodsLinesAndShippingAreProcessedTogether()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);
		var transactionId = Guid.NewGuid();

		await service.CreateAndProcessAsync(
			ClerkId,
			CreateRequest(
				transactionId,
				goods:
				[
					new GoodsBasketRequest(BoxSku.Value, 2),
					new GoodsBasketRequest(PenSku.Value, 3)
				],
				shipments:
				[
					new ShippingBasketRequest(
						ShippingItemType.Parcel, 2m, 10m, 10m, 10m,
						"ZONE-A", ShippingServiceLevel.Standard)
				]));

		var snapshot = repository.Current;
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(3, basket.LineItems.Count);
		Assert.Equal(Now, basket.OccurredAtUtc);
		Assert.Equal(
			new CounterSaleDetails(HomeCounter, ClerkId, "Cash"),
			basket.SaleDetails);
		Assert.Equal(8, StockOf(snapshot, BoxSku));
		Assert.Equal(17, StockOf(snapshot, PenSku));
		// Box 2 x 20 + pen 3 x 5 + shipping 10 + 2 x 2 kg = 69, plus 10% tax.
		Assert.Equal(new Money(69m, "INR"), basket.Subtotal);
		Assert.Equal(new Money(75.9m, "INR"), basket.Total);
		Assert.Single(snapshot.ShippingItems);
	}

	[Fact]
	public async Task CounterSale_PaymentMethodMatchesConfiguredNameIgnoringCase()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);

		await service.CreateAndProcessAsync(
			ClerkId, CreateRequest(paymentMethod: " cash "));

		Assert.Equal(
			"Cash",
			Assert.Single(repository.Current.TransactionBaskets)
				.SaleDetails!.PaymentMethod);
	}

	[Fact]
	public async Task CounterSale_UnknownPaymentMethodIsRejectedWithoutWriting()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			service.CreateAndProcessAsync(
				ClerkId, CreateRequest(paymentMethod: "Cheque")));

		Assert.Equal(0, repository.WriteCount);
	}

	[Fact]
	public async Task CounterSale_WithoutConfiguredPaymentMethodsIsRejected()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository, paymentMethods: []);

		Assert.Empty(service.PaymentMethods);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(ClerkId, CreateRequest()));
		Assert.Equal(0, repository.WriteCount);
	}

	[Fact]
	public void Constructor_RejectsDuplicateOrBlankPaymentMethods()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());

		Assert.Throws<ArgumentException>(() =>
			CreateService(repository, paymentMethods: ["Cash", "CASH"]));
		Assert.Throws<ArgumentException>(() =>
			CreateService(repository, paymentMethods: ["Cash", " "]));
	}

	[Fact]
	public async Task CounterSale_CounterFromAnotherBranchIsRejected()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				ClerkId, CreateRequest(counterTerminalId: "COUNTER-BR02")));

		Assert.Empty(repository.Current.TransactionBaskets);
	}

	[Fact]
	public async Task CounterSale_RepeatedSubmissionIsProcessedOnce()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);
		var request = CreateRequest();

		await service.CreateAndProcessAsync(ClerkId, request);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(ClerkId, request));

		Assert.Single(repository.Current.TransactionBaskets);
		Assert.Equal(9, StockOf(repository.Current, BoxSku));
	}

	[Fact]
	public async Task MoneyOrder_IsABasketLineWithTheConfiguredFee()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var verification = new FakeIdentityVerificationService(true);
		var service = CreateService(repository, verification);

		await service.CreateAndProcessAsync(
			ClerkId,
			CreateRequest(
				goods: [],
				moneyOrders: [new MoneyOrderBasketRequest(
					500m, "Sender", "Recipient", null, null)]));

		var snapshot = repository.Current;
		var order = Assert.Single(snapshot.MoneyOrders);
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(new Money(500m, "INR"), order.Amount);
		Assert.Equal(MoneyOrders.Fee, order.Fee);
		Assert.Equal(HomeBranch, order.ProcessingBranchCode);
		Assert.Null(order.IdentityVerification);
		Assert.Same(
			order,
			Assert.Single(
				basket.LineItems,
				line => line.Type == TransactionItemType.MoneyOrder).MoneyOrder);
		Assert.Contains(
			basket.LineItems,
			line => line.Type == TransactionItemType.Service &&
				line.UnitPrice == MoneyOrders.Fee);
		Assert.Equal(new Money(525m, "INR"), basket.Total);
		Assert.Equal(0, verification.CallCount);
	}

	[Fact]
	public async Task MoneyOrder_AtThresholdNeedsNoVerification()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);

		await service.CreateAndProcessAsync(
			ClerkId,
			CreateRequest(
				goods: [],
				moneyOrders: [new MoneyOrderBasketRequest(
					1000m, "Sender", "Recipient", null, null)]));

		Assert.Single(repository.Current.MoneyOrders);
	}

	[Fact]
	public async Task MoneyOrder_AboveThresholdWithSuccessfulCheckKeepsOnlyMaskedNumber()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var verification = new FakeIdentityVerificationService(true);
		var service = CreateService(repository, verification);

		await service.CreateAndProcessAsync(
			ClerkId,
			CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
				1500m, "Sender", "Recipient", Passport, DocumentNumber)]));

		var snapshot = repository.Current;
		var stored = Assert.Single(snapshot.IdentityVerifications);
		Assert.True(stored.IsSuccessful);
		Assert.Equal(Passport, stored.DocumentType);
		Assert.Equal("****4567", stored.MaskedDocumentIdentifier);
		Assert.Same(stored, Assert.Single(snapshot.MoneyOrders).IdentityVerification);
		Assert.Equal((Passport, DocumentNumber), verification.LastRequest);
	}

	[Fact]
	public async Task MoneyOrder_AboveThresholdWithFailedCheckIsRejectedWithoutWriting()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(
			repository, new FakeIdentityVerificationService(false));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				ClerkId,
				CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
					1500m, "Sender", "Recipient", Passport, DocumentNumber)])));

		AssertNothingSold(repository);
	}

	[Fact]
	public async Task MoneyOrder_AboveThresholdWithoutDocumentIsRejectedWithoutWriting()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				ClerkId,
				CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
					1500m, "Sender", "Recipient", null, null)])));

		AssertNothingSold(repository);
	}

	[Fact]
	public async Task MoneyOrder_FailedCheckBelowThresholdIsIssuedAndRecorded()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(
			repository, new FakeIdentityVerificationService(false));

		await service.CreateAndProcessAsync(
			ClerkId,
			CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
				500m, "Sender", "Recipient", Passport, DocumentNumber)]));

		var order = Assert.Single(repository.Current.MoneyOrders);
		Assert.False(order.IdentityVerification!.IsSuccessful);
	}

	[Fact]
	public async Task MoneyOrder_VerificationUnavailableIsReportedWithoutWriting()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(
			repository, new FakeIdentityVerificationService(null));

		await Assert.ThrowsAsync<IdentityVerificationUnavailableException>(() =>
			service.CreateAndProcessAsync(
				ClerkId,
				CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
					1500m, "Sender", "Recipient", Passport, DocumentNumber)])));

		AssertNothingSold(repository);
	}

	[Fact]
	public async Task MoneyOrder_DocumentTypeWithoutNumberIsRejected()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository);

		await Assert.ThrowsAsync<ArgumentException>(() =>
			service.CreateAndProcessAsync(
				ClerkId,
				CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
					1500m, "Sender", "Recipient", Passport, null)])));

		AssertNothingSold(repository);
	}

	[Fact]
	public async Task MoneyOrder_WithoutConfiguredSettingsIsRejected()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());
		var service = CreateService(repository, moneyOrderSettings: null);

		Assert.False(service.MoneyOrdersAvailable);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				ClerkId,
				CreateRequest(moneyOrders: [new MoneyOrderBasketRequest(
					100m, "Sender", "Recipient", null, null)])));
		AssertNothingSold(repository);
	}

	[Fact]
	public async Task MoneyOrder_FeeTaxCategoryWithoutConfiguredRateIsUnavailable()
	{
		var service = TestServices.MixedBasket(
			new FakeApexPostRepository(CreateSnapshot()),
			new Dictionary<TaxCategory, decimal> { [TaxCategory.Standard] = 0.1m },
			new Dictionary<ShippingServiceLevel, Weight>(),
			moneyOrderSettings: MoneyOrders,
			paymentMethods: ["Cash"]);

		Assert.False(service.MoneyOrdersAvailable);
		Assert.Null((await service.GetCatalogAsync(ClerkId)).MoneyOrders);
	}

	private static void AssertNothingSold(FakeApexPostRepository repository)
	{
		Assert.Empty(repository.Current.TransactionBaskets);
		Assert.Empty(repository.Current.MoneyOrders);
		Assert.Empty(repository.Current.IdentityVerifications);
		Assert.Equal(10, StockOf(repository.Current, BoxSku));
	}

	private static int StockOf(ApexPostSnapshot snapshot, Sku sku) =>
		snapshot.BranchStocks
			.Single(stock => stock.BranchCode == HomeBranch && stock.ProductSku == sku)
			.Quantity;

	private static CounterBasketRequest CreateRequest(
		Guid? transactionId = null,
		string counterTerminalId = HomeCounter,
		string paymentMethod = "Cash",
		IReadOnlyList<GoodsBasketRequest>? goods = null,
		IReadOnlyList<ShippingBasketRequest>? shipments = null,
		IReadOnlyList<MoneyOrderBasketRequest>? moneyOrders = null) =>
		new(
			transactionId ?? Guid.NewGuid(),
			counterTerminalId,
			paymentMethod,
			goods ?? [new GoodsBasketRequest(BoxSku.Value, 1)],
			shipments ?? [],
			moneyOrders ?? []);

	private static MixedBasketApplicationService CreateService(
		FakeApexPostRepository repository,
		IIdentityVerificationService? verification = null,
		IReadOnlyCollection<string>? paymentMethods = null) =>
		CreateService(repository, MoneyOrders, verification, paymentMethods);

	private static MixedBasketApplicationService CreateService(
		FakeApexPostRepository repository,
		MoneyOrderBasketSettings? moneyOrderSettings,
		IIdentityVerificationService? verification = null,
		IReadOnlyCollection<string>? paymentMethods = null) =>
		TestServices.MixedBasket(
			repository,
			new Dictionary<TaxCategory, decimal>
			{
				[TaxCategory.Standard] = 0.1m,
				[TaxCategory.ZeroRated] = 0m
			},
			new Dictionary<ShippingServiceLevel, Weight>
			{
				[ShippingServiceLevel.Standard] = new Weight(5m)
			},
			TaxCategory.Standard,
			new FixedTimeProvider(Now),
			moneyOrderSettings,
			verification ?? new FakeIdentityVerificationService(true),
			paymentMethods: paymentMethods ?? ["Cash"]);

	private static ApexPostSnapshot CreateSnapshot() =>
		new(
			[CreateBranch(HomeBranch), CreateBranch(OtherBranch)],
			[
				new Staff(ClerkId, "Clerk", "clerk@example.test",
					StaffRole.CounterClerk, HomeBranch)
			],
			[
				new Product(BoxSku, "Box", ProductCategory.Packaging,
					new Money(20m, "INR"), TaxCategory.Standard),
				new Product(PenSku, "Pen", ProductCategory.Stationery,
					new Money(5m, "INR"), TaxCategory.Standard)
			],
			[
				new BranchStock(HomeBranch, BoxSku, 10),
				new BranchStock(HomeBranch, PenSku, 20)
			],
			[],
			[new ShippingRate(
				"ZONE-A",
				ShippingServiceLevel.Standard,
				new Money(10m, "INR"),
				new Money(2m, "INR"))],
			[],
			[],
			[]);

	private static Branch CreateBranch(BranchCode code) =>
		new(
			code,
			$"Branch {code}",
			"Region",
			"09:00-17:00",
			[new CounterTerminal($"COUNTER-{code}", "Counter")]);

	// Answers every check with a fixed result; null simulates an outage.
	private sealed class FakeIdentityVerificationService(bool? result)
		: IIdentityVerificationService
	{
		public int CallCount { get; private set; }
		public (string Type, string Identifier)? LastRequest { get; private set; }

		public Task<bool> VerifyAsync(
			string documentType,
			string documentIdentifier,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			LastRequest = (documentType, documentIdentifier);
			return result is bool value
				? Task.FromResult(value)
				: throw new IdentityVerificationUnavailableException(
					"Verification provider timed out.");
		}
	}

	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}
}
