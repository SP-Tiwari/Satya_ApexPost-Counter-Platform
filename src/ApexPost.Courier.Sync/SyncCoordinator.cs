using ApexPost.Courier.Offline.Application;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

// D9: runs the outbox when connectivity returns, and on demand.
// - Debounce: a network that comes back starts a sync only after it has stayed
//   available for ReconnectDelay; flapping restarts the wait, and losing the
//   network cancels it.
// - Single flight: at most one sync run at a time. A request while a run is in
//   progress joins that run instead of starting another.
// - Shutdown: DisposeAsync stops listening, cancels a waiting or running sync
//   and waits for it; scans not yet answered stay Pending.
// Each run's outcome is recorded in SyncState. A fresh OutboxService is created
// for each run, so its typed HttpClient is never held for the app's lifetime.
public sealed class SyncCoordinator(
	Func<OutboxService> createOutbox,
	SyncState state,
	INetworkStatus network,
	TimeProvider timeProvider,
	ILogger<SyncCoordinator> logger) : IAsyncDisposable
{
	public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

	private readonly Lock _gate = new();
	private readonly CancellationTokenSource _shutdown = new();
	private ITimer? _reconnectTimer;
	private Task<OutboxRunResult?>? _running;
	private bool _started;
	private bool _disposed;

	// True while a sync run is in progress.
	public bool IsSyncing
	{
		get
		{
			lock (_gate)
				return _running is { IsCompleted: false };
		}
	}

	// Starts listening for connectivity changes.
	public void Start()
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_started)
				throw new InvalidOperationException("The sync coordinator is already started.");

			network.AvailabilityChanged += OnAvailabilityChanged;
			_started = true;
		}
	}

	// Runs the outbox now, or joins the run in progress. Returns the run's
	// result, or null when it was cancelled by shutdown or failed unexpectedly
	// (then SyncState holds the reason). Never throws.
	public Task<OutboxRunResult?> SyncNowAsync()
	{
		lock (_gate)
		{
			if (_disposed)
				return Task.FromResult<OutboxRunResult?>(null);

			if (_running is { IsCompleted: false } running)
				return running;

			// Task.Run: the run starts outside this lock.
			_running = Task.Run(() => RunAsync(_shutdown.Token));
			return _running;
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task<OutboxRunResult?>? running;
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
			if (_started)
				network.AvailabilityChanged -= OnAvailabilityChanged;
			_reconnectTimer?.Dispose();
			_reconnectTimer = null;
			running = _running;
		}

		await _shutdown.CancelAsync().ConfigureAwait(false);
		if (running is not null)
			await running.ConfigureAwait(false);
		_shutdown.Dispose();
	}

	private void OnAvailabilityChanged(object? sender, bool available)
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			_reconnectTimer?.Dispose();
			_reconnectTimer = available
				? timeProvider.CreateTimer(_ => OnReconnected(), null, ReconnectDelay, Timeout.InfiniteTimeSpan)
				: null;
		}

		logger.LogInformation(
			available
				? "Network available; syncing Pending scans if it stays available."
				: "Network lost; scans are saved on the device until it returns.");
	}

	private void OnReconnected()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			_reconnectTimer?.Dispose();
			_reconnectTimer = null;
		}

		if (network.IsAvailable)
			_ = SyncNowAsync();
	}

	private async Task<OutboxRunResult?> RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			var result = await createOutbox()
				.SyncPendingAsync(cancellationToken)
				.ConfigureAwait(false);
			state.RecordRun(result, timeProvider.GetUtcNow());
			return result;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			logger.LogInformation("Sync cancelled by shutdown; unsent scans stay Pending.");
			return null;
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "The sync run failed.");
			state.RecordRunFailure("The sync run failed unexpectedly; scans stay Pending.", timeProvider.GetUtcNow());
			return null;
		}
	}
}
