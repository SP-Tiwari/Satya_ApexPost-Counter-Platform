using System.Diagnostics;

namespace ApexPost.Web.Controllers;

public class HomeController(BranchDashboardApplicationService dashboard) : Controller
{
    // Signed-in staff see their branch dashboard; anonymous visitors see a
    // sign-in prompt.
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var staffId = User.FindFirst("StaffId")?.Value;
        if (string.IsNullOrWhiteSpace(staffId))
            return View(model: null);

        try
        {
            return View(DashboardViewModel.From(await dashboard.GetDashboardAsync(
                staffId, cancellationToken)));
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

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
