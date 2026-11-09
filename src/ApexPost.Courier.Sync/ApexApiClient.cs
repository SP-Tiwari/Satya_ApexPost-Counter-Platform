using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ApexPost.Courier.Sync.Contracts;

namespace ApexPost.Courier.Sync;

// D6: the typed client for ApexPost.Api. IHttpClientFactory creates and pools
// its HttpClient (AddCourierSync), whose BaseAddress is the configured API
// root and whose pipeline includes AuthHeaderHandler. Every body goes through
// the source-generated CourierJsonContext. An answer from the server is an
// ApiResult (after any retries, the last answer). A call that never got an
// answer throws, so the caller can keep the scans for a later attempt:
// HttpRequestException (network), TimeoutRejectedException (attempt or overall
// timeout) or BrokenCircuitException (circuit open); see CourierResilience.
// The token request is not marked safe to retry, so it is sent once.
public sealed class ApexApiClient(HttpClient httpClient)
{
	public const string DevelopmentTokenPath = "dev/token";
	public const string CourierRole = "Courier";

	// POST /dev/token for a Courier token carrying this courierId. The endpoint
	// exists only while the API runs in Development.
	public async Task<ApiResult<AccessToken>> RequestCourierTokenAsync(
		string courierId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(courierId);

		using var response = await httpClient
			.PostAsJsonAsync(
				DevelopmentTokenPath,
				new DevelopmentTokenRequest(courierId, CourierRole),
				CourierJsonContext.Default.DevelopmentTokenRequest,
				cancellationToken)
			.ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			return ApiResult<AccessToken>.Failure(
				await ApiErrors.ReadAsync(response, cancellationToken).ConfigureAwait(false));
		}

		var body = await ReadBodyAsync(
				response,
				CourierJsonContext.Default.DevelopmentTokenResponse,
				cancellationToken)
			.ConfigureAwait(false);
		return body is { AccessToken: { } token, ExpiresAtUtc: { } expiresAtUtc }
			&& !string.IsNullOrWhiteSpace(token)
			&& string.Equals(body.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)
			? ApiResult<AccessToken>.Success(new AccessToken(token, expiresAtUtc))
			: ApiResult<AccessToken>.Failure(ApiErrors.Unexpected(response.StatusCode));
	}

	// POST /api/v2/shipments/{trackingNumber}/scans. Needs a Courier token whose
	// courierId is every scan's courierId. Rejections is never null in a
	// successful result. The API puts every submitted scan in exactly one of
	// accepted, duplicatesSkipped or rejected, with one rejections entry per
	// rejected scan; a response that does not add up (including {} or missing
	// counts, which read as 0) is an UnexpectedResponse, never a success.
	public async Task<ApiResult<ScanUploadResponse>> UploadScansAsync(
		string trackingNumber,
		ScanUploadRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(trackingNumber);
		ArgumentNullException.ThrowIfNull(request);

		// Safe to retry: every scan carries its ID, and the API stores an ID
		// once, so a repeated upload only counts the scans as duplicates. The
		// JSON content serializes the same request again for each attempt.
		using var message = new HttpRequestMessage(
			HttpMethod.Post,
			$"api/v2/shipments/{Uri.EscapeDataString(trackingNumber)}/scans")
		{
			Content = JsonContent.Create(request, CourierJsonContext.Default.ScanUploadRequest)
		};
		CourierResilience.MarkRetrySafe(message);

		using var response = await httpClient
			.SendAsync(message, cancellationToken)
			.ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			return ApiResult<ScanUploadResponse>.Failure(
				await ApiErrors.ReadAsync(response, cancellationToken).ConfigureAwait(false));
		}

		var body = await ReadBodyAsync(
				response,
				CourierJsonContext.Default.ScanUploadResponse,
				cancellationToken)
			.ConfigureAwait(false);
		return IsConsistent(body, request.Scans.Count)
			? ApiResult<ScanUploadResponse>.Success(body with { Rejections = body.Rejections ?? [] })
			: ApiResult<ScanUploadResponse>.Failure(ApiErrors.Unexpected(response.StatusCode));
	}

	private static bool IsConsistent(
		[NotNullWhen(true)] ScanUploadResponse? body,
		int submitted) =>
		body is { Accepted: >= 0, DuplicatesSkipped: >= 0, Rejected: >= 0 }
		&& (long)body.Accepted + body.DuplicatesSkipped + body.Rejected == submitted
		&& (body.Rejections?.Count ?? 0) == body.Rejected;

	// A malformed or empty body yields null instead of an exception.
	internal static async Task<T?> ReadBodyAsync<T>(
		HttpResponseMessage response,
		JsonTypeInfo<T> typeInfo,
		CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			return await response.Content
				.ReadFromJsonAsync(typeInfo, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			return null;
		}
	}
}
