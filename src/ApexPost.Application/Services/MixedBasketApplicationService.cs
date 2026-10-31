using ApexPost.Application.Events;
using ApexPost.Application.Validation;
using ApexPost.Application.Verification;
using ApexPost.Domain.Events;
using ApexPost.Domain.Services;

namespace ApexPost.Application.Services;

public sealed record GoodsBasketRequest(
	[property: NotBlank] string Sku,
	[property: GreaterThanZero] int Quantity);

// Weight and dimensions are validated by the Weight and Dimensions value
// objects; the request only checks what the domain types cannot.
public sealed record ShippingBasketRequest(
	[property: DefinedEnum] ShippingItemType Type,
	decimal WeightKilograms,
	decimal LengthCm,
	decimal WidthCm,
	decimal HeightCm,
	[property: NotBlank, MaximumLength(InputLimits.ZoneCode)] string DestinationZone,
	[property: DefinedEnum] ShippingServiceLevel ServiceLevel);

// A complete counter sale from the transaction screen: any number of goods,
// shipping and money-order lines, taken at one counter with one payment method.
public sealed record CounterBasketRequest(
	Guid TransactionId,
	[property: NotBlank, MaximumLength(InputLimits.CounterId)] string CounterTerminalId,
	[property: NotBlank, MaximumLength(InputLimits.PaymentMethod)] string PaymentMethod,
	IReadOnlyList<GoodsBasketRequest> Goods,
	IReadOnlyList<ShippingBasketRequest> Shipments,
	IReadOnlyList<MoneyOrderBasketRequest> MoneyOrders);

public sealed record MixedBasketCatalog(
	IReadOnlyList<Product> Products,
	Branch Branch,
	IReadOnlyList<ShippingRate> ShippingRates,
	MoneyOrderBasketSettings? MoneyOrders = null);

public sealed class MixedBasketApplicationService
{
	private const int MaximumSkuLength = 30;

	private readonly IStaffRepository _staff;
	private readonly IBranchRepository _branches;
	private readonly IProductRepository _products;
	private readonly IStockRepository _stock;
	private readonly ICounterTransactionRepository _transactions;
	private readonly IUnitOfWork _unitOfWork;
	private readonly IReadOnlyDictionary<TaxCategory, decimal> _taxRates;
	private readonly IReadOnlyDictionary<ShippingServiceLevel, Weight>
		_maximumParcelWeights;
	private readonly TaxCategory? _shippingTaxCategory;
	private readonly TransactionProcessingRule _processingRule = new();
	private readonly TimeProvider _timeProvider;
	private readonly MoneyOrderBasketSettings? _moneyOrderSettings;
	private readonly MoneyOrderIdentityVerificationRule? _moneyOrderRule;
	private readonly IIdentityVerificationService? _identityVerification;
	private readonly HighValueMoneyOrderNotificationRule? _highValueRule;
	private readonly DomainEventPublisher? _events;
	private readonly int? _lowStockThreshold;
	private readonly IReadOnlyList<string> _paymentMethods;

