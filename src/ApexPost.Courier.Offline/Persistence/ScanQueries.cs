using Couchbase.Lite;
using Couchbase.Lite.Query;

namespace ApexPost.Courier.Offline.Persistence;

// Every SQL++ statement the courier store runs. Values always arrive through
// $parameters; nothing a user types is ever joined into the query text.
// "_" is the default collection.
internal static class ScanQueries
{
	public const string TrackingNumberParameter = "trackingNumber";
	public const string SyncStatusParameter = "syncStatus";
	public const string CourierIdParameter = "courierId";
	public const string IdPatternParameter = "idPattern";

	private const string SelectScan =
		"SELECT META().id AS id, trackingNumber, scanType, courierId, location, "
		+ "scannedAtUtc, notes, syncStatus FROM _";

	public const string All =
		SelectScan + " WHERE META().id LIKE $idPattern ORDER BY scannedAtUtc, META().id";

	// Uses idx_scans_trackingNumber.
	public const string ByTrackingNumber =
		SelectScan + " WHERE trackingNumber = $trackingNumber ORDER BY scannedAtUtc, META().id";

	// Uses idx_scans_syncStatus.
	public const string BySyncStatus =
		SelectScan + " WHERE syncStatus = $syncStatus ORDER BY scannedAtUtc, META().id";

	public const string CountBySyncStatus =
		"SELECT COUNT(*) AS scanCount FROM _ WHERE syncStatus = $syncStatus";

	public const string CountByScanTypeForCourier =
		"SELECT scanType, COUNT(*) AS scanCount FROM _ "
		+ "WHERE courierId = $courierId GROUP BY scanType ORDER BY scanType";

	public static List<TResult> Run<TResult>(
		Database database,
		string sql,
		Parameters parameters,
		Func<Result, TResult> map)
	{
		using var query = database.CreateQuery(sql);
		query.Parameters = parameters;
		return query.Execute().Select(map).ToList();
	}

	public static string Explain(Database database, string sql, Parameters parameters)
	{
		using var query = database.CreateQuery(sql);
		query.Parameters = parameters;
		return query.Explain();
	}
}
