using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Offline.Application;

// Local scan storage on the courier device. Couchbase Lite has a synchronous
// API, so these methods are synchronous; the upload in OutboxService is async.
public interface IScanRepository
{
	// Saves a new scan. It must be Pending and its ID must not exist yet.
	void Create(ShipmentScan scan);

	// Key-value lookup by document ID; null when no such scan exists.
	ShipmentScan? GetById(string id);

	// Replaces an existing scan; throws KeyNotFoundException if it is missing.
	void Update(ShipmentScan scan);

	// Returns false when no such scan exists.
	bool Delete(string id);

	IReadOnlyList<ShipmentScan> ListAll();

	// Ordered by scannedAtUtc, oldest first.
	IReadOnlyList<ShipmentScan> FindByTrackingNumber(string trackingNumber);

	// Ordered by scannedAtUtc, so the outbox uploads the oldest scan first.
	IReadOnlyList<ShipmentScan> ListPending();

	IReadOnlyList<ScanTypeCount> CountByScanType(string courierId);
}