	public MixedBasketApplicationService(
		IStaffRepository staff,
		IBranchRepository branches,
		IProductRepository products,
		IStockRepository stock,
		ICounterTransactionRepository transactions,
		IUnitOfWork unitOfWork,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		IReadOnlyDictionary<ShippingServiceLevel, Weight> maximumParcelWeights,
		TaxCategory? shippingTaxCategory = null,
		TimeProvider? timeProvider = null,
		MoneyOrderBasketSettings? moneyOrderSettings = null,
		IIdentityVerificationService? identityVerification = null,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null,
		IReadOnlyCollection<string>? paymentMethods = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(branches);
		ArgumentNullException.ThrowIfNull(products);
		ArgumentNullException.ThrowIfNull(stock);
		ArgumentNullException.ThrowIfNull(transactions);
		ArgumentNullException.ThrowIfNull(unitOfWork);
		ArgumentNullException.ThrowIfNull(taxRates);
		ArgumentNullException.ThrowIfNull(maximumParcelWeights);
		_staff = staff;
		_branches = branches;
		_products = products;
		_stock = stock;
		_transactions = transactions;
		_unitOfWork = unitOfWork;
		_timeProvider = timeProvider ?? TimeProvider.System;
		var validatedTaxRates = new Dictionary<TaxCategory, decimal>();
		foreach (var (category, rate) in taxRates)
		{
			if (!Enum.IsDefined(category))
				throw new ArgumentOutOfRangeException(nameof(taxRates));
			if (rate < 0m)
				throw new ArgumentOutOfRangeException(
					nameof(taxRates), "Tax rates cannot be negative.");
			validatedTaxRates.Add(category, rate);
		}
		_taxRates = validatedTaxRates;

		var validatedMaximumWeights =
			new Dictionary<ShippingServiceLevel, Weight>();
		foreach (var (serviceLevel, maximumWeight) in maximumParcelWeights)
		{
			if (!Enum.IsDefined(serviceLevel))
				throw new ArgumentOutOfRangeException(
					nameof(maximumParcelWeights));
			ArgumentNullException.ThrowIfNull(maximumWeight);
			validatedMaximumWeights.Add(serviceLevel, maximumWeight);
		}
		_maximumParcelWeights = validatedMaximumWeights;
		if (shippingTaxCategory is not null &&
			!Enum.IsDefined(shippingTaxCategory.Value))
			throw new ArgumentOutOfRangeException(nameof(shippingTaxCategory));
		_shippingTaxCategory = shippingTaxCategory;
		_moneyOrderSettings = moneyOrderSettings;
		_moneyOrderRule = moneyOrderSettings is null
			? null
			: new MoneyOrderIdentityVerificationRule(
				moneyOrderSettings.HighValueThreshold);
		_identityVerification = identityVerification;
		_highValueRule = moneyOrderSettings is null
			? null
			: new HighValueMoneyOrderNotificationRule(
				moneyOrderSettings.HighValueThreshold);
		_events = events;
		if (lowStockThreshold < 0)
			throw new ArgumentOutOfRangeException(
				nameof(lowStockThreshold), "Low-stock threshold cannot be negative.");
		_lowStockThreshold = lowStockThreshold;

		var validatedPaymentMethods = new List<string>();
		foreach (var method in paymentMethods ?? [])
		{
			if (string.IsNullOrWhiteSpace(method))
				throw new ArgumentException(
					"Payment method names cannot be blank.", nameof(paymentMethods));
			var name = method.Trim();
			if (validatedPaymentMethods.Contains(
				name, StringComparer.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					$"Payment method '{name}' is configured more than once.",
					nameof(paymentMethods));
			}
			validatedPaymentMethods.Add(name);
		}
		_paymentMethods = validatedPaymentMethods.AsReadOnly();
	}

	public bool HasTaxRates =>
		_taxRates.Count > 0;

	// Configured payment method names; counter sales cannot be taken until
	// at least one is configured.
	public IReadOnlyList<string> PaymentMethods => _paymentMethods;

