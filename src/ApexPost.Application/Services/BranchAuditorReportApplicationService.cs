using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class BranchAuditorReportApplicationService
{
	private const string DeniedMessage =
		"An active Auditor or Administrator is required to access branch auditor reports.";

	private readonly IStaffRepository _staff;
	private readonly ICounterTransactionRepository _transactions;
	private readonly BranchAuditorReportService _reportService = new();

	public BranchAuditorReportApplicationService(
		IStaffRepository staff,
		ICounterTransactionRepository transactions)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(transactions);
		_staff = staff;
		_transactions = transactions;
	}

	public async Task<IReadOnlyList<BranchAuditorReportRow>> GetReportAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(DeniedMessage);

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not
			{
				IsActive: true,
				Role: StaffRole.Auditor or StaffRole.Administrator
			})
		{
			throw new UnauthorizedAccessException(DeniedMessage);
		}

		var moneyOrders = await _transactions.GetMoneyOrdersAsync(cancellationToken)
			.ConfigureAwait(false);
		var entries = moneyOrders
			.Where(order => order.ProcessingBranchCode is not null)
			.Select(order => new BranchAuditorReportEntry(
				order.ProcessingBranchCode!,
				order,
				order.IdentityVerification));

		return _reportService.CreateReport(entries);
	}
}
