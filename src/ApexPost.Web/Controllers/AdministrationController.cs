using ApexPost.Web.Filters;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ApexPost.Web.Controllers;

[Authorize(Roles = nameof(StaffRole.Administrator))]
[RequireAdminNetwork]
[Route("[controller]/[action]")]
public sealed class AdministrationController(AdministrationApplicationService administration, ScreenQueryApplicationService screens) : Controller, IInvalidModelStateHandler
{
	// Every administration form posts back to the Index page.
	async Task<IActionResult?> IInvalidModelStateHandler.HandleInvalidModelStateAsync(ActionExecutingContext context) =>
		HttpMethods.IsPost(context.HttpContext.Request.Method)
			? await BadFormAsync(context.HttpContext.RequestAborted)
			: null;

	[HttpGet]
	public async Task<IActionResult> Index(CancellationToken cancellationToken)
	{
		try { return View(ToModel(await screens.GetAdministrationAsync(StaffId(), cancellationToken))); }
		catch (UnauthorizedAccessException) { return Forbid(); }
	}

	[HttpPost, ValidateAntiForgeryToken, Audited]
	public async Task<IActionResult> CreateStaff(CreateStaffViewModel model, CancellationToken cancellationToken)
	{
		if (!ModelState.IsValid) return await BadFormAsync(cancellationToken);
		try { await administration.AddStaffAsync(StaffId(), model.Id!, model.Name!, model.Email!, model.Role!.Value, model.HomeBranchCode!, cancellationToken); return RedirectToAction(nameof(Index)); }
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException) { return await RejectAsync(exception, cancellationToken); }
	}

	[HttpPost, ValidateAntiForgeryToken, Audited]
	public async Task<IActionResult> CreateBranch(CreateBranchViewModel model, CancellationToken cancellationToken)
	{
		if (!ModelState.IsValid) return await BadFormAsync(cancellationToken);
		try { await administration.AddBranchAsync(StaffId(), model.Code!, model.Name!, model.Region!, model.OpeningHours!, model.TerminalId!, model.TerminalName!, cancellationToken); return RedirectToAction(nameof(Index)); }
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return await RejectAsync(exception, cancellationToken); }
	}

	[HttpPost, ValidateAntiForgeryToken, Audited]
	public async Task<IActionResult> CreateProduct(CreateProductViewModel model, CancellationToken cancellationToken)
	{
		if (!ModelState.IsValid || model.UnitPrice is null) return await BadFormAsync(cancellationToken);
		try { await administration.AddProductAsync(StaffId(), model.Sku!, model.Name!, model.Category!.Value, model.UnitPrice.Value, model.Currency!, model.TaxCategory!.Value, cancellationToken); return RedirectToAction(nameof(Index)); }
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return await RejectAsync(exception, cancellationToken); }
	}

	[HttpPost, ValidateAntiForgeryToken, Audited]
	public async Task<IActionResult> SetBranchStatus(SetBranchStatusViewModel model, CancellationToken cancellationToken)
	{
		if (!ModelState.IsValid) return await BadFormAsync(cancellationToken);
		try { await administration.SetBranchStatusAsync(StaffId(), model.Code!, model.IsActive!.Value, cancellationToken); return RedirectToAction(nameof(Index)); }
		catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException) { return await RejectAsync(exception, cancellationToken); }
	}

	[HttpPost, ValidateAntiForgeryToken, Audited]
	public async Task<IActionResult> UpdateStaff(UpdateStaffViewModel model, CancellationToken cancellationToken)
	{
		if (!ModelState.IsValid) return await BadFormAsync(cancellationToken);
		try
		{
			await administration.UpdateStaffAsync(
				StaffId(), model.StaffId!, model.Role!.Value, model.HomeBranchCode!, model.IsActive, cancellationToken);
			return RedirectToAction(nameof(Index));
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
		{
			return await RejectAsync(exception, cancellationToken);
		}
	}

	private string StaffId() => User.FindFirst("StaffId")?.Value ?? throw new UnauthorizedAccessException();
	// A duplicate or a state conflict is 409 and a missing record is 404, as on the
	// other forms. Everything else on these forms is an invalid value.
	private Task<IActionResult> RejectAsync(Exception exception, CancellationToken cancellationToken)
	{
		ModelState.AddModelError(string.Empty, exception is ArgumentException argument
			? argument.Message.Replace($" (Parameter '{argument.ParamName}')", string.Empty, StringComparison.Ordinal)
			: exception.Message);
		return BadFormAsync(cancellationToken, exception switch
		{
			KeyNotFoundException => StatusCodes.Status404NotFound,
			InvalidOperationException => StatusCodes.Status409Conflict,
			_ => StatusCodes.Status400BadRequest
		});
	}

	private async Task<IActionResult> BadFormAsync(
		CancellationToken cancellationToken,
		int statusCode = StatusCodes.Status400BadRequest)
	{
		Response.StatusCode = statusCode;
		return View(nameof(Index), ToModel(await screens.GetAdministrationAsync(StaffId(), cancellationToken)));
	}
	private static AdministrationViewModel ToModel(AdministrationScreen screen) => new() { Branches = screen.Branches, Staff = screen.Staff, Products = screen.Products };
}
