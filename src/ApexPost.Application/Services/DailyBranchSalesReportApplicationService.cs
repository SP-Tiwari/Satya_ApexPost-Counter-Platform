using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class DailyBranchSalesReportApplicationService
{
	private const string DeniedMessage =
		"An active Branch Manager is required to access daily sales reports.";

	private readonly IStaffRepository _staff;
	private readonly IReportQueryService _queries;

	public DailyBranchSalesReportApplicationService(
		IStaffRepository staff,
		IReportQueryService queries)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(queries);
		_staff = staff;
		_queries = queries;
	}

	public async Task<IReadOnlyList<DailySalesReportRow>> GetReportAsync(
		string staffId,
		DateOnly businessDate,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(DeniedMessage);

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true, Role: StaffRole.BranchManager })
			throw new UnauthorizedAccessException(DeniedMessage);

		var totals = await _queries
			.GetDailySalesAsync(staff.HomeBranchCode, businessDate, cancellationToken)
			.ConfigureAwait(false);
		return totals is null ? [] : [totals];
	}
}
