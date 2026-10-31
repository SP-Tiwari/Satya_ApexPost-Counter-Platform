using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class ShippingVolumeReportApplicationService
{
	private const string DeniedMessage =
		"An active Auditor or Administrator is required to access shipping-volume reports.";

	private readonly IStaffRepository _staff;
	private readonly ICounterTransactionRepository _transactions;
	private readonly ShippingVolumeReportService _reportService = new();

	public ShippingVolumeReportApplicationService(
		IStaffRepository staff,
		ICounterTransactionRepository transactions)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(transactions);
		_staff = staff;
		_transactions = transactions;
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

		var shipments = await _transactions.GetShipmentsAsync(cancellationToken)
			.ConfigureAwait(false);
		return _reportService.CreateReport(shipments);
	}
}
