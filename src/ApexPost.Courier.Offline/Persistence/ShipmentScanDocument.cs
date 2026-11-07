using System.Globalization;
using ApexPost.Courier.Offline.Domain;
using Couchbase.Lite;

namespace ApexPost.Courier.Offline.Persistence;

// Explicit mapping between ShipmentScan and its JSON document. The domain type
// never sees Couchbase; this is the only place that knows the field names.
internal static class ShipmentScanDocument
{
	public const string TrackingNumber = "trackingNumber";
	public const string ScanType = "scanType";
	public const string CourierId = "courierId";
	public const string Location = "location";
	public const string ScannedAtUtc = "scannedAtUtc";
	public const string Notes = "notes";
	public const string SyncStatus = "syncStatus";

	// Fixed width and always UTC, so comparing the strings orders the scans by
	// time, which ORDER BY scannedAtUtc relies on.
	private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

	public static string FormatTimestamp(DateTimeOffset value) =>
		value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

	public static void Write(MutableDocument document, ShipmentScan scan)
	{
		document.SetString(TrackingNumber, scan.TrackingNumber);
		document.SetString(ScanType, scan.ScanType.ToString());
		document.SetString(CourierId, scan.CourierId);
		document.SetString(Location, scan.Location);
		document.SetString(ScannedAtUtc, FormatTimestamp(scan.ScannedAtUtc));
		document.SetString(Notes, scan.Notes);
		document.SetString(SyncStatus, scan.SyncStatus.ToString());
	}

	// Reads a stored document or a query row. A value the domain would reject
	// means the stored data is corrupt, so it fails loudly with the document ID.
	public static ShipmentScan Read(string id, IDictionaryObject values)
	{
		try
		{
			return new ShipmentScan
			{
				Id = id,
				TrackingNumber = Text(values, TrackingNumber),
				ScanType = Parse<ScanType>(Text(values, ScanType), ScanType),
				CourierId = Text(values, CourierId),
				Location = Text(values, Location),
				ScannedAtUtc = ParseTimestamp(Text(values, ScannedAtUtc)),
				Notes = values.GetString(Notes),
				SyncStatus = Parse<SyncStatus>(Text(values, SyncStatus), SyncStatus)
			};
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				$"Document {id} is not a valid shipment scan: {exception.Message}",
				exception);
		}
	}

	private static string Text(IDictionaryObject values, string key) =>
		values.GetString(key)
		?? throw new ArgumentException($"{key} is missing.", key);

	// Names only: Enum.Parse would also accept a number such as "7".
	private static TEnum Parse<TEnum>(string text, string key)
		where TEnum : struct, Enum =>
		Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
			? Enum.Parse<TEnum>(text)
			: throw new ArgumentException($"{key} has an unknown value.", key);

	private static DateTimeOffset ParseTimestamp(string text) =>
		DateTimeOffset.TryParseExact(
			text,
			TimestampFormat,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
			out var value)
			? value
			: throw new ArgumentException(
				$"{ScannedAtUtc} is not a UTC timestamp.",
				ScannedAtUtc);
}
