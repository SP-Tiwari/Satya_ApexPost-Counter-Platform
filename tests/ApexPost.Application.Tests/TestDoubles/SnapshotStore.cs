using System.Runtime.CompilerServices;
using ApexPost.Application.Caching;

namespace ApexPost.Application.Tests.TestDoubles;

// In-memory repositories and unit of work over the snapshot fake, so service
// tests need no database. A use case holds a gate from its first read to its
// commit. Payments and adjustments are recorded for tests to inspect.
internal sealed class SnapshotStore
	: IBranchRepository,
		IProductRepository,
		IStockRepository,
		ICounterTransactionRepository,
		IStaffRepository,
		IUnitOfWork,
		IReferenceDataSource
{
	private static readonly ConditionalWeakTable<IApexPostRepository, SemaphoreSlim>
		Gates = new();

	private readonly IApexPostRepository _repository;
	private readonly SemaphoreSlim _gate;

	// Flows with the use case, so a service shared between concurrent callers
	// keeps each caller's working set apart.
	private readonly AsyncLocal<WorkingSet?> _current = new();

	public SnapshotStore(IApexPostRepository repository)
	{
		ArgumentNullException.ThrowIfNull(repository);
		_repository = repository;
		_gate = Gates.GetValue(repository, _ => new SemaphoreSlim(1, 1));
	}

	public async Task<TResult> ExecuteInTransactionAsync<TResult>(
		Func<CancellationToken, Task<TResult>> work,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(work);

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_current.Value = new WorkingSet(
				await _repository.LoadAsync(cancellationToken).ConfigureAwait(false));
			return await work(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_current.Value = null;
			_gate.Release();
		}
	}

	public Task SaveChangesAsync(CancellationToken cancellationToken = default)
	{
		var working = _current.Value ?? throw new InvalidOperationException(
			"Changes can be saved only inside ExecuteInTransactionAsync.");
		return _repository.SaveAsync(working.ToSnapshot(), cancellationToken);
	}

	public Task<Branch?> FindByCodeAsync(
		BranchCode code,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Single(set.Branches.Where(branch => branch.Code == code)),
			cancellationToken);

	public void Add(Branch branch) => Working.Branches.Add(branch);

	public Task<Product?> FindBySkuAsync(
		Sku sku,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Single(set.Products.Where(product => product.Sku == sku)),
			cancellationToken);

	public Task<IReadOnlyList<Product>> FindBySkusAsync(
		IReadOnlyCollection<Sku> skus,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Copy(set.Products.Where(product => skus.Contains(product.Sku))),
			cancellationToken);

	public Task<IReadOnlyList<Product>> GetProductsAsync(
		CancellationToken cancellationToken = default) =>
		ReadAsync(set => Copy(set.Products), cancellationToken);

	public Task<IReadOnlyList<ShippingRate>> GetShippingRatesAsync(
		CancellationToken cancellationToken = default) =>
		ReadAsync(set => Copy(set.ShippingRates), cancellationToken);

	public void Add(Product product) => Working.Products.Add(product);

	public Task<int> RevisePricesAsync(
		ProductCategory category,
		decimal multiplier,
		CancellationToken cancellationToken = default)
	{
		var working = Working;
		var changed = 0;
		for (var index = 0; index < working.Products.Count; index++)
		{
			var product = working.Products[index];
			if (product.Category != category)
				continue;

			working.Products[index] = new Product(
				product.Sku,
				product.Name,
				product.Category,
				new Money(
					Math.Round(
						product.UnitPrice.Amount * multiplier,
						2,
						MidpointRounding.AwayFromZero),
					product.UnitPrice.Currency),
				product.TaxCategory);
			changed++;
		}

		return Task.FromResult(changed);
	}

	public Task<BranchStock?> FindAsync(
		BranchCode branchCode,
		Sku sku,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Single(set.BranchStocks.Where(stock =>
				stock.BranchCode == branchCode && stock.ProductSku == sku)),
			cancellationToken);

	public Task<IReadOnlyList<BranchStock>> GetForProductsAsync(
		BranchCode branchCode,
		IReadOnlyCollection<Sku> skus,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Copy(set.BranchStocks.Where(stock =>
				stock.BranchCode == branchCode && skus.Contains(stock.ProductSku))),
			cancellationToken);

	Task<IReadOnlyList<BranchStock>> IStockRepository.GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken) =>
		ReadAsync(
			set => Copy(set.BranchStocks.Where(
				stock => stock.BranchCode == branchCode)),
			cancellationToken);

	public List<StockAdjustment> Adjustments { get; } = [];

	public List<Payment> Payments { get; } = [];

	public void AddAdjustment(StockAdjustment adjustment)
	{
		ArgumentNullException.ThrowIfNull(adjustment);
		Adjustments.Add(adjustment);
	}

	public Task<bool> ExistsAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => set.Baskets.Any(basket => basket.Id == transactionId),
			cancellationToken);

	public Task<TransactionBasket?> FindAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Single(set.Baskets.Where(basket => basket.Id == transactionId)),
			cancellationToken);

	Task<IReadOnlyList<TransactionBasket>> ICounterTransactionRepository.GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken) =>
		ReadAsync(
			set => Copy(set.Baskets.Where(basket => basket.BranchCode == branchCode)),
			cancellationToken);

	public Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Copy(set.MoneyOrders.Where(
				order => order.ProcessingBranchCode == branchCode)),
			cancellationToken);

	public void Add(TransactionBasket basket)
	{
		ArgumentNullException.ThrowIfNull(basket);

		var working = Working;
		foreach (var lineItem in basket.LineItems)
		{
			if (lineItem.ShippingItem is { } shippingItem)
				working.ShippingItems.Add(shippingItem);

			if (lineItem.MoneyOrder is { } moneyOrder)
			{
				working.MoneyOrders.Add(moneyOrder);
				if (moneyOrder.IdentityVerification is { } verification)
					working.IdentityVerifications.Add(verification);
			}
		}

		working.Baskets.Add(basket);
	}

	public void AddPayment(Payment payment)
	{
		ArgumentNullException.ThrowIfNull(payment);
		Payments.Add(payment);
	}

	public Task<Payment?> FindPaymentAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		Task.FromResult(Payments.SingleOrDefault(
			payment => payment.TransactionId == transactionId &&
				payment.Type == PaymentEntryType.Payment));

	public Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersAsync(
		CancellationToken cancellationToken = default) =>
		ReadAsync(set => Copy(set.MoneyOrders), cancellationToken);

	public Task<IReadOnlyList<ShippingItem>> GetShipmentsAsync(
		CancellationToken cancellationToken = default) =>
		ReadAsync(set => Copy(set.ShippingItems), cancellationToken);

	public Task<TransactionBasket?> FindByShipmentIdAsync(
		string shipmentId,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Single(set.Baskets.Where(basket => basket.LineItems.Any(
				line => line.ShippingItem?.Id == shipmentId))),
			cancellationToken);

	public async Task<(IReadOnlyList<Product> Products, IReadOnlyList<ShippingRate> ShippingRates)>
		LoadAsync(CancellationToken cancellationToken = default)
	{
		var products = await GetProductsAsync(cancellationToken).ConfigureAwait(false);
		var rates = await GetShippingRatesAsync(cancellationToken).ConfigureAwait(false);
		return (products, rates);
	}

	public Task<Staff?> FindByIdAsync(
		string staffId,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => Single(set.Staff.Where(person => string.Equals(
				person.Id, staffId, StringComparison.Ordinal))),
			cancellationToken);

	public Task<bool> EmailExistsAsync(
		string email,
		CancellationToken cancellationToken = default) =>
		ReadAsync(
			set => set.Staff.Any(person => string.Equals(
				person.Email, email, StringComparison.OrdinalIgnoreCase)),
			cancellationToken);

	public Task<IReadOnlyList<Staff>> GetStaffAsync(
		CancellationToken cancellationToken = default) =>
		ReadAsync(set => Copy(set.Staff), cancellationToken);

	public void Add(Staff staff) => Working.Staff.Add(staff);

	private WorkingSet Working => _current.Value
		?? throw new InvalidOperationException(
			"Changes can be staged only inside ExecuteInTransactionAsync.");

	private async Task<T> ReadAsync<T>(
		Func<WorkingSet, T> read,
		CancellationToken cancellationToken)
	{
		var working = _current.Value ?? new WorkingSet(
			await _repository.LoadAsync(cancellationToken).ConfigureAwait(false));
		return read(working);
	}

	private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) =>
		Array.AsReadOnly(values.ToArray());

	private static T? Single<T>(IEnumerable<T> matches)
		where T : class
	{
		var found = matches.Take(2).ToArray();
		return found.Length > 1
			? throw new InvalidDataException(
				$"Multiple {typeof(T).Name} records match one identity.")
			: found.SingleOrDefault();
	}

	private sealed class WorkingSet(ApexPostSnapshot snapshot)
	{
		public List<Branch> Branches { get; } = [.. snapshot.Branches];
		public List<Staff> Staff { get; } = [.. snapshot.Staff];
		public List<Product> Products { get; } = [.. snapshot.Products];
		public List<BranchStock> BranchStocks { get; } = [.. snapshot.BranchStocks];
		public List<ShippingItem> ShippingItems { get; } = [.. snapshot.ShippingItems];
		public List<ShippingRate> ShippingRates { get; } = [.. snapshot.ShippingRates];
		public List<MoneyOrder> MoneyOrders { get; } = [.. snapshot.MoneyOrders];
		public List<IdentityVerification> IdentityVerifications { get; } =
			[.. snapshot.IdentityVerifications];
		public List<TransactionBasket> Baskets { get; } =
			[.. snapshot.TransactionBaskets];

		public ApexPostSnapshot ToSnapshot() => new(
			Branches,
			Staff,
			Products,
			BranchStocks,
			ShippingItems,
			ShippingRates,
			MoneyOrders,
			IdentityVerifications,
			Baskets);
	}
}
