using ApexPost.Domain.Notifications;

namespace ApexPost.Application.Tests.Services;

public sealed class TransactionApplicationServiceTests
{
	private static readonly BranchCode BranchOne = new("BR01");
	private static readonly BranchCode BranchTwo = new("BR02");
	private static readonly Sku ProductSku = new("BOX-01");

	[Fact]
	public async Task CreateBasketAndAddLineItemAsync_PersistsBasketWithExplicitTaxRates()
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		var rates = new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = 0.1m
		};

		var transactionId = await service.CreateBasketAsync("INR", rates);
		await service.AddLineItemAsync(
			transactionId,
			new TransactionLineItem(
				"Counter service",
				TransactionItemType.Service,
				new Money(10m, "INR"),
				1,
				TaxCategory.Standard));

		var basket = Assert.Single((await repository.LoadAsync()).TransactionBaskets);
		Assert.Equal(transactionId, basket.Id);
		Assert.Equal(TransactionPaymentStatus.Open, basket.PaymentStatus);
		Assert.Equal(0.1m, basket.TaxRates[TaxCategory.Standard]);
		Assert.Equal(new Money(11m, "INR"), basket.Total);
		Assert.Null(Assert.Single(basket.LineItems).ProductSku);
	}

	[Fact]
	public async Task MixedBasket_PricingStockAndBranchAttributionArePersistedAtomically()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(repository);
		var transactionId = Guid.NewGuid();

		await service.CreateAndProcessAsync(
			transactionId,
			"manager-1",
			[new GoodsBasketRequest(ProductSku.Value, 2)],
			[CreateShippingRequest()]);

		var snapshot = await repository.LoadAsync();
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(transactionId, basket.Id);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(BranchOne, basket.BranchCode);
		Assert.Equal(new Money(56m, "INR"), basket.Subtotal);
		Assert.Equal(new Money(5.6m, "INR"), basket.Tax);
		Assert.Equal(new Money(61.6m, "INR"), basket.Total);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Equal(2, basket.LineItems.Count);
		Assert.Equal(
			new Money(16m, "INR"),
			basket.LineItems[1].UnitPrice);
		Assert.Equal(
			Assert.Single(snapshot.ShippingItems),
			basket.LineItems[1].ShippingItem);
	}

	[Fact]
	public async Task MixedBasket_ActiveCounterClerkCanProcessSaleAtHomeBranch()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(repository);
		var transactionId = Guid.NewGuid();

		await service.CreateAndProcessAsync(
			transactionId,
			"clerk-1",
			[new GoodsBasketRequest(ProductSku.Value, 2)],
			[]);

		var snapshot = await repository.LoadAsync();
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(BranchOne, basket.BranchCode);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task MixedBasket_InactiveCounterClerkCannotProcessSale()
	{
		var repository = CreateMixedBasketRepository(deactivateClerk: true);
		var service = CreateMixedBasketService(repository);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"clerk-1",
				[new GoodsBasketRequest(ProductSku.Value, 1)],
				[]));

		var snapshot = await repository.LoadAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task MixedBasket_ClosedBranchCannotAcceptCounterClerkSale()
	{
		var repository = CreateMixedBasketRepository(closeHomeBranch: true);
		var service = CreateMixedBasketService(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"clerk-1",
				[new GoodsBasketRequest(ProductSku.Value, 1)],
				[]));

		var snapshot = await repository.LoadAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task MixedBasket_ParcelOverConfiguredWeightDoesNotPersistOrDeductStock()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"manager-1",
				[new GoodsBasketRequest(ProductSku.Value, 2)],
				[CreateShippingRequest(weightKilograms: 6m)]));

		var snapshot = await repository.LoadAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Empty(snapshot.ShippingItems);
	}

	[Fact]
	public async Task MixedBasket_InsufficientStockDoesNotPersistTransaction()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"manager-1",
				[new GoodsBasketRequest(ProductSku.Value, 5)],
				[CreateShippingRequest()]));

		var snapshot = await repository.LoadAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Empty(snapshot.ShippingItems);
	}

	[Fact]
	public async Task MixedBasket_RejectsDifferentCurrenciesBeforePersistence()
	{
		var repository = CreateMixedBasketRepository(
			new ShippingRate(
				"ZONE-A",
				ShippingServiceLevel.Standard,
				new Money(10m, "USD"),
				new Money(2m, "USD")));
		var service = CreateMixedBasketService(repository);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"manager-1",
				[new GoodsBasketRequest(ProductSku.Value, 1)],
				[CreateShippingRequest()]));

		Assert.Empty((await repository.LoadAsync()).TransactionBaskets);
	}

	[Fact]
	public async Task MixedBasket_LetterDoesNotRequireParcelWeightConfiguration()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(
			repository, new Dictionary<ShippingServiceLevel, Weight>());

		await service.CreateAndProcessAsync(
			Guid.NewGuid(),
			"manager-1",
			[],
			[CreateShippingRequest(ShippingItemType.Letter)]);

		var snapshot = await repository.LoadAsync();
		Assert.Equal(TransactionPaymentStatus.Paid,
			Assert.Single(snapshot.TransactionBaskets).PaymentStatus);
		Assert.Equal(
			ShippingItemType.Letter,
			Assert.Single(snapshot.ShippingItems).Type);
	}

	[Fact]
	public async Task MixedBasket_ParcelRequiresConfiguredServiceWeightLimit()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(
			repository, new Dictionary<ShippingServiceLevel, Weight>());

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"manager-1",
				[],
				[CreateShippingRequest()]));

		Assert.Empty((await repository.LoadAsync()).TransactionBaskets);
	}

	[Fact]
	public void MixedBasket_RejectsNegativeTaxConfiguration()
	{
		var repository = CreateMixedBasketRepository();

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			TestServices.MixedBasket(
				repository,
				new Dictionary<TaxCategory, decimal>
				{
					[TaxCategory.Standard] = -0.1m
				},
				new Dictionary<ShippingServiceLevel, Weight>(),
				TaxCategory.Standard));
	}

	[Fact]
	public async Task MixedBasket_RepeatedTransactionIdCannotProcessOrDeductTwice()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(repository);
		var transactionId = Guid.NewGuid();

		await service.CreateAndProcessAsync(
			transactionId,
			"manager-1",
			[new GoodsBasketRequest(ProductSku.Value, 2)],
			[CreateShippingRequest()]);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				transactionId,
				"manager-1",
				[new GoodsBasketRequest(ProductSku.Value, 2)],
				[CreateShippingRequest()]));

		var snapshot = await repository.LoadAsync();
		Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Single(snapshot.ShippingItems);
	}

	[Fact]
	public async Task MixedBasket_RejectsUnauthorizedStaffAndMissingRate()
	{
		var repository = CreateMixedBasketRepository();
		var service = CreateMixedBasketService(repository);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"unknown-staff",
				[new GoodsBasketRequest(ProductSku.Value, 1)],
				[]));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.CreateAndProcessAsync(
				Guid.NewGuid(),
				"manager-1",
				[],
				[CreateShippingRequest(
					serviceLevel: ShippingServiceLevel.Express)]));

		Assert.Empty((await repository.LoadAsync()).TransactionBaskets);
	}

	[Fact]
	public async Task TransactionReads_ReturnOnlyTheActiveManagersAssignedBranch()
	{
		var localBasket = CreateScopedBasket(
			Guid.NewGuid(), BranchOne, TransactionPaymentStatus.Paid);
		var otherBranchBasket = CreateScopedBasket(
			Guid.NewGuid(), BranchTwo, TransactionPaymentStatus.Paid);
		var legacyBasket = CreateScopedBasket(
			Guid.NewGuid(), null, TransactionPaymentStatus.Paid);
		var repository = new InMemoryApexPostRepository(
			CreateScopedSnapshot(
				[localBasket, otherBranchBasket, legacyBasket]));
		var service = TestServices.Transactions(repository);

		var visible = await service.GetTransactionBasketsAsync("manager-1");
		var details = await service.GetTransactionBasketAsync(
			localBasket.Id, "manager-1");

		Assert.Equal([localBasket.Id], visible.Select(item => item.Id));
		Assert.Equal(localBasket.Id, details.Id);
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.GetTransactionBasketAsync(
				otherBranchBasket.Id, "manager-1"));
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.GetTransactionBasketAsync(
				legacyBasket.Id, "manager-1"));
	}

	[Fact]
	public async Task TransactionReads_RejectInactiveStaffAndWrongRole()
	{
		var manager = CreateStaff(
			"manager-1", StaffRole.BranchManager, BranchOne);
		manager.Deactivate();
		var inactiveSnapshot = CreateScopedSnapshot(
			[CreateScopedBasket(
				Guid.NewGuid(), BranchOne, TransactionPaymentStatus.Paid)],
			[manager]);
		var inactiveService = TestServices.Transactions(
			new InMemoryApexPostRepository(inactiveSnapshot));

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			inactiveService.GetTransactionBasketsAsync("manager-1"));

		var clerkService = TestServices.Transactions(
			new InMemoryApexPostRepository(CreateScopedSnapshot(
				[],
				[CreateStaff("manager-1", StaffRole.CounterClerk, BranchOne)])));
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			clerkService.GetTransactionBasketsAsync("manager-1"));
	}

	[Theory]
	[InlineData(TransactionPaymentStatus.Open, "void")]
	[InlineData(TransactionPaymentStatus.Paid, "refund")]
	public async Task StatusChange_CannotMutateAnotherBranchesTransaction(
		TransactionPaymentStatus status,
		string action)
	{
		var foreignBasket = CreateScopedBasket(
			Guid.NewGuid(), BranchTwo, status);
		var repository = new InMemoryApexPostRepository(
			CreateScopedSnapshot([foreignBasket]));
		var service = TestServices.Transactions(repository);

		var operation = action == "void"
			? service.VoidAsync(
				foreignBasket.Id, "manager-1", "Attempted branch crossing")
			: service.RefundAsync(
				foreignBasket.Id, "manager-1", "Attempted branch crossing");
		await Assert.ThrowsAsync<KeyNotFoundException>(() => operation);

		Assert.Equal(
			status,
			Assert.Single(
				(await repository.LoadAsync()).TransactionBaskets).PaymentStatus);
	}

	[Fact]
	public async Task ProcessSaleAsync_UsesDomainRulesAndPersistsStockDeduction()
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		var transactionId = await CreateGoodsBasketAsync(service);

		await service.ProcessSaleAsync(transactionId, "manager-1", BranchOne);

		var snapshot = await repository.LoadAsync();
		Assert.Equal(
			TransactionPaymentStatus.Paid,
			Assert.Single(snapshot.TransactionBaskets).PaymentStatus);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task ProcessSaleAsync_WhenStaffIsNotAssignedToBranch_DoesNotPersistMutation()
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		var transactionId = await CreateGoodsBasketAsync(service);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.ProcessSaleAsync(transactionId, "clerk-2", BranchOne));

		var snapshot = await repository.LoadAsync();
		Assert.Equal(
			TransactionPaymentStatus.Open,
			Assert.Single(snapshot.TransactionBaskets).PaymentStatus);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task VoidAndRefundAsync_RequireManagerAndPersistReasons()
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		var voidBasket = new TransactionBasket(
			"INR", CreateTaxRates(), BranchOne);
		var initialSnapshot = await repository.LoadAsync();
		await repository.SaveAsync(new ApexPostSnapshot(
			initialSnapshot.Branches,
			initialSnapshot.Staff,
			initialSnapshot.Products,
			initialSnapshot.BranchStocks,
			initialSnapshot.ShippingItems,
			initialSnapshot.ShippingRates,
			initialSnapshot.MoneyOrders,
			initialSnapshot.IdentityVerifications,
			[voidBasket]));
		var voidId = voidBasket.Id;
		var refundId = await CreateGoodsBasketAsync(service);
		await service.ProcessSaleAsync(refundId, "manager-1", BranchOne);

		await service.VoidAsync(voidId, "manager-1", "Duplicate entry");
		await service.RefundAsync(refundId, "manager-1", "Customer request");

		var baskets = (await repository.LoadAsync()).TransactionBaskets;
		var voided = Assert.Single(baskets, item => item.Id == voidId);
		var refunded = Assert.Single(baskets, item => item.Id == refundId);
		Assert.Equal(TransactionPaymentStatus.Voided, voided.PaymentStatus);
		Assert.Equal(
			"Duplicate entry",
			Assert.IsType<TransactionVoidedNotification>(
				Assert.Single(voided.Notifications)).Reason);
		Assert.Equal(TransactionPaymentStatus.Refunded, refunded.PaymentStatus);
		Assert.Equal(
			"Customer request",
			Assert.IsType<TransactionRefundedNotification>(
				Assert.Single(refunded.Notifications)).Reason);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			service.VoidAsync(voidId, "clerk-1", "Unauthorized"));
		Assert.Equal(
			TransactionPaymentStatus.Voided,
			Assert.Single(
				(await repository.LoadAsync()).TransactionBaskets,
				item => item.Id == voidId).PaymentStatus);
	}

	[Fact]
	public async Task OperationsAsync_WhenTransactionDoesNotExist_ReportsMissingInput()
	{
		var service = TestServices.Transactions(
			new InMemoryApexPostRepository(CreateSnapshot()));

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			service.AddLineItemAsync(
				Guid.NewGuid(),
				new TransactionLineItem(
					"Service",
					TransactionItemType.Service,
					new Money(1m, "INR"),
					1,
					TaxCategory.Standard)));
	}

	[Fact]
	public async Task CreateBasketAsync_PropagatesCancellation()
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			service.CreateBasketAsync(
				"INR", CreateTaxRates(), cancellation.Token));
		Assert.Empty((await repository.LoadAsync()).TransactionBaskets);
	}

	[Theory]
	[InlineData("void")]
	[InlineData("refund")]
	public async Task StatusChangeAsync_PaidSaleReturnsStockOnce(string action)
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		var transactionId = await CreateGoodsBasketAsync(service);
		await service.ProcessSaleAsync(transactionId, "manager-1", BranchOne);
		Assert.Equal(
			2,
			Assert.Single((await repository.LoadAsync()).BranchStocks).Quantity);

		await (action == "void"
			? service.VoidAsync(transactionId, "manager-1", "Sale cancelled")
			: service.RefundAsync(transactionId, "manager-1", "Customer request"));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.VoidAsync(transactionId, "manager-1", "Repeated request"));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.RefundAsync(transactionId, "manager-1", "Repeated request"));
		var snapshot = await repository.LoadAsync();
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(
			action == "void"
				? TransactionPaymentStatus.Voided
				: TransactionPaymentStatus.Refunded,
			basket.PaymentStatus);
		Assert.Single(basket.Notifications);
	}

	[Fact]
	public async Task VoidAsync_WhenStockRecordIsMissing_PersistsNoChange()
	{
		var repository = new InMemoryApexPostRepository(CreateSnapshot());
		var service = TestServices.Transactions(repository);
		var transactionId = await CreateGoodsBasketAsync(service);
		await service.ProcessSaleAsync(transactionId, "manager-1", BranchOne);
		var soldSnapshot = await repository.LoadAsync();
		await repository.SaveAsync(new ApexPostSnapshot(
			soldSnapshot.Branches,
			soldSnapshot.Staff,
			soldSnapshot.Products,
			[],
			soldSnapshot.ShippingItems,
			soldSnapshot.ShippingRates,
			soldSnapshot.MoneyOrders,
			soldSnapshot.IdentityVerifications,
			soldSnapshot.TransactionBaskets));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.VoidAsync(transactionId, "manager-1", "Sale cancelled"));

		var basket = Assert.Single(
			(await repository.LoadAsync()).TransactionBaskets);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Empty(basket.Notifications);
	}

	private static async Task<Guid> CreateGoodsBasketAsync(
		TransactionApplicationService service)
	{
		var transactionId = await service.CreateBasketAsync(
			"INR", CreateTaxRates());
		await service.AddLineItemAsync(
			transactionId,
			new TransactionLineItem(
				"Box",
				TransactionItemType.Goods,
				new Money(20m, "INR"),
				2,
				TaxCategory.Standard,
				ProductSku));
		return transactionId;
	}

	private static Dictionary<TaxCategory, decimal> CreateTaxRates() => new()
	{
		[TaxCategory.Standard] = 0m
	};

	private static TransactionBasket CreateScopedBasket(
		Guid id,
		BranchCode? branchCode,
		TransactionPaymentStatus status)
	{
		var lineItems = new[]
		{
			new TransactionLineItem(
				"Counter service",
				TransactionItemType.Service,
				new Money(10m, "INR"),
				1,
				TaxCategory.Standard)
		};
		return TransactionBasket.Reconstitute(
			id,
			"INR",
			new Dictionary<TaxCategory, decimal>
			{
				[TaxCategory.Standard] = 0m
			},
			lineItems,
			status,
			[],
			branchCode);
	}

	private static ApexPostSnapshot CreateScopedSnapshot(
		IEnumerable<TransactionBasket> baskets,
		IEnumerable<Staff>? staff = null)
	{
		var defaultStaff = new[]
		{
			CreateStaff("manager-1", StaffRole.BranchManager, BranchOne),
			CreateStaff("manager-2", StaffRole.BranchManager, BranchTwo)
		};
		return new ApexPostSnapshot(
			[CreateBranch(BranchOne), CreateBranch(BranchTwo)],
			staff ?? defaultStaff,
			[],
			[],
			[],
			[],
			[],
			[],
			baskets);
	}

	private static InMemoryApexPostRepository CreateMixedBasketRepository(
		ShippingRate? shippingRate = null,
		bool deactivateClerk = false,
		bool closeHomeBranch = false)
	{
		var snapshot = CreateSnapshot();
		if (deactivateClerk)
			snapshot.Staff.Single(staff => staff.Id == "clerk-1").Deactivate();
		if (closeHomeBranch)
			snapshot.Branches.Single(branch => branch.Code == BranchOne).Close();
		return new InMemoryApexPostRepository(new ApexPostSnapshot(
			snapshot.Branches,
			snapshot.Staff,
			snapshot.Products,
			snapshot.BranchStocks,
			[],
			[shippingRate ?? new ShippingRate(
				"ZONE-A",
				ShippingServiceLevel.Standard,
				new Money(10m, "INR"),
				new Money(2m, "INR"))],
			[],
			[],
			[]));
	}

	private static MixedBasketApplicationService CreateMixedBasketService(
		InMemoryApexPostRepository repository,
		IReadOnlyDictionary<ShippingServiceLevel, Weight>? maximumWeights = null) =>
		TestServices.MixedBasket(
			repository,
			new Dictionary<TaxCategory, decimal>
			{
				[TaxCategory.Standard] = 0.1m
			},
			maximumWeights ??
				new Dictionary<ShippingServiceLevel, Weight>
				{
					[ShippingServiceLevel.Standard] = new Weight(5m)
				},
			TaxCategory.Standard);

	private static ShippingBasketRequest CreateShippingRequest(
		ShippingItemType type = ShippingItemType.Parcel,
		decimal weightKilograms = 3m,
		ShippingServiceLevel serviceLevel = ShippingServiceLevel.Standard) =>
		new(
			type,
			weightKilograms,
			10m,
			20m,
			30m,
			"ZONE-A",
			serviceLevel);

	private static ApexPostSnapshot CreateSnapshot()
	{
		var branchOne = CreateBranch(BranchOne);
		var branchTwo = CreateBranch(BranchTwo);
		return new ApexPostSnapshot(
			[branchOne, branchTwo],
			[
				CreateStaff("manager-1", StaffRole.BranchManager, BranchOne),
				CreateStaff("clerk-1", StaffRole.CounterClerk, BranchOne),
				CreateStaff("clerk-2", StaffRole.CounterClerk, BranchTwo)
			],
			[
				new Product(
					ProductSku,
					"Box",
					ProductCategory.Packaging,
					new Money(20m, "INR"),
					TaxCategory.Standard)
			],
			[new BranchStock(BranchOne, ProductSku, 4)],
			[],
			[],
			[],
			[],
			[]);
	}

	private static Branch CreateBranch(BranchCode code) =>
		new(
			code,
			$"Branch {code}",
			"Region",
			"09:00-17:00",
			[new CounterTerminal($"COUNTER-{code}", "Counter")]);

	private static Staff CreateStaff(
		string id,
		StaffRole role,
		BranchCode branchCode) =>
		new(id, id, $"{id}@example.test", role, branchCode);

	private sealed class InMemoryApexPostRepository : IApexPostRepository
	{
		private readonly SemaphoreSlim _gate = new(1, 1);
		private ApexPostSnapshot _snapshot;

		public InMemoryApexPostRepository(ApexPostSnapshot snapshot) =>
			_snapshot = Clone(snapshot);

		public async Task<ApexPostSnapshot> LoadAsync(
			CancellationToken cancellationToken = default)
		{
			await _gate.WaitAsync(cancellationToken);
			try
			{
				return Clone(_snapshot);
			}
			finally
			{
				_gate.Release();
			}
		}

		public async Task SaveAsync(
			ApexPostSnapshot snapshot,
			CancellationToken cancellationToken = default)
		{
			await _gate.WaitAsync(cancellationToken);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				_snapshot = Clone(snapshot);
			}
			finally
			{
				_gate.Release();
			}
		}

		public async Task UpdateAsync(
			Func<ApexPostSnapshot, ApexPostSnapshot> update,
			CancellationToken cancellationToken = default)
		{
			await _gate.WaitAsync(cancellationToken);
			try
			{
				var updated = update(Clone(_snapshot));
				cancellationToken.ThrowIfCancellationRequested();
				_snapshot = Clone(updated);
			}
			finally
			{
				_gate.Release();
			}
		}

		private static ApexPostSnapshot Clone(ApexPostSnapshot snapshot)
		{
			var branches = snapshot.Branches.Select(branch =>
			{
				var terminals = branch.CounterTerminals.Select(terminal =>
				{
					var copy = new CounterTerminal(terminal.Id, terminal.Name);
					if (!terminal.IsActive)
						copy.Deactivate();
					return copy;
				});
				var copy = new Branch(
					branch.Code,
					branch.Name,
					branch.Region,
					branch.OpeningHours,
					terminals);
				if (!branch.IsActive)
					copy.Close();
				return copy;
			});
			var staff = snapshot.Staff.Select(person =>
			{
				var copy = new Staff(
					person.Id,
					person.Name,
					person.Email,
					person.Role,
					person.HomeBranchCode);
				if (!person.IsActive)
					copy.Deactivate();
				return copy;
			});
			var stocks = snapshot.BranchStocks.Select(stock =>
				new BranchStock(
					stock.BranchCode, stock.ProductSku, stock.Quantity));
			var baskets = snapshot.TransactionBaskets.Select(basket =>
			{
				var state = basket.GetSnapshot();
				return TransactionBasket.Reconstitute(
					state.Id,
					state.Currency,
					state.TaxRates,
					state.LineItems,
					state.PaymentStatus,
					state.Notifications,
					state.BranchCode);
			});

			return new ApexPostSnapshot(
				branches,
				staff,
				snapshot.Products,
				stocks,
				snapshot.ShippingItems,
				snapshot.ShippingRates,
				snapshot.MoneyOrders,
				snapshot.IdentityVerifications,
				baskets);
		}
	}
}
