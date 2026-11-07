using ApexPost.Application.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Errors;

// The global exception handler. A conflict that outlasted the unit of work's
// retries is a 409; anything else is a logged 500. The exception text and stack
// trace are added only in Development.
public sealed class ApiExceptionHandler(
	IProblemDetailsService problemDetails,
	IHostEnvironment environment,
	ILogger<ApiExceptionHandler> logger) : IExceptionHandler
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
				"The shipment was changed by someone else. Please try again."),
			DuplicateRecordException => (
				StatusCodes.Status409Conflict,
				"The record already exists."),
			_ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
		};
		if (status == StatusCodes.Status500InternalServerError)
			logger.LogError(exception, "An unexpected error reached the exception handler.");

		httpContext.Response.StatusCode = status;
		return await problemDetails.TryWriteAsync(new ProblemDetailsContext
		{
			HttpContext = httpContext,
			ProblemDetails = new ProblemDetails
			{
				Status = status,
				Title = title,
				Detail = environment.IsDevelopment() ? exception.ToString() : null
			}
		}).ConfigureAwait(false);
	}
}
