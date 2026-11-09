using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Sync.Contracts;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

// D9: the real IShipmentUploader, replacing Part A's FakeShipmentUploader. The
// API takes the scans of one shipment per request
// (POST /api/v2/shipments/{trackingNumber}/scans), so a batch from the outbox
// (at most 20 scans, oldest first) is grouped by tracking number, in the order
// each tracking number first appears, and sent one group at a time through
// ApexApiClient (with its retries, circuit breaker and timeouts).
//
// Each scan's result comes from the API's answer:
// - accepted, or a duplicate the server already holds   -> Synced
// - listed in the response's rejections                  -> Rejected (reason)
// - 400, 403, 404 or another final 4xx for the group     -> Rejected (reason)
// - 401, 408, 429, 5xx after retries, an unreadable or
//   inconsistent response, no network, a timeout, an
//   open circuit or an unexpected failure                -> RetryLater
// After a RetryLater group, the remaining groups are not sent: they are
// RetryLater too, so the outbox leaves them Pending and stops the run. A 401
// has already cleared the stored token (AuthHeaderHandler, CourierSession).
public sealed class ApiShipmentUploader(ApexApiClient api, ILogger<ApiShipmentUploader> logger)
	: IShipmentUploader
{
	public async Task<IReadOnlyList<ShipmentUploadResult>> UploadAsync(
		IReadOnlyList<ShipmentScan> scans,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scans);
		if (scans.Count > OutboxService.BatchSize)
		{
			throw new ArgumentException(
				$"A batch holds at most {OutboxService.BatchSize} scans.",
				nameof(scans));
		}

		var results = new List<ShipmentUploadResult>(scans.Count);
		string? stoppedBecause = null;
		foreach (var shipment in scans.GroupBy(scan => scan.TrackingNumber, StringComparer.Ordinal))
		{
			if (stoppedBecause is not null)
			{
				results.AddRange(shipment.Select(scan => ShipmentUploadResult.RetryLater(scan.Id, stoppedBecause)));
				continue;
			}

			var group = shipment.ToList();
			var groupResults = await UploadShipmentAsync(shipment.Key, group, cancellationToken).ConfigureAwait(false);
			results.AddRange(groupResults);
			stoppedBecause = groupResults
				.FirstOrDefault(result => result.Outcome == ShipmentUploadOutcome.RetryLater)
				?.FailureReason;
		}

		return results;
	}

	private async Task<IReadOnlyList<ShipmentUploadResult>> UploadShipmentAsync(
		string trackingNumber,
		List<ShipmentScan> scans,
		CancellationToken cancellationToken)
	{
		ApiResult<ScanUploadResponse> answer;
		try
		{
			answer = await api
				.UploadScansAsync(trackingNumber, new ScanUploadRequest([.. scans.Select(ToUploadItem)]), cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			// No answer: the expected failures (network, timeout, open circuit)
			// and anything unexpected. Either way the scans stay Pending, and
			// the answers already received for earlier shipments are kept.
			var reason = ApiErrors.NoAnswer(exception);
			if (reason is null)
			{
				logger.LogError(exception, "The upload of the scans for {TrackingNumber} failed unexpectedly.", trackingNumber);
				reason = "The upload failed unexpectedly before the server answered.";
			}

			return RetryLater(trackingNumber, scans, reason);
		}

		if (answer.Value is { } response)
			return FromResponse(trackingNumber, scans, response);

		var error = answer.Error!;
		if (IsFinal(error))
		{
			var reason = WithFieldErrors(error);
			logger.LogWarning(
				"The API refused the {Count} scans for {TrackingNumber} (HTTP {Status}): {Reason}",
				scans.Count,
				trackingNumber,
				error.StatusCode,
				reason);
			return [.. scans.Select(scan => ShipmentUploadResult.Failure(scan.Id, reason))];
		}

		return RetryLater(trackingNumber, scans, error.Message);
	}

	// Accepted and duplicate scans are Synced; each rejection is matched to its
	// scan. A rejection naming no scan of this request makes the answer
	// inconsistent, so nothing is decided for the group.
	private IReadOnlyList<ShipmentUploadResult> FromResponse(
		string trackingNumber,
		List<ShipmentScan> scans,
		ScanUploadResponse response)
	{
		var rejections = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var rejection in response.Rejections ?? [])
		{
			if (rejection.ScanId is not { } scanId
				|| !scans.Exists(scan => scan.Id == scanId)
				|| !rejections.TryAdd(scanId, rejection.Reason ?? "Rejected by the server."))
			{
				return RetryLater(trackingNumber, scans, "The server sent a response this client could not read.");
			}
		}

		return
		[
			.. scans.Select(scan => rejections.TryGetValue(scan.Id, out var reason)
				? ShipmentUploadResult.Failure(scan.Id, reason)
				: ShipmentUploadResult.Success(scan.Id))
		];
	}

	private List<ShipmentUploadResult> RetryLater(string trackingNumber, List<ShipmentScan> scans, string reason)
	{
		logger.LogWarning(
			"No final answer for the {Count} scans for {TrackingNumber}; they stay Pending: {Reason}",
			scans.Count,
			trackingNumber,
			reason);
		return [.. scans.Select(scan => ShipmentUploadResult.RetryLater(scan.Id, reason))];
	}

	// A final refusal: any 4xx except 401 (sign in and send again), 408 (a
	// timeout) and 429 (too many requests, try later).
	private static bool IsFinal(ApiError error) =>
		error.Kind is ApiErrorKind.Validation or ApiErrorKind.Forbidden or ApiErrorKind.NotFound
		|| (error.Kind == ApiErrorKind.OtherStatus
			&& error.StatusCode is >= 400 and < 500 and not 408 and not 429);

	private static string WithFieldErrors(ApiError error) =>
		error.FieldErrors.Count == 0
			? error.Message
			: $"{error.Message} {string.Join(" ", error.FieldErrors.Values.SelectMany(messages => messages))}";

	private static ScanUploadItem ToUploadItem(ShipmentScan scan) =>
		new(
			scan.Id,
			scan.TrackingNumber,
			scan.ScanType,
			scan.CourierId,
			scan.Location,
			scan.ScannedAtUtc,
			scan.Notes);
}