	private string ResolvePaymentMethod(string? paymentMethod)
	{
		if (_paymentMethods.Count == 0)
			throw new InvalidOperationException(
				"No payment methods are configured.");
		if (string.IsNullOrWhiteSpace(paymentMethod))
			throw new ArgumentException(
				"Select a payment method.", nameof(paymentMethod));

		return _paymentMethods.FirstOrDefault(configured => string.Equals(
				configured, paymentMethod.Trim(), StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException(
				"Select a configured payment method.", nameof(paymentMethod));
	}

	// Money orders need configured settings and a tax rate for the fee.
	public bool MoneyOrdersAvailable =>
		_moneyOrderSettings is not null &&
		_taxRates.ContainsKey(_moneyOrderSettings.FeeTaxCategory);

	public async Task<MixedBasketCatalog> GetCatalogAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindActiveSalesStaffAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		var branch = await FindBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		if (!branch.IsActive)
			throw new InvalidOperationException(
				"Transactions cannot be processed at an inactive branch.");

		var products = await _products.GetProductsAsync(cancellationToken)
			.ConfigureAwait(false);
		var shippingRates = await _products.GetShippingRatesAsync(cancellationToken)
			.ConfigureAwait(false);
		return new MixedBasketCatalog(
			Array.AsReadOnly(products
				.Where(product => _taxRates.ContainsKey(product.TaxCategory))
				.ToArray()),
			branch,
			Array.AsReadOnly(shippingRates
				.Where(_ => _shippingTaxCategory is not null &&
					_taxRates.ContainsKey(_shippingTaxCategory.Value))
				.ToArray()),
			MoneyOrdersAvailable ? _moneyOrderSettings : null);
	}

	// Records no counter, clerk, or payment method; counter sales use the
	// CounterBasketRequest overload.
	public Task<Guid> CreateAndProcessAsync(
		Guid transactionId,
		string staffId,
		IReadOnlyList<GoodsBasketRequest> goods,
		IReadOnlyList<ShippingBasketRequest> shipments,
		CancellationToken cancellationToken = default) =>
		ProcessAsync(
			transactionId,
			staffId,
			goods,
			shipments,
			[],
			counterTerminalId: null,
			paymentMethod: null,
			cancellationToken);

	public Task<Guid> CreateAndProcessAsync(
		string staffId,
		CounterBasketRequest request,
		CancellationToken cancellationToken = default)
	{
		RequestValidator.Validate(request);

		return ProcessAsync(
			request.TransactionId,
			staffId,
			request.Goods,
			request.Shipments,
			request.MoneyOrders,
			request.CounterTerminalId,
			ResolvePaymentMethod(request.PaymentMethod),
			cancellationToken);
	}

	private async Task<Guid> ProcessAsync(
		Guid transactionId,
		string staffId,
		IReadOnlyList<GoodsBasketRequest> goods,
		IReadOnlyList<ShippingBasketRequest> shipments,
		IReadOnlyList<MoneyOrderBasketRequest> moneyOrders,
		string? counterTerminalId,
		string? paymentMethod,
		CancellationToken cancellationToken)
	{
		if (transactionId == Guid.Empty)
			throw new ArgumentException(
				"Transaction ID cannot be empty.", nameof(transactionId));
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"Authenticated staff are required to process a basket.");
		ArgumentNullException.ThrowIfNull(goods);
		ArgumentNullException.ThrowIfNull(shipments);
		ArgumentNullException.ThrowIfNull(moneyOrders);
		if (goods.Count == 0 && shipments.Count == 0 && moneyOrders.Count == 0)
			throw new ArgumentException(
				"A transaction must contain at least one goods, shipping, or money-order item.");
		RequestValidator.ValidateAll(goods);
		RequestValidator.ValidateAll(shipments);
		RequestValidator.ValidateAll(moneyOrders);
		foreach (var shipment in shipments)
		{
			InputLimits.Ensure(
				shipment.WeightKilograms, InputLimits.WeightKilograms, "Weight");
			InputLimits.Ensure(shipment.LengthCm, InputLimits.DimensionCm, "Length");
			InputLimits.Ensure(shipment.WidthCm, InputLimits.DimensionCm, "Width");
			InputLimits.Ensure(shipment.HeightCm, InputLimits.DimensionCm, "Height");
		}
		foreach (var moneyOrder in moneyOrders)
		{
			InputLimits.Ensure(
				moneyOrder.Amount, InputLimits.MoneyAmount, "Money order amount");
			// A cross-field rule, so it stays explicit rather than an attribute.
			if (string.IsNullOrWhiteSpace(moneyOrder.DocumentType) !=
				string.IsNullOrWhiteSpace(moneyOrder.DocumentIdentifier))
			{
				throw new ArgumentException(
					"Enter both the identity document type and number, or neither.",
					nameof(moneyOrders));
			}
		}
		if (moneyOrders.Count > 0 && !MoneyOrdersAvailable)
			throw new InvalidOperationException(
				"Money orders are not configured.");

		// The verification provider is asynchronous and possibly slow, so it
		// runs before the transaction starts. The decision is applied inside
		// the unit of work below, and a retry keeps it: it comes from the
		// provider, not from the database state that a conflict makes stale.
		var verificationResults = await VerifyIdentitiesAsync(
			staffId, moneyOrders, cancellationToken).ConfigureAwait(false);

		// Replaced on every attempt; published only once a commit has succeeded.
		IReadOnlyList<DomainEvent> committedEvents = [];
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			if (await _transactions.ExistsAsync(transactionId, token)
				.ConfigureAwait(false))
			{
				throw new InvalidOperationException(
					"This transaction submission has already been processed.");
			}

			var staff = await FindActiveSalesStaffAsync(staffId, token)
				.ConfigureAwait(false);
			var branch = await FindBranchAsync(staff.HomeBranchCode, token)
				.ConfigureAwait(false);
			var catalogue = await _products.FindBySkusAsync(
				goods.Select(request => ToSkuOrNull(request.Sku))
					.OfType<Sku>()
					.Distinct()
					.ToArray(),
				token).ConfigureAwait(false);
			var shippingRates = await _products.GetShippingRatesAsync(token)
				.ConfigureAwait(false);
			var occurredAtUtc = _timeProvider.GetUtcNow();
			// The clerk is the authenticated staff member; the domain rule
			// checks the counter belongs to their branch and is active.
			var basket = new TransactionBasket(
				transactionId,
				ResolveCurrency(
					catalogue, shippingRates, goods, shipments, moneyOrders),
				_taxRates,
				staff.HomeBranchCode,
				occurredAtUtc,
				counterTerminalId is null
					? null
					: new CounterSaleDetails(
						counterTerminalId, staff.Id, paymentMethod!));

			var issuedMoneyOrders = new List<MoneyOrder>(moneyOrders.Count);
			for (var index = 0; index < moneyOrders.Count; index++)
			{
				var moneyOrder = CreateMoneyOrder(
					moneyOrders[index],
					verificationResults[index],
					staff.HomeBranchCode);
				basket.AddLineItem(new TransactionLineItem(moneyOrder));
				basket.AddLineItem(new TransactionLineItem(
					"Money order fee",
					TransactionItemType.Service,
					moneyOrder.Fee,
					1,
					_moneyOrderSettings!.FeeTaxCategory));
				issuedMoneyOrders.Add(moneyOrder);
			}

			foreach (var goodsRequest in goods)
			{
				var product = FindProduct(catalogue, goodsRequest.Sku);
				basket.AddLineItem(new TransactionLineItem(
					product, goodsRequest.Quantity));
			}

			var pricingService = new ShippingPricingService(shippingRates);
			var shippingItems = new List<ShippingItem>(shipments.Count);
			var weightRule = new ShippingWeightLimitRule(
				_maximumParcelWeights);

			foreach (var shipment in shipments)
			{
				var shippingTaxCategory = _shippingTaxCategory
					?? throw new InvalidOperationException(
						"No shipping tax category is configured.");
				var shippingItem = new ShippingItem(
					Guid.NewGuid().ToString("N"),
					shipment.Type,
					new Weight(shipment.WeightKilograms),
					new Dimensions(
						shipment.LengthCm,
						shipment.WidthCm,
						shipment.HeightCm),
					shipment.DestinationZone,
					shipment.ServiceLevel);
				weightRule.EnsureWithinLimit(shippingItem);

				var price = pricingService.CalculatePrice(shippingItem);
				if (!string.Equals(
					price.Currency, basket.Currency, StringComparison.Ordinal))
				{
					throw new InvalidOperationException(
						"Shipping rate currency must match the transaction currency.");
				}

				basket.AddLineItem(new TransactionLineItem(
					$"Shipping {shippingItem.Type} to {shippingItem.DestinationZone} ({shippingItem.ServiceLevel})",
					TransactionItemType.Service,
					price,
					1,
					shippingTaxCategory,
					productSku: null,
					shippingItem: shippingItem));
				shippingItems.Add(shippingItem);
			}

			var soldSkus = basket.LineItems
				.Select(lineItem => lineItem.ProductSku)
				.OfType<Sku>()
				.Distinct()
				.ToArray();
			var branchStocks = await _stock
				.GetForProductsAsync(branch.Code, soldSkus, token)
				.ConfigureAwait(false);
			var quantitiesBefore = LowStockEvents.CaptureQuantities(
				branchStocks, branch.Code, soldSkus);
			_processingRule.Process(staff, branch, basket, branchStocks);
			var basketTotal = basket.Total;
			if (basketTotal.Amount <= 0m)
				throw new InvalidOperationException(
					"Transaction total must be greater than zero.");
			InputLimits.Ensure(
				basketTotal.Amount, InputLimits.MoneyAmount, "Transaction total");

			committedEvents = CreateSaleEvents(
				basket,
				staffId,
				issuedMoneyOrders,
				branchStocks,
				quantitiesBefore,
				occurredAtUtc);
			_transactions.Add(basket);
			if (paymentMethod is not null)
			{
				_transactions.AddPayment(new Payment(
					Guid.NewGuid(),
					basket.Id,
					PaymentEntryType.Payment,
					basketTotal,
					paymentMethod,
					occurredAtUtc));
			}

			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		_events?.Publish(committedEvents);
		return transactionId;
	}

