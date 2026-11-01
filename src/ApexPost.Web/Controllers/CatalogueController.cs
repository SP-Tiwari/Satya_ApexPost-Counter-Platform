namespace ApexPost.Web.Controllers;

// Read-only product catalogue with stock on hand at the signed-in staff
// member's home branch. Stock changes go through StockController.
[Authorize]
public sealed class CatalogueController(
	ScreenQueryApplicationService screens) : Controller
{
	[HttpGet]
	public async Task<IActionResult> Index(CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			return View(await screens.GetCatalogueAsync(
				staffId, cancellationToken));
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
	}
}
