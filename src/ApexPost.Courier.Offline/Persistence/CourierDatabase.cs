using ApexPost.Courier.Offline.Domain;
using Couchbase.Lite;
using Couchbase.Lite.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApexPost.Courier.Offline.Persistence;

// Owns the device's local Couchbase Lite database, apexpost_courier, and its
// default collection. Opening it creates the value indexes; disposing it removes
// the change listener first and then closes the database. A lock file keeps a
// second process (or a second instance in this one) from opening the same
// database at the same time; see OFFLINE-DESIGN.md.
public sealed class CourierDatabase : IDisposable
{
	public const string DatabaseName = "apexpost_courier";
	public const string TrackingNumberIndexName = "idx_scans_trackingNumber";
	public const string SyncStatusIndexName = "idx_scans_syncStatus";

	private const string LockFileName = DatabaseName + ".lock";

	private readonly Lock _gate = new();
	private readonly ILogger<CourierDatabase> _logger;
	private readonly FileStream _processLock;
	private readonly Database _database;
	private readonly Collection _collection;
	private ListenerToken? _listenerToken;
	private bool _disposed;

	public CourierDatabase(string directory, ILogger<CourierDatabase>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		_logger = logger ?? NullLogger<CourierDatabase>.Instance;
		Directory.CreateDirectory(directory);
		_processLock = AcquireProcessLock(directory);

		Database? database = null;
		try
		{
			database = new Database(
				DatabaseName,
				new DatabaseConfiguration { Directory = directory });
			_collection = database.GetDefaultCollection();

			// Creating an index that already exists with the same definition is a
			// no-op, so this is safe on every open.
			_collection.CreateIndex(
				TrackingNumberIndexName,
				new ValueIndexConfiguration(ShipmentScanDocument.TrackingNumber));
			_collection.CreateIndex(
				SyncStatusIndexName,
				new ValueIndexConfiguration(ShipmentScanDocument.SyncStatus));
		}
		catch
		{
			database?.Dispose();
			_processLock.Dispose();
			throw;
		}

		_database = database;
		_logger.LogInformation("Opened {DatabaseName} at {Path}.", DatabaseName, _database.Path);
	}

	public string Path
	{
		get
		{
			ThrowIfDisposed();
			return _database.Path!;
		}
	}

	public bool IsChangeListenerRegistered
	{
		get
		{
			lock (_gate)
			{
				return _listenerToken is not null;
			}
		}
	}

	internal Database Database
	{
		get
		{
			ThrowIfDisposed();
			return _database;
		}
	}

	internal Collection Collection
	{
		get
		{
			ThrowIfDisposed();
			return _collection;
		}
	}

	public int CountPendingScans()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return CountPendingCore();
		}
	}

	// Registers the one collection change listener. It logs the IDs of the
	// changed documents and the current number of pending scans, then passes the
	// same values to onChange. Couchbase Lite calls it on a background thread.
	public void StartChangeListener(Action<ScanChangeNotification>? onChange = null)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_listenerToken is not null)
			{
				throw new InvalidOperationException(
					"The change listener is already registered.");
			}

			_listenerToken = _collection.AddChangeListener(
				(_, change) => OnCollectionChanged(change.DocumentIDs, onChange));
		}

		_logger.LogInformation("Change listener registered.");
	}

	public void StopChangeListener()
	{
		ListenerToken? token;
		lock (_gate)
		{
			token = _listenerToken;
			_listenerToken = null;
		}

		// Removed outside the lock: a callback that is running may be waiting
		// for the lock.
		if (token is not null)
		{
			token.Value.Remove();
			_logger.LogInformation("Change listener removed.");
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
		}

		StopChangeListener();

		lock (_gate)
		{
			_database.Close();
			_database.Dispose();
		}

		_processLock.Dispose();
		_logger.LogInformation("Closed {DatabaseName}.", DatabaseName);
	}

	private void OnCollectionChanged(
		IReadOnlyList<string> documentIds,
		Action<ScanChangeNotification>? onChange)
	{
		int pending;
		lock (_gate)
		{
			// A change delivered after Stop or Dispose is ignored.
			if (_disposed || _listenerToken is null)
				return;

			pending = CountPendingCore();
		}

		_logger.LogInformation(
			"Changed documents: {DocumentIds}. Pending scans: {PendingCount}.",
			string.Join(", ", documentIds),
			pending);

		try
		{
			onChange?.Invoke(new ScanChangeNotification(documentIds, pending));
		}
		catch (Exception exception)
		{
			// An exception must not escape onto Couchbase Lite's callback thread.
			_logger.LogError(exception, "The change handler threw.");
		}
	}

	private int CountPendingCore() =>
		(int)ScanQueries.Run(
				_database,
				ScanQueries.CountBySyncStatus,
				new Parameters().SetString(
					ScanQueries.SyncStatusParameter,
					nameof(SyncStatus.Pending)),
				row => row.GetLong("scanCount"))
			.Single();

	private static FileStream AcquireProcessLock(string directory)
	{
		try
		{
			return new FileStream(
				System.IO.Path.Combine(directory, LockFileName),
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None,
				bufferSize: 1,
				FileOptions.DeleteOnClose);
		}
		catch (IOException exception)
		{
			throw new InvalidOperationException(
				$"{DatabaseName} is already open in another process or instance. "
				+ "An embedded database must have a single owner.",
				exception);
		}
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed record ScanChangeNotification(
	IReadOnlyList<string> DocumentIds,
	int PendingCount);
