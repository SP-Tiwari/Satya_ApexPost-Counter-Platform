using ApexPost.Application.Caching;

namespace ApexPost.Application.Services;

// QuantityOnHand is null when the branch holds no stock record for the
// product, which is different from a record of zero.
public sealed record ProductCatalogueRow(
	Product Product,
	int? QuantityOnHand);

public sealed record ProductCatalogue(
	Branch Branch,
	IReadOnlyList<ProductCatalogueRow> Rows,
	DateTimeOffset CatalogueLoadedAtUtc);

// The product catalogue with stock on hand at the signed-in staff member's
// home branch. Products come from the reference-data cache; stock levels
// change with every sale, so they are always read from the repository.
public sealed class ProductCatalogueApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly IBranchRepository _branches;
	private readonly IStockRepository _stock;
	private readonly ReferenceDataCache _referenceData;

	public ProductCatalogueApplicationService(
		IStaffRepository staff,
		IBranchRepository branches,
		IStockRepository stock,
		ReferenceDataCache referenceData)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(branches);
		ArgumentNullException.ThrowIfNull(stock);
		ArgumentNullException.ThrowIfNull(referenceData);
		_staff = staff;
		_branches = branches;
		_stock = stock;
		_referenceData = referenceData;
	}

	// JSON persistence, until the SQL Server cutover in M3.
	public ProductCatalogueApplicationService(
		IApexPostRepository repository,
		ReferenceDataCache referenceData)
		: this(new SnapshotStore(repository), referenceData)
	{
	}

	private ProductCatalogueApplicationService(
		SnapshotStore store,
		ReferenceDataCache referenceData)
		: this(store, store, store, referenceData)
	{
	}

	public async Task<ProductCatalogue> GetCatalogueAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		// One after another: the repositories share a scoped DbContext, which
		// cannot run two operations at once.
		var staff = await FindActiveStaffAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		var branch = await _branches
			.FindByCodeAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException(
				$"Branch '{staff.HomeBranchCode}' was not found.");
		var branchStocks = await _stock
			.GetForBranchAsync(branch.Code, cancellationToken)
			.ConfigureAwait(false);
		if (branchStocks.Select(stock => stock.ProductSku).Distinct().Count() !=
			branchStocks.Count)
		{
			throw new InvalidOperationException(
				$"Multiple stock records match one product at branch '{branch.Code}'.");
		}

		var referenceData = await _referenceData.GetAsync(cancellationToken)
			.ConfigureAwait(false);
		var quantities = branchStocks.ToDictionary(
			stock => stock.ProductSku, stock => stock.Quantity);
		var rows = referenceData.Products
			.OrderBy(product => product.Category)
			.ThenBy(product => product.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(product => new ProductCatalogueRow(
				product,
				quantities.TryGetValue(product.Sku, out var quantity)
					? quantity
					: null))
			.ToArray();

		return new ProductCatalogue(
			branch, Array.AsReadOnly(rows), referenceData.LoadedAtUtc);
	}

	private async Task<Staff> FindActiveStaffAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"Active staff are required to view the catalogue.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		return staff is { IsActive: true }
			? staff
			: throw new UnauthorizedAccessException(
				"Active staff are required to view the catalogue.");
	}
}
