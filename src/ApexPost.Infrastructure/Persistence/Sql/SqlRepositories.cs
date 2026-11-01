using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// The repositories only stage changes on the shared scoped context, and never
// return IQueryable. IUnitOfWork commits them.
public sealed class SqlBranchRepository(ApexPostDbContext database)
	: IBranchRepository
{
	public Task<Branch?> FindByCodeAsync(
		BranchCode code,
		CancellationToken cancellationToken = default) =>
		database.Branches
			.Include(branch => branch.CounterTerminals)
			.SingleOrDefaultAsync(branch => branch.Code == code, cancellationToken);

	public async Task<IReadOnlyList<Branch>> GetBranchesAsync(
		CancellationToken cancellationToken = default) =>
		await database.Branches
			.AsNoTracking()
			.Include(branch => branch.CounterTerminals)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public void Add(Branch branch) => database.Branches.Add(branch);
}

public sealed class SqlProductRepository(ApexPostDbContext database)
	: IProductRepository
{
	private const string ModifiedAtUtc = "ModifiedAtUtc";

	public Task<Product?> FindBySkuAsync(
		Sku sku,
		CancellationToken cancellationToken = default) =>
		database.Products.SingleOrDefaultAsync(
			product => product.Sku == sku, cancellationToken);

	public async Task<IReadOnlyList<Product>> FindBySkusAsync(
		IReadOnlyCollection<Sku> skus,
		CancellationToken cancellationToken = default) =>
		await database.Products
			.Where(product => skus.Contains(product.Sku))
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<Product>> GetProductsAsync(
		CancellationToken cancellationToken = default) =>
		await database.Products
			.AsNoTracking()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<ShippingRate>> GetShippingRatesAsync(
		CancellationToken cancellationToken = default) =>
		await database.ShippingRates
			.AsNoTracking()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public void Add(Product product) => database.Products.Add(product);

	// A value multiplied with this column takes the column's decimal(18,2) type,
	// which would round a multiplier such as 1.125. Whole numbers keep their
	// value, so the multiplier is sent as a ratio.
	public Task<int> RevisePricesAsync(
		ProductCategory category,
		decimal multiplier,
		CancellationToken cancellationToken = default)
	{
		var scale = (multiplier / DecimalScale.OneWithFullScale).Scale;
		var denominator = 1m / new decimal(1, 0, 0, false, scale);
		var numerator = multiplier * denominator;
		var modifiedAtUtc = DateTime.UtcNow;
		return database.Products
			.Where(product => product.Category == category)
			.ExecuteUpdateAsync(
				setters => setters
					.SetProperty(
						product => product.UnitPrice.Amount,
						product => Math.Round(
							product.UnitPrice.Amount * numerator / denominator, 2))
					.SetProperty(
						product => EF.Property<DateTime?>(product, ModifiedAtUtc),
						modifiedAtUtc),
				cancellationToken);
	}
}

public sealed class SqlStockRepository(ApexPostDbContext database)
	: IStockRepository
{
	public Task<BranchStock?> FindAsync(
		BranchCode branchCode,
		Sku sku,
		CancellationToken cancellationToken = default) =>
		database.BranchStock.SingleOrDefaultAsync(
			stock => stock.BranchCode == branchCode && stock.ProductSku == sku,
			cancellationToken);

	public async Task<IReadOnlyList<BranchStock>> GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		await database.BranchStock
			.AsNoTracking()
			.Where(stock => stock.BranchCode == branchCode)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	// Ordered, so concurrent sales read and update stock rows in one order.
	public async Task<IReadOnlyList<BranchStock>> GetForProductsAsync(
		BranchCode branchCode,
		IReadOnlyCollection<Sku> skus,
		CancellationToken cancellationToken = default) =>
		await database.BranchStock
			.Where(stock =>
				stock.BranchCode == branchCode && skus.Contains(stock.ProductSku))
			.OrderBy(stock => stock.ProductSku)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public void AddAdjustment(StockAdjustment adjustment) =>
		database.StockAdjustments.Add(adjustment);
}

