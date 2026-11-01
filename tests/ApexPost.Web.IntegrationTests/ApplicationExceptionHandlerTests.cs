using ApexPost.Application.Persistence;
using ApexPost.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApexPost.Web.IntegrationTests;

// Pins ADR 0025. Controllers answer state conflicts themselves, so an
// InvalidOperationException that reaches this handler is a configuration or data
// fault. It must stay a logged 500, while the two conflict types derived from it
// stay 409.
public sealed class ApplicationExceptionHandlerTests
{
	[Fact]
	public async Task PlainInvalidOperationException_StaysA500()
	{
		var (status, title) = await HandleAsync(
			new InvalidOperationException("Reports:LowStockThreshold is not configured."));

		Assert.Equal(StatusCodes.Status500InternalServerError, status);
		Assert.Equal("An unexpected error occurred.", title);
	}

	[Fact]
	public async Task ConcurrencyConflict_StaysA409()
	{
		var (status, _) = await HandleAsync(
			new ConcurrencyConflictException("conflict", new InvalidOperationException()));

		Assert.Equal(StatusCodes.Status409Conflict, status);
	}

	[Fact]
	public async Task DuplicateRecord_StaysA409()
	{
		var (status, _) = await HandleAsync(
			new DuplicateRecordException("duplicate", new InvalidOperationException()));

		Assert.Equal(StatusCodes.Status409Conflict, status);
	}

	private static async Task<(int Status, string? Title)> HandleAsync(Exception exception)
	{
		var writer = new RecordingProblemDetailsService();
		var handler = new ApplicationExceptionHandler(
			writer, NullLogger<ApplicationExceptionHandler>.Instance);
		var context = new DefaultHttpContext();

		Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));

		return (context.Response.StatusCode, writer.Written?.ProblemDetails.Title);
	}

	private sealed class RecordingProblemDetailsService : IProblemDetailsService
	{
		public ProblemDetailsContext? Written { get; private set; }

		public ValueTask WriteAsync(ProblemDetailsContext context)
		{
			Written = context;
			return ValueTask.CompletedTask;
		}

		public async ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
		{
			await WriteAsync(context);
			return true;
		}
	}
}
