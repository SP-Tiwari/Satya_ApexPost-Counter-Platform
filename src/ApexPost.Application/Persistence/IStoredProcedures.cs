namespace ApexPost.Application.Persistence;

public sealed record DailyBranchSalesResult(
	string BranchCode,
	DateOnly BusinessDate,
	string Currency,
	decimal RetailSales,
	decimal ServiceSales,
	decimal TotalSales);

public sealed record LowStockResult(
	string BranchCode,
	string Sku,
	int Quantity,
	int Threshold);

public sealed record StockAdjustmentResult(
	Guid StockAdjustmentId,
	int NewQuantity);

// The stored procedures in sql/procedures. See ADR 0023.
public interface IStoredProcedures
{
	Task<DailyBranchSalesResult?> GetDailyBranchSalesAsync(
		string branchCode,
		DateOnly date,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<LowStockResult>> GetLowStockAsync(
		string? branchCode,
		int threshold,
		CancellationToken cancellationToken = default);

	Task<StockAdjustmentResult> AdjustStockAsync(
		string branchCode,
		string sku,
		int delta,
		string reason,
		string changedByStaffId,
		CancellationToken cancellationToken = default);
}
