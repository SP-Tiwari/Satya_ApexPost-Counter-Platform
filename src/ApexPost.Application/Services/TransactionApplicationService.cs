using ApexPost.Application.Events;
using ApexPost.Application.Validation;
using ApexPost.Domain.Events;

namespace ApexPost.Application.Services;

public sealed class TransactionApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly IBranchRepository _branches;
	private readonly IStockRepository _stock;
	private readonly ICounterTransactionRepository _transactions;
	private readonly IUnitOfWork _unitOfWork;
	private readonly TransactionProcessingRule _processingRule = new();
	private readonly TransactionAccessRule _accessRule = new();
	private readonly TransactionVoidRefundRule _voidRefundRule = new();
	private readonly TimeProvider _timeProvider;
	private readonly DomainEventPublisher? _events;
	private readonly int? _lowStockThreshold;

	public TransactionApplicationService(
		IStaffRepository staff,
		IBranchRepository branches,
		IStockRepository stock,
		ICounterTransactionRepository transactions,
		IUnitOfWork unitOfWork,
		TimeProvider? timeProvider = null,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(branches);
		ArgumentNullException.ThrowIfNull(stock);
		ArgumentNullException.ThrowIfNull(transactions);
		ArgumentNullException.ThrowIfNull(unitOfWork);
		if (lowStockThreshold < 0)
			throw new ArgumentOutOfRangeException(
				nameof(lowStockThreshold), "Low-stock threshold cannot be negative.");
		_staff = staff;
		_branches = branches;
		_stock = stock;
		_transactions = transactions;
		_unitOfWork = unitOfWork;
		_timeProvider = timeProvider ?? TimeProvider.System;
		_events = events;
		_lowStockThreshold = lowStockThreshold;
	}

	// JSON persistence, until the SQL Server cutover in M3.
	public TransactionApplicationService(
		IApexPostRepository repository,
		TimeProvider? timeProvider = null,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null)
		: this(new SnapshotStore(repository), timeProvider, events, lowStockThreshold)
	{
	}

	private TransactionApplicationService(
		SnapshotStore store,
		TimeProvider? timeProvider,
		DomainEventPublisher? events,
		int? lowStockThreshold)
		: this(store, store, store, store, store, timeProvider, events, lowStockThreshold)
	{
	}

	public async Task<IReadOnlyList<TransactionBasketSnapshot>>
		GetTransactionBasketsAsync(
			string staffId,
			CancellationToken cancellationToken = default)
	{
		var staff = await FindActiveBranchManagerAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		var baskets = await _transactions
			.GetForBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		return Array.AsReadOnly(baskets
			.Where(basket => _accessRule.CanAccess(staff, basket))
			.Select(basket => basket.GetSnapshot())
			.ToArray());
	}

	public async Task<TransactionBasketSnapshot> GetTransactionBasketAsync(
		Guid transactionId,
		string staffId,
		CancellationToken cancellationToken = default)
	{
		if (transactionId == Guid.Empty)
			throw new ArgumentException(
				"Transaction ID cannot be empty.", nameof(transactionId));

		var staff = await FindActiveTransactionViewerAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		var basket = await FindBasketAsync(transactionId, cancellationToken)
			.ConfigureAwait(false);
		if (!_accessRule.CanView(staff, basket))
			throw new KeyNotFoundException(
				$"Transaction '{transactionId}' was not found.");
		return basket.GetSnapshot();
	}

	public async Task<Guid> CreateBasketAsync(
		string currency,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		CancellationToken cancellationToken = default)
	{
		var basket = new TransactionBasket(
			currency,
			taxRates,
			occurredAtUtc: _timeProvider.GetUtcNow());
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			if (await _transactions.ExistsAsync(basket.Id, token).ConfigureAwait(false))
			{
				throw new InvalidOperationException(
					$"Transaction '{basket.Id}' already exists.");
			}

			_transactions.Add(basket);
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		return basket.Id;
	}

	public Task AddLineItemAsync(
		Guid transactionId,
		TransactionLineItem lineItem,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(lineItem);
		return _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			var basket = await FindBasketAsync(transactionId, token)
				.ConfigureAwait(false);
			basket.AddLineItem(lineItem);
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken);
	}

	public async Task ProcessSaleAsync(
		Guid transactionId,
		string staffId,
		BranchCode branchCode,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(branchCode);
		// Replaced on every attempt; published only once a commit has succeeded.
		IReadOnlyList<DomainEvent> committedEvents = [];
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			var basket = await FindBasketAsync(transactionId, token)
				.ConfigureAwait(false);
			var staff = await FindStaffAsync(staffId, token).ConfigureAwait(false);
			var branch = await _branches.FindByCodeAsync(branchCode, token)
				.ConfigureAwait(false)
				?? throw new KeyNotFoundException(
					$"Branch '{branchCode}' was not found.");
			var branchStocks = await GetStockForBasketAsync(basket, branch.Code, token)
				.ConfigureAwait(false);
			var quantitiesBefore = LowStockEvents.CaptureQuantities(
				branchStocks,
				branch.Code,
				basket.LineItems
					.Select(lineItem => lineItem.ProductSku)
					.OfType<Sku>());
			_processingRule.Process(staff, branch, basket, branchStocks);

			var occurredAtUtc = _timeProvider.GetUtcNow();
			RecordPaymentEntry(basket, PaymentEntryType.Payment, occurredAtUtc);
			committedEvents =
			[
				new TransactionCompletedEvent(
					basket.Id, branch.Code, staffId, basket.Total, occurredAtUtc),
				.. LowStockEvents.Evaluate(
					branchStocks,
					branch.Code,
					quantitiesBefore,
					_lowStockThreshold,
					occurredAtUtc)
			];
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		_events?.Publish(committedEvents);
	}

	public async Task VoidAsync(
		Guid transactionId,
		string staffId,
		string reason,
		CancellationToken cancellationToken = default)
	{
		InputLimits.Ensure(reason, InputLimits.ReversalReason, "Reason");

		TransactionVoidedEvent? committedEvent = null;
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			var (basket, staff, branchStocks) = await LoadForReversalAsync(
				transactionId, staffId, token).ConfigureAwait(false);
			var wasPaid = basket.PaymentStatus == TransactionPaymentStatus.Paid;
			_voidRefundRule.Void(staff, basket, reason, branchStocks);

			var occurredAtUtc = _timeProvider.GetUtcNow();
			// A paid sale took money, so voiding it reverses that money. An
			// open basket took none.
			if (wasPaid)
				RecordPaymentEntry(basket, PaymentEntryType.Refund, occurredAtUtc);

			committedEvent = new TransactionVoidedEvent(
				basket.Id,
				basket.BranchCode,
				staffId,
				reason.Trim(),
				occurredAtUtc);
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		_events?.Publish([committedEvent!]);
	}

	public async Task RefundAsync(
		Guid transactionId,
		string staffId,
		string reason,
		CancellationToken cancellationToken = default)
	{
		InputLimits.Ensure(reason, InputLimits.ReversalReason, "Reason");

		TransactionRefundedEvent? committedEvent = null;
		await _unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			var (basket, staff, branchStocks) = await LoadForReversalAsync(
				transactionId, staffId, token).ConfigureAwait(false);
			_voidRefundRule.Refund(staff, basket, reason, branchStocks);

			var occurredAtUtc = _timeProvider.GetUtcNow();
			RecordPaymentEntry(basket, PaymentEntryType.Refund, occurredAtUtc);
			committedEvent = new TransactionRefundedEvent(
				basket.Id,
				basket.BranchCode,
				staffId,
				reason.Trim(),
				occurredAtUtc);
			await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);

		_events?.Publish([committedEvent!]);
	}

	private async Task<(
		TransactionBasket Basket,
		Staff Staff,
		IReadOnlyList<BranchStock> BranchStocks)> LoadForReversalAsync(
		Guid transactionId,
		string staffId,
		CancellationToken cancellationToken)
	{
		var basket = await FindBasketAsync(transactionId, cancellationToken)
			.ConfigureAwait(false);
		var staff = await FindActiveBranchManagerAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (!_accessRule.CanAccess(staff, basket))
			throw new KeyNotFoundException(
				$"Transaction '{transactionId}' was not found.");

		var branchStocks = await GetStockForBasketAsync(
			basket, staff.HomeBranchCode, cancellationToken).ConfigureAwait(false);
		return (basket, staff, branchStocks);
	}

	// The workflow has one method per sale and no way to choose another for a
	// reversal, so the entry records the sale's method. A basket without sale
	// details has no method, and a zero total moved no money.
	private void RecordPaymentEntry(
		TransactionBasket basket,
		PaymentEntryType type,
		DateTimeOffset occurredAtUtc)
	{
		var total = basket.Total;
		if (basket.SaleDetails is { } saleDetails && total.Amount > 0m)
		{
			_transactions.AddPayment(new Payment(
				Guid.NewGuid(),
				basket.Id,
				type,
				total,
				saleDetails.PaymentMethod,
				occurredAtUtc));
		}
	}

	private Task<IReadOnlyList<BranchStock>> GetStockForBasketAsync(
		TransactionBasket basket,
		BranchCode branchCode,
		CancellationToken cancellationToken) =>
		_stock.GetForProductsAsync(
			branchCode,
			basket.LineItems
				.Select(lineItem => lineItem.ProductSku)
				.OfType<Sku>()
				.Distinct()
				.ToArray(),
			cancellationToken);

	private async Task<TransactionBasket> FindBasketAsync(
		Guid transactionId,
		CancellationToken cancellationToken)
	{
		if (transactionId == Guid.Empty)
			throw new ArgumentException(
				"Transaction ID cannot be empty.", nameof(transactionId));

		return await _transactions.FindAsync(transactionId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException(
				$"Transaction '{transactionId}' was not found.");
	}

	private async Task<Staff> FindStaffAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new ArgumentException("Staff ID is required.", nameof(staffId));

		return await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Staff '{staffId}' was not found.");
	}

	private async Task<Staff> FindActiveBranchManagerAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true, Role: StaffRole.BranchManager })
		{
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required.");
		}

		return staff;
	}

	// Branch Managers and Counter Clerks may read a transaction; which ones
	// each can see is decided by TransactionAccessRule.CanView.
	private async Task<Staff> FindActiveTransactionViewerAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"An active Branch Manager or Counter Clerk is required.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not
			{
				IsActive: true,
				Role: StaffRole.BranchManager or StaffRole.CounterClerk
			})
		{
			throw new UnauthorizedAccessException(
				"An active Branch Manager or Counter Clerk is required.");
		}

		return staff;
	}
}