	private IReadOnlyList<DomainEvent> CreateSaleEvents(
		TransactionBasket basket,
		string staffId,
		IReadOnlyList<MoneyOrder> issuedMoneyOrders,
		IEnumerable<BranchStock> branchStocks,
		IReadOnlyDictionary<Sku, int> quantitiesBefore,
		DateTimeOffset occurredAtUtc)
	{
		var branchCode = basket.BranchCode!;
		var events = new List<DomainEvent>
		{
			new TransactionCompletedEvent(
				basket.Id, branchCode, staffId, basket.Total, occurredAtUtc)
		};

		events.AddRange(LowStockEvents.Evaluate(
			branchStocks,
			branchCode,
			quantitiesBefore,
			_lowStockThreshold,
			occurredAtUtc));

		foreach (var moneyOrder in issuedMoneyOrders)
		{
			events.Add(new MoneyOrderIssuedEvent(
				moneyOrder.Id,
				basket.Id,
				branchCode,
				moneyOrder.Amount,
				occurredAtUtc));
			if (_highValueRule?.Evaluate(moneyOrder) is { } highValue)
			{
				events.Add(new HighValueMoneyOrderIssuedEvent(
					moneyOrder.Id, basket.Id, highValue, occurredAtUtc));
			}
		}

		return events;
	}

