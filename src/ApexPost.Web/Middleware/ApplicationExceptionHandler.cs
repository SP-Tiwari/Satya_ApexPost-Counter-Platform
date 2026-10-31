using ApexPost.Application.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace ApexPost.Web.Middleware;

// Maps errors that reach the exception handler to Problem Details. Controllers
// answer most of these themselves; anything not listed stays a 500.
public sealed class ApplicationExceptionHandler(
	IProblemDetailsService problemDetails) : IExceptionHandler
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
			_ => (0, string.Empty)
		};
		if (status == 0)
			return false;

		httpContext.Response.StatusCode = status;
		return await problemDetails.TryWriteAsync(new ProblemDetailsContext
		{
			HttpContext = httpContext,
			Exception = exception,
			ProblemDetails = new ProblemDetails { Status = status, Title = title }
		}).ConfigureAwait(false);
	}
}
