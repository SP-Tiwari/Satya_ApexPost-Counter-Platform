using ApexPost.Courier.Offline.Domain;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Offline.Application;

// The outbox: scans are saved locally as Pending, and this service later reads
// them back and uploads each one. A scan the uploader accepts becomes Synced;
// one it rejects, or that throws, becomes Failed. Cancellation stops the run
// and leaves the remaining scans Pending.
public sealed class OutboxService(
	IScanRepository scans,
	IShipmentUploader uploader,
	ILogger<OutboxService> logger)
{
	public async Task<OutboxRunResult> SyncPendingAsync(
		CancellationToken cancellationToken = default)
	{
		var pending = scans.ListPending();
		var synced = 0;
		var failed = 0;

		foreach (var scan in pending)
		{
			cancellationToken.ThrowIfCancellationRequested();

			ShipmentUploadResult result;
			try
			{
				result = await uploader
					.UploadAsync(scan, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Upload of scan {ScanId} threw.", scan.Id);
				result = ShipmentUploadResult.Failure(exception.Message);
			}

			if (result.Succeeded)
			{
				scans.Update(scan.MarkSynced());
				synced++;
				logger.LogInformation("Scan {ScanId} synced.", scan.Id);
			}
			else
			{
				scans.Update(scan.MarkSyncFailed());
				failed++;
				logger.LogWarning(
					"Scan {ScanId} failed to sync: {Reason}",
					scan.Id,
					result.FailureReason);
			}
		}

		return new OutboxRunResult(pending.Count, synced, failed);
	}
}

public sealed record OutboxRunResult(int Attempted, int Synced, int Failed);
