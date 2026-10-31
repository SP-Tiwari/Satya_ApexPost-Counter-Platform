namespace ApexPost.Web.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public sealed class ReportsController(
	LowStockReportApplicationService lowStockReports,
	ShippingVolumeReportApplicationService shippingVolumeReports,
	BranchAuditorReportApplicationService branchAuditorReports,
	DailyBranchSalesReportApplicationService dailySalesReports) : Controller
{
	[HttpGet]
	[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
	public async Task<IActionResult> DailySales(
		DateOnly? businessDate,
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		var reportDate = businessDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
		try
		{
			var report = await dailySalesReports.GetReportAsync(
				staffId,
				reportDate,
				cancellationToken);
			ViewData["BusinessDate"] = reportDate;
			return View(report.Select(DailySalesRowViewModel.From).ToArray());
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	[HttpGet]
	[Authorize(Roles = nameof(StaffRole.Auditor) + "," +
		nameof(StaffRole.Administrator))]
	public async Task<IActionResult> BranchAuditor(
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var report = await branchAuditorReports.GetReportAsync(
				staffId, cancellationToken);
			return View(report.Select(BranchAuditorRowViewModel.From).ToArray());
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	[HttpGet]
	[Authorize(Roles = nameof(StaffRole.Auditor) + "," +
		nameof(StaffRole.Administrator))]
	public async Task<IActionResult> ShippingVolume(
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var report = await shippingVolumeReports.GetReportAsync(
				staffId, cancellationToken);
			return View(report.Select(ShippingVolumeRowViewModel.From).ToArray());
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	[HttpGet]
	[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
	public async Task<IActionResult> LowStock(
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var report = await lowStockReports.GetReportAsync(
				staffId, cancellationToken);
			return View(report.Select(LowStockRowViewModel.From).ToArray());
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (InvalidOperationException)
		{
			return StatusCode(
				StatusCodes.Status503ServiceUnavailable,
				"Low-stock reporting is unavailable because its threshold is not configured.");
		}
	}
}
