using System.Net;
using ApexPost.Courier.Sync.Contracts;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ApexPost.Courier.Sync;

// D8: turns an unsuccessful response into an ApiError. The body is read as
// ValidationProblemDetails when it is JSON; a missing, non-JSON or malformed
// body falls back to a standard message, never to an exception.
internal static class ApiErrors
{
	private const int MaximumMessageLength = 300;

	private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
		new Dictionary<string, string[]>();

	public static async Task<ApiError> ReadAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		var status = (int)response.StatusCode;
		var kind = status switch
		{
			400 => ApiErrorKind.Validation,
			401 => ApiErrorKind.Unauthorized,
			403 => ApiErrorKind.Forbidden,
			404 => ApiErrorKind.NotFound,
			>= 500 => ApiErrorKind.ServerError,
			_ => ApiErrorKind.OtherStatus
		};

		var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
		var message = kind switch
		{
			ApiErrorKind.Unauthorized =>
				"Your sign-in is missing, invalid or expired. Sign in again.",
			ApiErrorKind.ServerError =>
				"The server could not complete the request. Try again later.",
			_ => Clean(problem?.Detail) ?? Clean(problem?.Title) ?? DefaultMessage(kind, status)
		};

		var fieldErrors = kind == ApiErrorKind.Validation && problem?.Errors is { Count: > 0 } errors
			? errors.ToDictionary(
				pair => pair.Key,
				pair => (pair.Value ?? []).Select(Clean).OfType<string>().ToArray())
			: NoFieldErrors;

		return new ApiError(kind, status, message, fieldErrors);
	}

	// Why no answer arrived, for the failures ApexApiClient throws when the
	// server gave no usable answer (CourierResilience); null for anything else.
	public static string? NoAnswer(Exception exception) => exception switch
	{
		BrokenCircuitException => "Server unavailable – working offline.",
		TimeoutRejectedException or OperationCanceledException => "The API did not answer in time.",
		HttpRequestException => "The API could not be reached.",
		_ => null
	};

	public static ApiError Unexpected(HttpStatusCode statusCode) =>
		new(
			ApiErrorKind.UnexpectedResponse,
			(int)statusCode,
			"The server sent a response this client could not read.",
			NoFieldErrors);

	private static async Task<ValidationProblemDetails?> ReadProblemAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		var mediaType = response.Content.Headers.ContentType?.MediaType;
		return mediaType is "application/problem+json" or "application/json"
			? await ApexApiClient
				.ReadBodyAsync(response, CourierJsonContext.Default.ValidationProblemDetails, cancellationToken)
				.ConfigureAwait(false)
			: null;
	}

	private static string DefaultMessage(ApiErrorKind kind, int status) => kind switch
	{
		ApiErrorKind.Validation => "The server refused the request because some values are invalid.",
		ApiErrorKind.Forbidden => "You are not allowed to do this.",
		ApiErrorKind.NotFound => "The server could not find what was requested.",
		_ => $"The server refused the request (HTTP {status})."
	};

	// Server text is shown in a console, so control characters are removed and
	// the length is limited.
	private static string? Clean(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;

		var printable = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
		if (printable.Length == 0)
			return null;

		return printable.Length <= MaximumMessageLength
			? printable
			: printable[..MaximumMessageLength] + "…";
	}
}
