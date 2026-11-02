using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using Couchbase.Lite;
using Couchbase.Lite.Query;

namespace ApexPost.Courier.Offline.Persistence;

// IScanRepository over the default collection of apexpost_courier. Get, update
// and delete are key-value operations on the document ID; the lists are SQL++
// queries with parameters (ScanQueries).
public sealed class CouchbaseScanRepository(CourierDatabase database) : IScanRepository
{
	public void Create(ShipmentScan scan)
	{
		ArgumentNullException.ThrowIfNull(scan);
		if (scan.SyncStatus != SyncStatus.Pending)
		{
			throw new InvalidOperationException(
				"A new scan must be saved locally as Pending.");
		}

		using var document = new MutableDocument(scan.Id);
		ShipmentScanDocument.Write(document, scan);

		// FailOnConflict refuses to overwrite an existing document with this ID.
		if (!database.Collection.Save(document, ConcurrencyControl.FailOnConflict))
		{
			throw new InvalidOperationException($"Scan {scan.Id} already exists.");
		}
	}

	public ShipmentScan? GetById(string id)
	{
		ShipmentScanId.Validate(id);

		using var document = database.Collection.GetDocument(id);
		return document is null ? null : ShipmentScanDocument.Read(document.Id, document);
	}

	public void Update(ShipmentScan scan)
	{
		ArgumentNullException.ThrowIfNull(scan);

		using var existing = database.Collection.GetDocument(scan.Id)
			?? throw new KeyNotFoundException($"Scan {scan.Id} was not found.");
		using var document = existing.ToMutable();
		ShipmentScanDocument.Write(document, scan);

		if (!database.Collection.Save(document, ConcurrencyControl.FailOnConflict))
		{
			throw new InvalidOperationException(
				$"Scan {scan.Id} was changed by another writer; reload it and try again.");
		}
	}

	public bool Delete(string id)
	{
		ShipmentScanId.Validate(id);

		using var document = database.Collection.GetDocument(id);
		if (document is null)
			return false;

		if (!database.Collection.Delete(document, ConcurrencyControl.FailOnConflict))
		{
			throw new InvalidOperationException(
				$"Scan {id} was changed by another writer; reload it and try again.");
		}

		return true;
	}

	public IReadOnlyList<ShipmentScan> ListAll() =>
		Scans(
			ScanQueries.All,
			new Parameters().SetString(
				ScanQueries.IdPatternParameter,
				ShipmentScanId.Prefix + "%"));

	public IReadOnlyList<ShipmentScan> FindByTrackingNumber(string trackingNumber)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(trackingNumber);

		return Scans(
			ScanQueries.ByTrackingNumber,
			new Parameters().SetString(
				ScanQueries.TrackingNumberParameter,
				trackingNumber.Trim()));
	}

	public IReadOnlyList<ShipmentScan> ListPending() =>
		Scans(
			ScanQueries.BySyncStatus,
			new Parameters().SetString(
				ScanQueries.SyncStatusParameter,
				nameof(SyncStatus.Pending)));

	public IReadOnlyList<ScanTypeCount> CountByScanType(string courierId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(courierId);

		return ScanQueries.Run(
			database.Database,
			ScanQueries.CountByScanTypeForCourier,
			new Parameters().SetString(ScanQueries.CourierIdParameter, courierId.Trim()),
			row => new ScanTypeCount(
				Enum.Parse<ScanType>(row.GetString("scanType")!),
				(int)row.GetLong("scanCount")));
	}

	// The query plan SQLite chooses, for evidence that the value indexes are used.
	internal string ExplainFindByTrackingNumber(string trackingNumber) =>
		ScanQueries.Explain(
			database.Database,
			ScanQueries.ByTrackingNumber,
			new Parameters().SetString(ScanQueries.TrackingNumberParameter, trackingNumber));

	internal string ExplainListPending() =>
		ScanQueries.Explain(
			database.Database,
			ScanQueries.BySyncStatus,
			new Parameters().SetString(
				ScanQueries.SyncStatusParameter,
				nameof(SyncStatus.Pending)));

	private List<ShipmentScan> Scans(string sql, Parameters parameters) =>
		ScanQueries.Run(
			database.Database,
			sql,
			parameters,
			row => ShipmentScanDocument.Read(row.GetString("id")!, row));
}