	private async Task<bool?[]> VerifyIdentitiesAsync(
		string staffId,
		IReadOnlyList<MoneyOrderBasketRequest> moneyOrders,
		CancellationToken cancellationToken)
	{
		var results = new bool?[moneyOrders.Count];
		if (moneyOrders.All(order => string.IsNullOrWhiteSpace(order.DocumentType)))
			return results;

		if (_identityVerification is null)
			throw new IdentityVerificationUnavailableException(
				"Identity verification is not configured.");

		// Only active sales staff may use the verification provider. The
		// unit of work re-checks staff authoritatively.
		_ = await FindActiveSalesStaffAsync(staffId, cancellationToken)
			.ConfigureAwait(false);

		for (var index = 0; index < moneyOrders.Count; index++)
		{
			var request = moneyOrders[index];
			if (string.IsNullOrWhiteSpace(request.DocumentType))
				continue;

			results[index] = await _identityVerification.VerifyAsync(
				request.DocumentType,
				request.DocumentIdentifier!,
				cancellationToken).ConfigureAwait(false);
		}

		return results;
	}

	private MoneyOrder CreateMoneyOrder(
		MoneyOrderBasketRequest request,
		bool? verificationResult,
		BranchCode branchCode)
	{
		var settings = _moneyOrderSettings!;
		// The full document identifier is masked by IdentityVerification and
		// is not kept anywhere else.
		var verification = verificationResult is bool isSuccessful
			? new IdentityVerification(
				request.DocumentType!,
				request.DocumentIdentifier!,
				isSuccessful)
			: null;
		var moneyOrder = new MoneyOrder(
			new Money(request.Amount, settings.HighValueThreshold.Currency),
			request.Sender,
			request.Recipient,
			settings.Fee,
			verification,
			branchCode);
		_moneyOrderRule!.EnsureVerification(moneyOrder, verification);
		return moneyOrder;
	}

