using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class ShippingVolumeReportApplicationService
{
	private const string DeniedMessage =
		"An active Auditor or Administrator is required to access shipping-volume reports.";

	private readonly IStaffRepository _staff;
	private readonly ICounterTransactionRepository _transactions;
	private readonly IReportQueryService? _queries;
	private readonly ShippingVolumeReportService _reportService = new();

	// Without it, the report is calculated from repository reads, which is what
	// the unit tests use with their in-memory fakes.
	public ShippingVolumeReportApplicationService(
		IStaffRepository staff,
		ICounterTransactionRepository transactions,
		IReportQueryService? queries = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(transactions);
		_staff = staff;
		_transactions = transactions;
		_queries = queries;
	}

	public async Task<IReadOnlyList<ShippingVolumeReportRow>> GetReportAsync(
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

		if (_queries is not null)
			return await _queries.GetShippingVolumeAsync(cancellationToken).ConfigureAwait(false);

		var shipments = await _transactions.GetShipmentsAsync(cancellationToken)
			.ConfigureAwait(false);
		return _reportService.CreateReport(shipments);
	}
}
