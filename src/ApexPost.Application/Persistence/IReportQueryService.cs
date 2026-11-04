using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Persistence;

// One line of a Paid transaction, with only what the sales totals need.
public sealed record PaidSaleLine(
	Guid TransactionId,
	string Currency,
	TransactionItemType Type,
	decimal UnitPrice,
	int Quantity,
	decimal? TaxRate,
	string? ShipmentId);

// The read side of reports and the dashboard. Implementations project only the
// columns each report needs, so nothing here is tracked or loaded whole.
public interface IReportQueryService
{
	// Most recent transaction first, and in line order within a transaction.
	Task<IReadOnlyList<PaidSaleLine>> GetPaidSaleLinesAsync(
		BranchCode branchCode,
		DateOnly businessDate,
		CancellationToken cancellationToken = default);

	// The branch's paid sales on one UTC date, with retail and service each
	// rounded to two decimals. Null when there is no paid sale that day.
	Task<DailySalesReportRow?> GetDailySalesAsync(
		BranchCode branchCode,
		DateOnly businessDate,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<LowStockReportRow>> GetLowStockAsync(
		BranchCode branchCode,
		int threshold,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<ShippingVolumeReportRow>> GetShippingVolumeAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<BranchAuditorReportRow>> GetBranchAuditorReportAsync(
		CancellationToken cancellationToken = default);
}
