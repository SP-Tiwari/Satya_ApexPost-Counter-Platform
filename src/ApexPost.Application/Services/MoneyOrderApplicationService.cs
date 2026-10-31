
namespace ApexPost.Application.Services;

// Reads the branch's money-order records for the list page. Money orders are
// issued only as basket lines (MixedBasketApplicationService).
public sealed class MoneyOrderApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly ICounterTransactionRepository _transactions;
	private readonly Money? _highValueThreshold;

	public MoneyOrderApplicationService(
		IStaffRepository staff,
		ICounterTransactionRepository transactions,
		Money? highValueThreshold)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(transactions);
		_staff = staff;
		_transactions = transactions;
		_highValueThreshold = highValueThreshold;
	}

	public bool IsConfigured => _highValueThreshold is not null;

	public async Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindActiveBranchManagerAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		return await _transactions
			.GetMoneyOrdersForBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<Staff> FindActiveBranchManagerAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required to access money orders.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true, Role: StaffRole.BranchManager })
		{
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required to access money orders.");
		}

		return staff;
	}
}
