using ApexPost.Application.Caching;
using ApexPost.Application.Courier;
using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

public sealed record DashboardSalesSummary(
	DateOnly BusinessDate,
	int TransactionCount,
	DailySalesReportRow? Totals);

// Sales, low-stock and shipment panels follow the existing report rules and
// are filled only for Branch Managers. LowStock is null when no threshold is
// configured; RecentShipments is null when no courier service is configured.
public sealed record BranchDashboard(
	string StaffName,
	StaffRole Role,
	DashboardBranch Branch,
	int ProductCount,
	int ShippingZoneCount,
	DashboardSalesSummary? TodaySales,
	IReadOnlyList<LowStockReportRow>? LowStock,
	IReadOnlyList<CourierTrackingResult>? RecentShipments,
	DateTimeOffset GeneratedAtUtc);

public sealed class BranchDashboardApplicationService
{
	// How many of today's shipments the dashboard looks up; a display limit.
	private const int RecentShipmentLimit = 5;

	private readonly IStaffRepository _staff;
	private readonly IReportQueryService _reports;
	private readonly IScreenQueryService _screens;
	private readonly ReferenceDataCache _referenceData;
	private readonly TimeProvider _timeProvider;
	private readonly int? _lowStockThreshold;
	private readonly ICourierTrackingService? _courier;

	public BranchDashboardApplicationService(
		IStaffRepository staff,
		IReportQueryService reports,
		IScreenQueryService screens,
		ReferenceDataCache referenceData,
		TimeProvider timeProvider,
		int? lowStockThreshold = null,
		ICourierTrackingService? courier = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(reports);
		ArgumentNullException.ThrowIfNull(screens);
		ArgumentNullException.ThrowIfNull(referenceData);
		ArgumentNullException.ThrowIfNull(timeProvider);
		if (lowStockThreshold < 0)
			throw new ArgumentOutOfRangeException(
				nameof(lowStockThreshold), "Low-stock threshold cannot be negative.");

		_staff = staff;
		_reports = reports;
		_screens = screens;
		_referenceData = referenceData;
		_timeProvider = timeProvider;
		_lowStockThreshold = lowStockThreshold;
		_courier = courier;
	}

	public async Task<BranchDashboard> GetDashboardAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var staff = await FindActiveStaffAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		var branch = await _screens
			.FindDashboardBranchAsync(staff.HomeBranchCode, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException(
				$"Branch '{staff.HomeBranchCode}' was not found.");

		var generatedAtUtc = _timeProvider.GetUtcNow();
		var today = DateOnly.FromDateTime(generatedAtUtc.UtcDateTime);
		var isManager = staff.Role == StaffRole.BranchManager;
		var panels = isManager
			? await LoadPanelsAsync(staff.HomeBranchCode, today, cancellationToken)
				.ConfigureAwait(false)
			: ManagerPanels.None;

		// The catalogue (from the cache, in its own scope) and the courier
		// lookups are independent I/O, so they run in parallel.
		var referenceTask = _referenceData.GetAsync(cancellationToken).AsTask();
		var shipmentsTask = isManager && _courier is not null
			? LookUpRecentShipmentsAsync(panels.ShipmentIds, _courier, cancellationToken)
			: Task.FromResult<IReadOnlyList<CourierTrackingResult>?>(null);
		await Task.WhenAll(referenceTask, shipmentsTask).ConfigureAwait(false);
		var referenceData = await referenceTask.ConfigureAwait(false);

		return new BranchDashboard(
			staff.Name,
			staff.Role,
			branch,
			referenceData.Products.Count,
			referenceData.ShippingRates
				.Select(rate => rate.DestinationZone)
				.Distinct(StringComparer.Ordinal)
				.Count(),
			panels.TodaySales,
			panels.LowStock,
			await shipmentsTask.ConfigureAwait(false),
			generatedAtUtc);
	}

	private async Task<ManagerPanels> LoadPanelsAsync(
		BranchCode branchCode,
		DateOnly today,
		CancellationToken cancellationToken)
	{
		// One after another: these share the request's DbContext.
		var lines = await _reports
			.GetPaidSaleLinesAsync(branchCode, today, cancellationToken)
			.ConfigureAwait(false);
		var lowStock = _lowStockThreshold is { } threshold
			? await _reports.GetLowStockAsync(branchCode, threshold, cancellationToken)
				.ConfigureAwait(false)
			: null;

		var summary = PaidSalesSummary.From(branchCode, today, lines);
		return new ManagerPanels(
			new DashboardSalesSummary(today, summary.TransactionCount, summary.Totals),
			lowStock,
			summary.ShipmentIds);
	}

	private static async Task<IReadOnlyList<CourierTrackingResult>?>
		LookUpRecentShipmentsAsync(
			IEnumerable<string> shipmentIds,
			ICourierTrackingService courier,
			CancellationToken cancellationToken)
	{
		// The courier service bounds its own concurrency and times out each
		// lookup, so all lookups can be started together.
		return await Task.WhenAll(shipmentIds
			.Take(RecentShipmentLimit)
			.Select(id => courier.GetTrackingAsync(id, cancellationToken)))
			.ConfigureAwait(false);
	}

	private async Task<Staff> FindActiveStaffAsync(
		string staffId,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(
				"Active staff are required to view the dashboard.");

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		return staff is { IsActive: true }
			? staff
			: throw new UnauthorizedAccessException(
				"Active staff are required to view the dashboard.");
	}

	private sealed record ManagerPanels(
		DashboardSalesSummary? TodaySales,
		IReadOnlyList<LowStockReportRow>? LowStock,
		IReadOnlyList<string> ShipmentIds)
	{
		public static readonly ManagerPanels None = new(null, null, []);
	}
}
