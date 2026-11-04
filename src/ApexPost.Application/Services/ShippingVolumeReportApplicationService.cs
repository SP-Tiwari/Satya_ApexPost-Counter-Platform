using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed class ShippingVolumeReportApplicationService
{
	private const string DeniedMessage =
		"An active Auditor or Administrator is required to access shipping-volume reports.";

	private readonly IStaffRepository _staff;
	private readonly IReportQueryService _queries;

	public ShippingVolumeReportApplicationService(
		IStaffRepository staff,
		IReportQueryService queries)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(queries);
		_staff = staff;
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

		return await _queries.GetShippingVolumeAsync(cancellationToken).ConfigureAwait(false);
	}
}
