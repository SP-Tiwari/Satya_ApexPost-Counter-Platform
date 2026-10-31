using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class LowStockReportApplicationService
{
	private readonly IStaffRepository _staff;
	private readonly IStockRepository _stock;
	private readonly IReportQueryService? _queries;
	private readonly int? _threshold;
	private readonly LowStockReportService _reportService = new();

	// Without it, the report is calculated from repository reads, which is what
	// the unit tests use with their in-memory fakes.
	public LowStockReportApplicationService(
		IStaffRepository staff,
		IStockRepository stock,
		int? threshold,
		IReportQueryService? queries = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(stock);
		if (threshold < 0)
			throw new ArgumentOutOfRangeException(
				nameof(threshold),
				"Low-stock threshold cannot be negative.");

		_staff = staff;
		_stock = stock;
		_threshold = threshold;
		_queries = queries;
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

		if (_queries is not null)
		{
			return await _queries
				.GetLowStockAsync(staff.HomeBranchCode, _threshold.Value, cancellationToken)
				.ConfigureAwait(false);
		}

		var branchStocks = await _stock
			.GetForBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false);
		return _reportService.CreateReport(branchStocks, _threshold.Value);
	}
}
