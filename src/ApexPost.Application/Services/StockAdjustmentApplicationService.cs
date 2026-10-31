using ApexPost.Application.Events;
using ApexPost.Domain.Events;

namespace ApexPost.Application.Services;

public sealed record StockAdjustmentCatalog(
	Branch Branch,
	IReadOnlyList<BranchStock> BranchStocks);

public sealed class StockAdjustmentApplicationService
{
	// The adjustment screen does not ask for a reason, but the history row
	// requires one.
	private const string ManualAdjustmentReason = "Manual stock adjustment";

	private readonly IStaffRepository _staff;
	private readonly IBranchRepository _branches;
	private readonly IProductRepository _products;
	private readonly IStockRepository _stock;
	private readonly IUnitOfWork _unitOfWork;
	private readonly DomainEventPublisher? _events;
	private readonly int? _lowStockThreshold;
	private readonly TimeProvider _timeProvider;

	public StockAdjustmentApplicationService(
		IStaffRepository staff,
		IBranchRepository branches,
		IProductRepository products,
		IStockRepository stock,
		IUnitOfWork unitOfWork,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null,
		TimeProvider? timeProvider = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(branches);
		ArgumentNullException.ThrowIfNull(products);
		ArgumentNullException.ThrowIfNull(stock);
		ArgumentNullException.ThrowIfNull(unitOfWork);
		if (lowStockThreshold < 0)
			throw new ArgumentOutOfRangeException(
				nameof(lowStockThreshold), "Low-stock threshold cannot be negative.");
		_staff = staff;
		_branches = branches;
		_products = products;
		_stock = stock;
		_unitOfWork = unitOfWork;
		_events = events;
		_lowStockThreshold = lowStockThreshold;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<StockAdjustmentCatalog> GetCatalogAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindActiveBranchManagerAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		var branch = await FindBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		var branchStocks = await _stock
			.GetForBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		return new StockAdjustmentCatalog(branch, branchStocks);
	}

	public async Task AdjustStockAsync(
		string staffId,
		string productSku,
		int quantityChange,
		CancellationToken cancellationToken = default)
	{
		// Replaced on every attempt; published only once a commit has succeeded.
		IReadOnlyList<StockLowEvent> committedEvents = [];
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			committedEvents = [];
			var staff = await FindActiveBranchManagerAsync(staffId, token)
				.ConfigureAwait(false);
			_ = await FindBranchAsync(staff.HomeBranchCode, token)
				.ConfigureAwait(false);

			if (quantityChange == 0 || quantityChange == int.MinValue)
				throw new ArgumentOutOfRangeException(
					nameof(quantityChange),
					"Stock adjustment must be a non-zero supported quantity.");

			var sku = new Sku(productSku);
			_ = await _products.FindBySkuAsync(sku, token).ConfigureAwait(false)
				?? throw new KeyNotFoundException($"Product '{sku}' was not found.");
			var stock = await _stock.FindAsync(staff.HomeBranchCode, sku, token)
				.ConfigureAwait(false)
				?? throw new KeyNotFoundException(
					$"Stock for product '{sku}' at branch '{staff.HomeBranchCode}' was not found.");

			var occurredAtUtc = _timeProvider.GetUtcNow();
			if (quantityChange > 0)
			{
				stock.AddStock(quantityChange);
			}
			else
			{
				var quantitiesBefore = LowStockEvents.CaptureQuantities(
					[stock], stock.BranchCode, [stock.ProductSku]);
				stock.RemoveStock(-quantityChange);
				committedEvents = LowStockEvents.Evaluate(
					[stock],
					stock.BranchCode,
					quantitiesBefore,
					_lowStockThreshold,
					occurredAtUtc);
			}

			_stock.AddAdjustment(new StockAdjustment(
				Guid.NewGuid(),
				stock.BranchCode,
				stock.ProductSku,
				quantityChange,
				ManualAdjustmentReason,
				staff.Id,
				occurredAtUtc));
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		_events?.Publish(committedEvents);
	}

	private async Task<Staff> FindActiveBranchManagerAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required to adjust stock.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true, Role: StaffRole.BranchManager })
		{
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required to adjust stock.");
		}

		return staff;
	}

	private async Task<Branch> FindBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken) =>
		await _branches.FindByCodeAsync(branchCode, cancellationToken)
			.ConfigureAwait(false)
		?? throw new KeyNotFoundException($"Branch '{branchCode}' was not found.");
}
