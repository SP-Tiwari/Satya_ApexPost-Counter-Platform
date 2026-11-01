namespace ApexPost.Application.Persistence;

public interface IStockRepository
{
	Task<BranchStock?> FindAsync(
		BranchCode branchCode,
		Sku sku,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<BranchStock>> GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<BranchStock>> GetForProductsAsync(
		BranchCode branchCode,
		IReadOnlyCollection<Sku> skus,
		CancellationToken cancellationToken = default);

	void AddAdjustment(StockAdjustment adjustment);
}