public sealed class SqlCounterTransactionRepository(ApexPostDbContext database)
	: ICounterTransactionRepository
{
	public Task<bool> ExistsAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		database.CounterTransactions.AnyAsync(
			basket => basket.Id == transactionId, cancellationToken);

	public async Task<TransactionBasket?> FindAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default)
	{
		var basket = await WholeBaskets()
			.SingleOrDefaultAsync(
				candidate => candidate.Id == transactionId, cancellationToken)
			.ConfigureAwait(false);
		if (basket is not null)
			await RestoreVerificationsAsync(MoneyOrdersOf([basket]), cancellationToken)
				.ConfigureAwait(false);

		return basket;
	}

	public async Task<TransactionBasket?> FindForReadAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default)
	{
		var basket = await WholeBaskets()
			.AsNoTracking()
			.SingleOrDefaultAsync(
				candidate => candidate.Id == transactionId, cancellationToken)
			.ConfigureAwait(false);
		if (basket is not null)
			await RestoreVerificationsAsync(MoneyOrdersOf([basket]), cancellationToken)
				.ConfigureAwait(false);

		return basket;
	}

	public async Task<IReadOnlyList<TransactionBasket>> GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default)
	{
		var baskets = await WholeBaskets()
			.AsNoTracking()
			.Where(basket => basket.BranchCode == branchCode)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		await RestoreVerificationsAsync(MoneyOrdersOf(baskets), cancellationToken)
			.ConfigureAwait(false);
		return baskets;
	}

	public async Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default)
	{
		var moneyOrders = await database.MoneyOrders
			.AsNoTracking()
			.Where(order => order.ProcessingBranchCode == branchCode)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		await RestoreVerificationsAsync(moneyOrders, cancellationToken)
			.ConfigureAwait(false);
		return moneyOrders;
	}

	public async Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersAsync(
		CancellationToken cancellationToken = default)
	{
		var moneyOrders = await database.MoneyOrders
			.AsNoTracking()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		await RestoreVerificationsAsync(moneyOrders, cancellationToken)
			.ConfigureAwait(false);
		return moneyOrders;
	}

	public async Task<IReadOnlyList<ShippingItem>> GetShipmentsAsync(
		CancellationToken cancellationToken = default) =>
		await database.Shipments
			.AsNoTracking()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<TransactionBasket?> FindByShipmentIdAsync(
		string shipmentId,
		CancellationToken cancellationToken = default)
	{
		var basket = await WholeBaskets()
			.AsNoTracking()
			.SingleOrDefaultAsync(
				candidate => candidate.LineItems.Any(
					line => line.ShippingItem!.Id == shipmentId),
				cancellationToken)
			.ConfigureAwait(false);
		if (basket is not null)
			await RestoreVerificationsAsync(MoneyOrdersOf([basket]), cancellationToken)
				.ConfigureAwait(false);

		return basket;
	}

	public void Add(TransactionBasket basket)
	{
		ArgumentNullException.ThrowIfNull(basket);

		database.CounterTransactions.Add(basket);
		foreach (var moneyOrder in MoneyOrdersOf([basket]))
		{
			if (moneyOrder.IdentityVerification is { } verification)
			{
				database.IdentityVerifications.Add(
					IdentityVerificationMapper.ToRecord(moneyOrder.Id, verification));
			}
		}
	}

	public void AddPayment(Payment payment) => database.Payments.Add(payment);

	public Task<Payment?> FindPaymentAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		database.Payments
			.AsNoTracking()
			.SingleOrDefaultAsync(
				payment => payment.TransactionId == transactionId &&
					payment.Type == PaymentEntryType.Payment,
				cancellationToken);

	// A split query, because lines would otherwise repeat for each joined row.
	private IQueryable<TransactionBasket> WholeBaskets() =>
		database.CounterTransactions
			.Include(basket => basket.LineItems)
				.ThenInclude(line => line.ShippingItem)
			.Include(basket => basket.LineItems)
				.ThenInclude(line => line.MoneyOrder)
			.AsSplitQuery();

	private static MoneyOrder[] MoneyOrdersOf(
		IEnumerable<TransactionBasket> baskets) =>
		baskets
			.SelectMany(basket => basket.LineItems)
			.Select(line => line.MoneyOrder)
			.OfType<MoneyOrder>()
			.ToArray();

	// One query for all the money orders, so loading a list is not N+1.
	private async Task RestoreVerificationsAsync(
		IReadOnlyCollection<MoneyOrder> moneyOrders,
		CancellationToken cancellationToken)
	{
		if (moneyOrders.Count == 0)
			return;

		var moneyOrderIds = moneyOrders.Select(order => order.Id).ToArray();
		var records = await database.IdentityVerifications
			.AsNoTracking()
			.Where(record => moneyOrderIds.Contains(record.MoneyOrderId))
			.ToDictionaryAsync(record => record.MoneyOrderId, cancellationToken)
			.ConfigureAwait(false);
		foreach (var moneyOrder in moneyOrders)
		{
			if (records.TryGetValue(moneyOrder.Id, out var record))
			{
				moneyOrder.RestoreIdentityVerification(
					IdentityVerificationMapper.ToDomain(record));
			}
		}
	}
}

public sealed class SqlStaffRepository(ApexPostDbContext database)
	: IStaffRepository
{
	// The column collation ignores case and the domain does not, so the ID is
	// compared again after the query.
	public async Task<Staff?> FindByIdAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await database.Staff
			.SingleOrDefaultAsync(person => person.Id == staffId, cancellationToken)
			.ConfigureAwait(false);
		return string.Equals(staff?.Id, staffId, StringComparison.Ordinal)
			? staff
			: null;
	}

	public Task<bool> EmailExistsAsync(
		string email,
		CancellationToken cancellationToken = default) =>
		database.Staff.AnyAsync(person => person.Email == email, cancellationToken);

	public async Task<IReadOnlyList<Staff>> GetStaffAsync(
		CancellationToken cancellationToken = default) =>
		await database.Staff
			.AsNoTracking()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public void Add(Staff staff) => database.Staff.Add(staff);
}
