using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class LowStockReportApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly IReportQueryService _queries;
	private readonly int? _threshold;

	public LowStockReportApplicationService(
		IStaffRepository staff,
		IReportQueryService queries,
		int? threshold)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(queries);
		if (threshold < 0)
			throw new ArgumentOutOfRangeException(
				nameof(threshold),
				"Low-stock threshold cannot be negative.");

		_staff = staff;
		_queries = queries;
		_threshold = threshold;
	}

	public async Task<IReadOnlyList<LowStockReportRow>> GetReportAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required to access low-stock reports.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true, Role: StaffRole.BranchManager })
		{
			throw new UnauthorizedAccessException(
				"An active Branch Manager is required to access low-stock reports.");
		}

		if (_threshold is null)
			throw new InvalidOperationException(
				"Reports:LowStockThreshold is not configured.");

		return await _queries
			.GetLowStockAsync(staff.HomeBranchCode, _threshold.Value, cancellationToken)
			.ConfigureAwait(false);
	}
}
