using ApexPost.Web.Filters;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ApexPost.Web.Controllers;

[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
[Route("[controller]/[action]")]
public sealed class StockController(
	StockAdjustmentApplicationService stockAdjustments,
	ScreenQueryApplicationService screens)
	: Controller, IInvalidModelStateHandler
{
	[HttpGet]
	public async Task<IActionResult> Adjust(
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var catalog = await screens.GetStockAsync(
				staffId, cancellationToken);
			return View(CreateViewModel(catalog));
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

	[HttpPost]
	[ValidateAntiForgeryToken]
	[Audited]
	public async Task<IActionResult> Adjust(
		StockAdjustmentViewModel model,
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		if (!ModelState.IsValid)
			return await RenderFormAsync(
				model,
				StatusCodes.Status400BadRequest,
				cancellationToken);

		try
		{
			await stockAdjustments.AdjustStockAsync(
				staffId,
				model.ProductSku!,
				model.QuantityChange!.Value,
				cancellationToken);
			TempData["StockAdjustmentStatus"] = "Stock adjusted.";
			return RedirectToAction(nameof(Adjust));
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (KeyNotFoundException)
		{
			ModelState.AddModelError(
				string.Empty,
				"The product or stock record was not found for your home branch.");
			return await RenderFormAsync(
				model,
				StatusCodes.Status404NotFound,
				cancellationToken);
		}
		catch (ArgumentException)
		{
			ModelState.AddModelError(
				string.Empty,
				"Enter a valid product SKU and a non-zero stock adjustment quantity.");
			return await RenderFormAsync(
				model,
				StatusCodes.Status400BadRequest,
				cancellationToken);
		}
		catch (OverflowException)
		{
			ModelState.AddModelError(
				nameof(model.QuantityChange),
				"The stock quantity would exceed the supported range.");
			return await RenderFormAsync(
				model,
				StatusCodes.Status409Conflict,
				cancellationToken);
		}
		catch (InvalidOperationException)
		{
			ModelState.AddModelError(
				string.Empty,
				"Stock could not be adjusted. Check available quantity and persisted records.");
			return await RenderFormAsync(
				model,
				StatusCodes.Status409Conflict,
				cancellationToken);
		}
	}

	async Task<IActionResult?> IInvalidModelStateHandler.HandleInvalidModelStateAsync(
		ActionExecutingContext context) =>
		context.ActionArguments.Values.OfType<StockAdjustmentViewModel>()
			.FirstOrDefault() is { } model
			? await RenderFormAsync(
				model,
				StatusCodes.Status400BadRequest,
				context.HttpContext.RequestAborted)
			: null;

	private async Task<IActionResult> RenderFormAsync(
		StockAdjustmentViewModel model,
		int statusCode,
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var catalog = await screens.GetStockAsync(
				staffId, cancellationToken);
			model.BranchName = catalog.Branch.Name;
			model.BranchCode = catalog.Branch.Code;
			model.BranchStocks = catalog.Stock;
			Response.StatusCode = statusCode;
			return View(nameof(Adjust), model);
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

	private static StockAdjustmentViewModel CreateViewModel(
		StockScreen catalog) =>
		new()
		{
			BranchName = catalog.Branch.Name,
			BranchCode = catalog.Branch.Code,
			BranchStocks = catalog.Stock
		};
}
