using ApexPost.Application.Caching;

namespace ApexPost.Application.Services;

public sealed record CatalogueScreenRow(
	string Sku,
	string Name,
	ProductCategory Category,
	decimal UnitPrice,
	string Currency,
	TaxCategory TaxCategory,
	int? QuantityOnHand);

public sealed record CatalogueScreen(
	BranchHeading Branch,
	IReadOnlyList<CatalogueScreenRow> Rows,
	DateTimeOffset CatalogueLoadedAtUtc);

public sealed record StockScreen(
	BranchHeading Branch,
	IReadOnlyList<StockRow> Stock);

public sealed record AdministrationScreen(
	IReadOnlyList<AdministrationBranchRow> Branches,
	IReadOnlyList<AdministrationStaffRow> Staff,
	IReadOnlyList<AdministrationProductRow> Products);

// Read screens as DTOs. Each method applies the same access checks as the
// service that owns the screen's commands, then reads through projections.
public sealed class ScreenQueryApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly IScreenQueryService _queries;
	private readonly ReferenceDataCache _referenceData;
	private readonly TransactionAccessRule _accessRule = new();

	public ScreenQueryApplicationService(
		IStaffRepository staff,
		IScreenQueryService queries,
		ReferenceDataCache referenceData)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(queries);
		ArgumentNullException.ThrowIfNull(referenceData);
		_staff = staff;
		_queries = queries;
		_referenceData = referenceData;
	}

	public async Task<CatalogueScreen> GetCatalogueAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		const string denied = "Active staff are required to view the catalogue.";
		var staff = await FindStaffAsync(staffId, denied, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true })
			throw new UnauthorizedAccessException(denied);

		// One after another: the queries share a scoped DbContext.
		var branch = await FindBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		var stock = await _queries
			.GetStockAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		var referenceData = await _referenceData.GetAsync(cancellationToken)
			.ConfigureAwait(false);

		var quantities = stock.ToDictionary(row => row.Sku, row => row.Quantity);
		var rows = referenceData.Products
			.OrderBy(product => product.Category)
			.ThenBy(product => product.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(product => new CatalogueScreenRow(
				product.Sku.Value,
				product.Name,
				product.Category,
				product.UnitPrice.Amount,
				product.UnitPrice.Currency,
				product.TaxCategory,
				quantities.TryGetValue(product.Sku.Value, out var quantity)
					? quantity
					: null))
			.ToArray();

		return new CatalogueScreen(
			branch, Array.AsReadOnly(rows), referenceData.LoadedAtUtc);
	}

	public async Task<StockScreen> GetStockAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindBranchManagerAsync(
			staffId,
			"An active Branch Manager is required to adjust stock.",
			cancellationToken).ConfigureAwait(false);
		var branch = await FindBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		var stock = await _queries
			.GetStockAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		return new StockScreen(branch, stock);
	}

	public async Task<IReadOnlyList<MoneyOrderRow>> GetMoneyOrdersAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindBranchManagerAsync(
			staffId,
			"An active Branch Manager is required to access money orders.",
			cancellationToken).ConfigureAwait(false);
		return await _queries
			.GetMoneyOrdersAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<TransactionDetail>> GetTransactionsAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindBranchManagerAsync(
			staffId,
			"An active Branch Manager is required.",
			cancellationToken).ConfigureAwait(false);
		return await _queries
			.GetTransactionsAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task<TransactionDetail> GetTransactionAsync(
		Guid transactionId,
		string staffId,
		CancellationToken cancellationToken = default)
	{
		if (transactionId == Guid.Empty)
			throw new ArgumentException(
				"Transaction ID cannot be empty.", nameof(transactionId));

		const string denied = "An active Branch Manager or Counter Clerk is required.";
		var staff = await FindStaffAsync(staffId, denied, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not
			{
				IsActive: true,
				Role: StaffRole.BranchManager or StaffRole.CounterClerk
			})
		{
			throw new UnauthorizedAccessException(denied);
		}

		var transaction = await _queries
			.FindTransactionAsync(transactionId, cancellationToken)
			.ConfigureAwait(false);
		var branchCode = transaction?.BranchCode is { } code ? new BranchCode(code) : null;
		return transaction is not null &&
			_accessRule.CanView(staff, branchCode, transaction.ClerkStaffId)
			? transaction
			: throw new KeyNotFoundException(
				$"Transaction '{transactionId}' was not found.");
	}

	public async Task<AdministrationScreen> GetAdministrationAsync(
		string administratorId,
		CancellationToken cancellationToken = default)
	{
		var administrator = await _staff
			.FindByIdAsync(administratorId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException("Administrator was not found.");
		if (!administrator.IsActive || administrator.Role != StaffRole.Administrator)
			throw new UnauthorizedAccessException("An active Administrator is required.");

		var branches = await _queries.GetBranchesAsync(cancellationToken)
			.ConfigureAwait(false);
		var staff = await _queries.GetStaffAsync(cancellationToken)
			.ConfigureAwait(false);
		var products = await _queries.GetProductsAsync(cancellationToken)
			.ConfigureAwait(false);
		return new AdministrationScreen(branches, staff, products);
	}

	private async Task<Staff?> FindStaffAsync(
		string staffId,
		string deniedMessage,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(deniedMessage);

		return await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<Staff> FindBranchManagerAsync(
		string staffId,
		string deniedMessage,
		CancellationToken cancellationToken)
	{
		var staff = await FindStaffAsync(staffId, deniedMessage, cancellationToken)
			.ConfigureAwait(false);
		return staff is { IsActive: true, Role: StaffRole.BranchManager }
			? staff
			: throw new UnauthorizedAccessException(deniedMessage);
	}

	private async Task<BranchHeading> FindBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken) =>
		await _queries.FindBranchAsync(branchCode, cancellationToken)
			.ConfigureAwait(false)
		?? throw new KeyNotFoundException($"Branch '{branchCode}' was not found.");
}
