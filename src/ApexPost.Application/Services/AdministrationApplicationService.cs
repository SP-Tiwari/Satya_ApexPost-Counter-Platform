using ApexPost.Application.Caching;
using ApexPost.Application.Validation;

namespace ApexPost.Application.Services;

/// <summary>
/// Administrative master-data use cases.  The Web layer deliberately supplies only
/// the acting staff ID; authorization is rechecked against persisted staff data.
/// </summary>
public sealed class AdministrationApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly IBranchRepository _branches;
	private readonly IProductRepository _products;
	private readonly IUnitOfWork _unitOfWork;
	private readonly ReferenceDataCache? _referenceData;

	public AdministrationApplicationService(
		IStaffRepository staff,
		IBranchRepository branches,
		IProductRepository products,
		IUnitOfWork unitOfWork,
		ReferenceDataCache? referenceData = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(branches);
		ArgumentNullException.ThrowIfNull(products);
		ArgumentNullException.ThrowIfNull(unitOfWork);
		_staff = staff;
		_branches = branches;
		_products = products;
		_unitOfWork = unitOfWork;
		_referenceData = referenceData;
	}

	// Holds only the branches, staff and products that administration manages.
	public async Task<ApexPostSnapshot> GetSnapshotAsync(
		string administratorId, CancellationToken cancellationToken = default)
	{
		await EnsureAdministratorAsync(administratorId, cancellationToken).ConfigureAwait(false);
		var branches = await _branches.GetBranchesAsync(cancellationToken).ConfigureAwait(false);
		var staff = await _staff.GetStaffAsync(cancellationToken).ConfigureAwait(false);
		var products = await _products.GetProductsAsync(cancellationToken).ConfigureAwait(false);
		return new ApexPostSnapshot(branches, staff, products, [], [], [], [], [], []);
	}

	public Task AddStaffAsync(string administratorId, string id, string name,
		string email, StaffRole role, string homeBranchCode,
		CancellationToken cancellationToken = default) =>
		_unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			await EnsureAdministratorAsync(administratorId, token).ConfigureAwait(false);
			var branchCode = new BranchCode(homeBranchCode);
			await EnsureBranchExistsAsync(branchCode, token).ConfigureAwait(false);
			if (await _staff.FindByIdAsync(id?.Trim() ?? string.Empty, token).ConfigureAwait(false) is not null)
				throw new InvalidOperationException("Staff ID already exists.");
			if (await _staff.EmailExistsAsync(email?.Trim() ?? string.Empty, token).ConfigureAwait(false))
				throw new InvalidOperationException("Staff email already exists.");
			InputLimits.Ensure(id, InputLimits.StaffId, "Staff ID");
			InputLimits.Ensure(name, InputLimits.StaffName, "Staff name");
			InputLimits.Ensure(email, InputLimits.StaffEmail, "Staff email");
			_staff.Add(new Staff(id!, name, email!, role, branchCode));
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken);

	public Task UpdateStaffAsync(string administratorId, string staffId, StaffRole role,
		string homeBranchCode, bool isActive, CancellationToken cancellationToken = default) =>
		_unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			await EnsureAdministratorAsync(administratorId, token).ConfigureAwait(false);
			var staff = await _staff.FindByIdAsync(staffId, token).ConfigureAwait(false)
				?? throw new KeyNotFoundException($"Staff '{staffId}' was not found.");
			var branchCode = new BranchCode(homeBranchCode);
			await EnsureBranchExistsAsync(branchCode, token).ConfigureAwait(false);
			staff.UpdateRole(role);
			staff.AssignToBranch(branchCode);
			if (isActive) staff.Activate(); else staff.Deactivate();
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken);

	public Task AddBranchAsync(string administratorId, string code, string name,
		string region, string openingHours, string terminalId, string terminalName,
		CancellationToken cancellationToken = default) =>
		_unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			await EnsureAdministratorAsync(administratorId, token).ConfigureAwait(false);
			var branchCode = new BranchCode(code);
			if (await _branches.FindByCodeAsync(branchCode, token).ConfigureAwait(false) is not null)
				throw new InvalidOperationException("Branch code already exists.");
			InputLimits.Ensure(name, InputLimits.BranchName, "Branch name");
			InputLimits.Ensure(region, InputLimits.BranchRegion, "Branch region");
			InputLimits.Ensure(openingHours, InputLimits.OpeningHours, "Opening hours");
			InputLimits.Ensure(terminalId, InputLimits.CounterId, "Counter terminal ID");
			InputLimits.Ensure(terminalName, InputLimits.CounterName, "Counter terminal name");
			_branches.Add(new Branch(branchCode, name, region, openingHours,
				[new CounterTerminal(terminalId, terminalName)]));
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken);

	public Task SetBranchStatusAsync(string administratorId, string code, bool isActive,
		CancellationToken cancellationToken = default) =>
		_unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			await EnsureAdministratorAsync(administratorId, token).ConfigureAwait(false);
			var branchCode = new BranchCode(code);
			var branch = await _branches.FindByCodeAsync(branchCode, token).ConfigureAwait(false)
				?? throw new KeyNotFoundException($"Branch '{branchCode}' was not found.");
			if (isActive) branch.Reopen(); else branch.Close();
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken);

	public async Task AddProductAsync(string administratorId, string sku, string name,
		ProductCategory category, decimal unitPrice, string currency, TaxCategory taxCategory,
		CancellationToken cancellationToken = default)
	{
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			await EnsureAdministratorAsync(administratorId, token).ConfigureAwait(false);
			var productSku = new Sku(sku);
			if (await _products.FindBySkuAsync(productSku, token).ConfigureAwait(false) is not null)
				throw new InvalidOperationException("Product SKU already exists.");
			InputLimits.Ensure(name, InputLimits.ProductName, "Product name");
			InputLimits.Ensure(unitPrice, InputLimits.MoneyAmount, "Unit price");
			_products.Add(new Product(productSku, name, category,
				new Money(unitPrice, currency), taxCategory));
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		// Only after a successful save, so a failed add leaves the cache valid.
		_referenceData?.Invalidate();
	}

	// A set-based change to every product in a category, for example 1.05 for
	// a five percent rise. Past sales keep the prices on their own lines.
	public async Task<int> RevisePricesAsync(string administratorId,
		ProductCategory category, decimal multiplier,
		CancellationToken cancellationToken = default)
	{
		if (multiplier <= 0m)
			throw new ArgumentOutOfRangeException(
				nameof(multiplier), "The price multiplier must be greater than zero.");

		var changed = await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			await EnsureAdministratorAsync(administratorId, token).ConfigureAwait(false);
			var products = await _products.GetProductsAsync(token).ConfigureAwait(false);
			foreach (var product in products.Where(entry => entry.Category == category))
				EnsureRevisedPriceFits(product, multiplier);

			var rows = await _products.RevisePricesAsync(category, multiplier, token)
				.ConfigureAwait(false);
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
			return rows;
		}, cancellationToken).ConfigureAwait(false);

		_referenceData?.Invalidate();
		return changed;
	}

	private static void EnsureRevisedPriceFits(Product product, decimal multiplier)
	{
		// The whole multiplication can exceed even decimal's own range.
		if (multiplier > InputLimits.MoneyAmount / Math.Max(product.UnitPrice.Amount, 1m))
			throw new ArgumentException(
				$"The revised price of '{product.Sku}' cannot exceed {InputLimits.MoneyAmount}.");
	}

	private async Task EnsureAdministratorAsync(string staffId, CancellationToken cancellationToken)
	{
		var administrator = await _staff.FindByIdAsync(staffId, cancellationToken).ConfigureAwait(false)
			?? throw new KeyNotFoundException("Administrator was not found.");
		if (!administrator.IsActive || administrator.Role != StaffRole.Administrator)
			throw new UnauthorizedAccessException("An active Administrator is required.");
	}

	private async Task EnsureBranchExistsAsync(BranchCode branchCode, CancellationToken cancellationToken)
	{
		_ = await _branches.FindByCodeAsync(branchCode, cancellationToken).ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Branch '{branchCode}' was not found.");
	}
}