	// A request can name a SKU no product could have; FindProduct then reports
	// it as not found.
	private static Sku? ToSkuOrNull(string sku) =>
		string.IsNullOrWhiteSpace(sku) || sku.Trim().Length > MaximumSkuLength
			? null
			: new Sku(sku);

	private string ResolveCurrency(
		IReadOnlyList<Product> catalogue,
		IReadOnlyList<ShippingRate> shippingRates,
		IReadOnlyList<GoodsBasketRequest> goods,
		IReadOnlyList<ShippingBasketRequest> shipments,
		IReadOnlyList<MoneyOrderBasketRequest> moneyOrders)
	{
		var currency = goods.Count > 0
			? FindProduct(catalogue, goods[0].Sku).UnitPrice.Currency
			: shipments.Count > 0
				? FindRate(shippingRates, shipments[0]).BasePrice.Currency
				: _moneyOrderSettings!.HighValueThreshold.Currency;

		if (moneyOrders.Count > 0 &&
			!string.Equals(
				_moneyOrderSettings!.HighValueThreshold.Currency,
				currency,
				StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"All transaction items must use the same currency.");
		}

		foreach (var goodsRequest in goods)
		{
			if (!string.Equals(
				FindProduct(catalogue, goodsRequest.Sku).UnitPrice.Currency,
				currency,
				StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					"All transaction items must use the same currency.");
			}
		}

		foreach (var shippingRequest in shipments)
		{
			if (!string.Equals(
				FindRate(shippingRates, shippingRequest).BasePrice.Currency,
				currency,
				StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					"All transaction items must use the same currency.");
			}
		}

		return currency;
	}

	private static Product FindProduct(
		IReadOnlyList<Product> catalogue,
		string sku)
	{
		if (string.IsNullOrWhiteSpace(sku))
			throw new ArgumentException("Product SKU is required.", nameof(sku));
		var matches = catalogue.Where(product =>
			string.Equals(product.Sku.Value, sku, StringComparison.Ordinal))
			.Take(2).ToArray();
		return matches.Length switch
		{
			1 => matches[0],
			0 => throw new KeyNotFoundException(
				$"Product '{sku}' was not found."),
			_ => throw new InvalidOperationException(
				$"Multiple products match SKU '{sku}'.")
		};
	}

	private async Task<Staff> FindActiveSalesStaffAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"Authenticated staff are required to process transactions.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not
			{
				IsActive: true,
				Role: StaffRole.CounterClerk or StaffRole.BranchManager
			})
		{
			throw new UnauthorizedAccessException(
				"An active Counter Clerk or Branch Manager is required to process transactions.");
		}

		return staff;
	}

	private async Task<Branch> FindBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken) =>
		await _branches.FindByCodeAsync(branchCode, cancellationToken)
			.ConfigureAwait(false)
		?? throw new KeyNotFoundException($"Branch '{branchCode}' was not found.");

	private static ShippingRate FindRate(
		IReadOnlyList<ShippingRate> shippingRates,
		ShippingBasketRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.DestinationZone))
			throw new ArgumentException(
				"Destination zone is required.", nameof(request));

		return shippingRates.SingleOrDefault(rate =>
			string.Equals(
				rate.DestinationZone,
				request.DestinationZone.Trim(),
				StringComparison.Ordinal) &&
			rate.ServiceLevel == request.ServiceLevel)
			?? throw new InvalidOperationException(
				"No configured shipping rate matches the destination and service level.");
	}
}
