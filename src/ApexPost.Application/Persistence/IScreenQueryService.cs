namespace ApexPost.Application.Persistence;

public sealed record BranchHeading(string Code, string Name);

public sealed record StockRow(string Sku, int Quantity);

public sealed record MoneyOrderRow(
	decimal Amount,
	string Currency,
	string Sender,
	string Recipient,
	decimal Fee,
	string? DocumentType,
	string? MaskedDocumentIdentifier,
	bool? IsSuccessful,
	MoneyOrderStatus Status);

public sealed record ShipmentDetail(
	string ShipmentId,
	ShippingItemType Type,
	ShippingServiceLevel ServiceLevel,
	decimal WeightKilograms,
	decimal LengthCm,
	decimal WidthCm,
	decimal HeightCm,
	string DestinationZone);

public sealed record TransactionLineDetail(
	string Description,
	TransactionItemType Type,
	decimal UnitPrice,
	string Currency,
	int Quantity,
	decimal? TaxRate,
	ShipmentDetail? Shipment)
{
	public decimal Subtotal => UnitPrice * Quantity;

	// The same rule as TransactionBasket: money-order principal is untaxed.
	public decimal Tax => Type == TransactionItemType.MoneyOrder
		? 0m
		: Subtotal * (TaxRate ?? throw new InvalidOperationException(
			$"No tax rate is stored for line '{Description}'."));
}

public sealed record TransactionDetail(
	Guid Id,
	string Currency,
	string? BranchCode,
	TransactionPaymentStatus PaymentStatus,
	string? ReversalReason,
	DateTimeOffset? OccurredAtUtc,
	string? CounterTerminalId,
	string? ClerkStaffId,
	string? PaymentMethod,
	IReadOnlyList<TransactionLineDetail> Lines)
{
	public decimal Subtotal => Lines.Sum(line => line.Subtotal);
	public decimal Tax => Lines.Sum(line => line.Tax);
	public decimal Total => Subtotal + Tax;
}

public sealed record ParcelDetail(
	string ShipmentId,
	ShippingItemType Type,
	ShippingServiceLevel ServiceLevel,
	string DestinationZone,
	DateTimeOffset? AcceptedAtUtc,
	TransactionPaymentStatus TransactionStatus);

public sealed record AdministrationBranchRow(
	string Code,
	string Name,
	string Region,
	bool IsActive);

public sealed record AdministrationStaffRow(
	string Id,
	string Name,
	StaffRole Role,
	string HomeBranchCode,
	bool IsActive);

public sealed record AdministrationProductRow(
	string Sku,
	string Name,
	ProductCategory Category,
	decimal UnitPrice,
	string Currency);

public sealed record DashboardBranch(
	string Code,
	string Name,
	string Region,
	string OpeningHours,
	bool IsActive,
	int CounterCount,
	int ActiveCounterCount);

public sealed record SaleFormCounter(string Id, string Name);

public sealed record SaleFormBranch(
	string Code,
	string Name,
	bool IsActive,
	IReadOnlyList<SaleFormCounter> ActiveCounters);

public sealed record SaleFormProduct(
	string Sku,
	string Name,
	TaxCategory TaxCategory,
	decimal UnitPrice,
	string Currency);

public sealed record SaleFormShippingRate(
	string DestinationZone,
	ShippingServiceLevel ServiceLevel,
	decimal BasePrice,
	string BasePriceCurrency,
	decimal PerKilogramCharge,
	string PerKilogramChargeCurrency);

// The read side of the working screens. Implementations project only the
// columns each screen shows, so nothing here is tracked or loaded whole.
public interface IScreenQueryService
{
	Task<BranchHeading?> FindBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<DashboardBranch?> FindDashboardBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<SaleFormBranch?> FindSaleBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<SaleFormProduct>> GetSaleProductsAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<SaleFormShippingRate>> GetSaleShippingRatesAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<StockRow>> GetStockAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<MoneyOrderRow>> GetMoneyOrdersAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<TransactionDetail?> FindTransactionAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<TransactionDetail>> GetTransactionsAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<ParcelDetail?> FindParcelAsync(
		string shipmentId,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<AdministrationBranchRow>> GetBranchesAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<AdministrationStaffRow>> GetStaffAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<AdministrationProductRow>> GetProductsAsync(
		CancellationToken cancellationToken = default);
}
