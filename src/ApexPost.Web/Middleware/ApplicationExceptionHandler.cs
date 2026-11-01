using ApexPost.Application.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace ApexPost.Web.Middleware;

// Maps errors that reach the exception handler to Problem Details. Controllers
// answer most of these themselves; anything not listed is a 500. Left unhandled,
// that 500 has no body on an MVC route. Once handled, the framework no longer
// logs it, so it is logged here.
public sealed class ApplicationExceptionHandler(
	IProblemDetailsService problemDetails,
	ILogger<ApplicationExceptionHandler> logger) : IExceptionHandler
{
	public async ValueTask<bool> TryHandleAsync(
		HttpContext httpContext,
		Exception exception,
		CancellationToken cancellationToken)
	{
		var (status, title) = exception switch
		{
			ConcurrencyConflictException => (
				StatusCodes.Status409Conflict,
				"The record was changed by someone else."),
			DuplicateRecordException => (
				StatusCodes.Status409Conflict,
				"The record already exists."),
			KeyNotFoundException => (
				StatusCodes.Status404NotFound,
				"The record was not found."),
			ArgumentException => (
				StatusCodes.Status400BadRequest,
				"The request is not valid."),
			_ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
		};
		if (status == StatusCodes.Status500InternalServerError)
			logger.LogError(exception, "An unexpected error reached the exception handler.");

		httpContext.Response.StatusCode = status;
		return await problemDetails.TryWriteAsync(new ProblemDetailsContext
		{
			HttpContext = httpContext,
			Exception = exception,
			ProblemDetails = new ProblemDetails { Status = status, Title = title }
		}).ConfigureAwait(false);
	}
}
