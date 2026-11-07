namespace ApexPost.Courier.Offline.Domain;

// One courier scan of a shipment. Every property validates itself in its init
// accessor, so a copy made with "with" is checked as well as a new scan.
public sealed record ShipmentScan
{
	public required string Id
	{
		get;
		init => field = ShipmentScanId.Validate(value, nameof(Id));
	}

	public required string TrackingNumber
	{
		get;
		init => field = Required(value, nameof(TrackingNumber));
	}

	public required ScanType ScanType
	{
		get;
		init => field = Defined(value, nameof(ScanType));
	}

	public required string CourierId
	{
		get;
		init => field = Required(value, nameof(CourierId));
	}

	public required string Location
	{
		get;
		init => field = Required(value, nameof(Location));
	}

	public required DateTimeOffset ScannedAtUtc
	{
		get;
		init
		{
			if (value.Offset != TimeSpan.Zero)
			{
				throw new ArgumentException(
					"The scan time must be in UTC.",
					nameof(ScannedAtUtc));
			}

			field = value;
		}
	}

	public string? Notes
	{
		get;
		init => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	public required SyncStatus SyncStatus
	{
		get;
		init => field = Defined(value, nameof(SyncStatus));
	}

	// A new scan always starts Pending: it is saved on the device first and
	// uploaded later by the outbox.
	public static ShipmentScan Record(
		string trackingNumber,
		ScanType scanType,
		string courierId,
		string location,
		DateTimeOffset scannedAtUtc,
		string? notes = null) =>
		new()
		{
			Id = ShipmentScanId.New(),
			TrackingNumber = trackingNumber,
			ScanType = scanType,
			CourierId = courierId,
			Location = location,
			ScannedAtUtc = scannedAtUtc,
			Notes = notes,
			SyncStatus = SyncStatus.Pending
		};

	public ShipmentScan MarkSynced() => this with { SyncStatus = SyncStatus.Synced };

	public ShipmentScan MarkSyncFailed() => this with { SyncStatus = SyncStatus.Failed };

	private static string Required(string value, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException(
				$"{parameterName} cannot be empty.",
				parameterName);
		}

		return value.Trim();
	}

	private static TEnum Defined<TEnum>(TEnum value, string parameterName)
		where TEnum : struct, Enum
	{
		if (!Enum.IsDefined(value))
		{
			throw new ArgumentOutOfRangeException(
				parameterName,
				$"{value} is not a valid {typeof(TEnum).Name}.");
		}

		return value;
	}
}
