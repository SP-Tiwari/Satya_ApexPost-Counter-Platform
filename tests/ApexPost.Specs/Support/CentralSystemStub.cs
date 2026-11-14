using ApexPost.Courier.Domain;
using ApexPost.Courier.Offline.Application;

namespace ApexPost.Specs.Support;

// Stands in for the Shipment API behind the real ApiShipmentUploader, and
// answers each scan as that uploader reports the API's answer: a new scan, or
// one the server already holds, is Synced; a refused scan is Rejected with the
// server's reason. Which scans are held or refused is set by the scenario. The
// API's own de-duplication and rejection rules are covered against SQL Server
// by ScanUploadTests and CourierOfflineSyncTests.
internal sealed class CentralSystemStub : IShipmentUploader
{
	private readonly HashSet<string> _heldScanIds = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _refusals = new(StringComparer.Ordinal);

	// The scan IDs of each upload request, in the order they were sent.
	public List<IReadOnlyList<string>> Requests { get; } = [];

	public void AlreadyHolds(string scanId) => _heldScanIds.Add(scanId);

	public void Refuses(string scanId, string reason) => _refusals[scanId] = reason;

	public Task<IReadOnlyList<ShipmentUploadResult>> UploadAsync(
		IReadOnlyList<ShipmentScan> scans,
		CancellationToken cancellationToken = default)
	{
		Requests.Add([.. scans.Select(scan => scan.Id)]);

		var results = new List<ShipmentUploadResult>();
		foreach (var scan in scans)
		{
			if (_refusals.TryGetValue(scan.Id, out var reason))
			{
				results.Add(ShipmentUploadResult.Failure(scan.Id, reason));
			}
			else
			{
				// A new scan is stored; one already held is a duplicate. The
				// API accepts both, so the uploader reports both as Success.
				_heldScanIds.Add(scan.Id);
				results.Add(ShipmentUploadResult.Success(scan.Id));
			}
		}

		return Task.FromResult<IReadOnlyList<ShipmentUploadResult>>(results);
	}
}
