using ApexPost.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ApexPost.Api.Security;

/// <summary>
/// D2: a courier may upload scans only under their own courierId. If any scan
/// in the batch names another courier, or the token has no courierId claim,
/// the whole batch is refused with a 403 Problem Details and nothing is
/// stored. It runs after [ApiController]'s automatic validation, so a batch
/// that fails validation is still a 400.
/// </summary>
/// <remarks>
/// This is an authorization rule about the caller, not a business rule about
/// scans, so it sits in the API layer as a filter and the controller stays
/// thin.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireOwnScansAttribute : Attribute, IActionFilter
{
	/// <inheritdoc/>
	public void OnActionExecuting(ActionExecutingContext context)
	{
		var upload = context.ActionArguments.Values.OfType<ScanUploadRequest>().SingleOrDefault();
		if (upload?.Scans is null)
			return;

		var courierId = context.HttpContext.User.FindFirst(ApexPostClaims.CourierId)?.Value;
		var refusal = string.IsNullOrWhiteSpace(courierId)
			? "The access token has no courierId claim, so it cannot upload scans."
			: upload.Scans.Any(scan => !string.Equals(scan.CourierId, courierId, StringComparison.Ordinal))
				? $"Every scan must be recorded under your own courierId, {courierId}. No scan in the batch was stored."
				: null;
		if (refusal is null)
			return;

		var problemDetails = context.HttpContext.RequestServices
			.GetRequiredService<ProblemDetailsFactory>()
			.CreateProblemDetails(
				context.HttpContext,
				StatusCodes.Status403Forbidden,
				title: "This courier may not upload these scans.",
				detail: refusal);
		context.Result = new ObjectResult(problemDetails)
		{
			StatusCode = StatusCodes.Status403Forbidden,
			ContentTypes = { "application/problem+json" }
		};
	}

	/// <inheritdoc/>
	public void OnActionExecuted(ActionExecutedContext context)
	{
	}
}
