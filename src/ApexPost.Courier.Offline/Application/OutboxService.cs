using ApexPost.Courier.Offline.Domain;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Offline.Application;

// The outbox: scans are saved locally as Pending, and this service later reads
// them back, oldest first, and uploads them in batches of at most BatchSize.
// Each scan's result decides its status:
// - Synced (accepted, or a duplicate the server already holds) -> Synced;
// - Rejected (a final refusal) -> Failed;
// - RetryLater (no final answer) -> stays Pending.
// A RetryLater result, an uploader exception or a missing result means the
// server could not be reached or did not answer usably, so the run stops after
// that batch; the scans of that batch without a final answer, and every later
// batch, stay Pending for the next run. Cancellation stops the run between
// batches and leaves the remaining scans Pending.
public sealed class OutboxService(
	IScanRepository scans,
	IShipmentUploader uploader,
	ILogger<OutboxService> logger)
{
	public const int BatchSize = 20;

	public async Task<OutboxRunResult> SyncPendingAsync(
		CancellationToken cancellationToken = default)
	{
		var pending = scans.ListPending();
		var attempted = 0;
		var synced = 0;
		var failed = 0;
		var failures = new List<ScanSyncFailure>();
		string? stoppedBecause = null;

		foreach (var batch in pending.Chunk(BatchSize))
		{
			cancellationToken.ThrowIfCancellationRequested();
			attempted += batch.Length;

			var results = await UploadAsync(batch, cancellationToken).ConfigureAwait(false);
			foreach (var scan in batch)
			{
				var result = results.GetValueOrDefault(scan.Id)
					?? ShipmentUploadResult.RetryLater(scan.Id, "No result was returned for this scan.");
				switch (result.Outcome)
				{
					case ShipmentUploadOutcome.Synced:
						scans.Update(scan.MarkSynced());
						synced++;
						logger.LogInformation("Scan {ScanId} synced.", scan.Id);
						break;

					case ShipmentUploadOutcome.Rejected:
						var reason = result.FailureReason ?? "Rejected by the server.";
						scans.Update(scan.MarkSyncFailed());
						failed++;
						failures.Add(new ScanSyncFailure(scan.Id, scan.TrackingNumber, reason));
						logger.LogWarning("Scan {ScanId} failed to sync: {Reason}", scan.Id, reason);
						break;

					default:
						stoppedBecause ??= result.FailureReason ?? "The server gave no final answer.";
						break;
				}
			}

			if (stoppedBecause is not null)
			{
				logger.LogWarning(
					"Sync stopped; the scans without a final answer stay Pending: {Reason}",
					stoppedBecause);
				break;
			}
		}

		return new OutboxRunResult(
			attempted,
			synced,
			failed,
			pending.Count - synced - failed,
			stoppedBecause,
			failures);
	}

	// The uploader's results by scan ID. An exception (other than cancellation)
	// is a batch without a final answer.
	private async Task<Dictionary<string, ShipmentUploadResult>> UploadAsync(
		ShipmentScan[] batch,
		CancellationToken cancellationToken)
	{
		try
		{
			var results = await uploader.UploadAsync(batch, cancellationToken).ConfigureAwait(false);
			var byId = new Dictionary<string, ShipmentUploadResult>(StringComparer.Ordinal);
			foreach (var result in results)
				byId.TryAdd(result.ScanId, result);
			return byId;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Upload of a batch of {Count} scans threw.", batch.Length);
			return batch.ToDictionary(
				scan => scan.Id,
				scan => ShipmentUploadResult.RetryLater(scan.Id, "The upload failed before the server answered."));
		}
	}
}

// Attempted: scans sent to the uploader. Pending: scans that were Pending when
// the run started and still are (no final answer, or not attempted).
// StoppedBecause: why the run stopped early, or null when every batch got a
// final answer. Failures: the scans marked Failed in this run, with reasons.
public sealed record OutboxRunResult(
	int Attempted,
	int Synced,
	int Failed,
	int Pending,
	string? StoppedBecause,
	IReadOnlyList<ScanSyncFailure> Failures);

public sealed record ScanSyncFailure(string ScanId, string TrackingNumber, string Reason);
