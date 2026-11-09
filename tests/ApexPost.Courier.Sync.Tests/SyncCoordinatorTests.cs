using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ApexPost.Courier.Sync.Tests;

// D9: sync at start, when connectivity returns (debounced), after a new scan
// and on demand; one run at a time; none while sign-in is required; a clean
// shutdown. The network is a fake (offline unless a test says otherwise), the
// clock is a FakeTimeProvider that moves only when a test advances it, and the
// uploader waits at a gate the test opens, so a run stays "in progress"
// exactly as long as the test needs.
public sealed class SyncCoordinatorTests : IAsyncLifetime
{
	private const string A = "APX-00000301";

	private readonly FakeTimeProvider _time = new(TestValues.Now);
	private readonly FakeNetwork _network = new() { IsAvailable = false };
	private readonly InMemoryTokenStore _tokens = new() { Token = TestValues.ValidToken() };
	private readonly InMemoryScanRepository _repository = new();
	private readonly GatedUploader _uploader = new();
	private readonly SyncState _state = new(NullLogger<SyncState>.Instance);
	private readonly SyncCoordinator _coordinator;

	public SyncCoordinatorTests()
	{
		_coordinator = Coordinator(() => new OutboxService(_repository, _uploader, NullLogger<OutboxService>.Instance));
		TestScans.Store(_repository, TestScans.For(A, 1), TestScans.For(A, 2));
	}

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync() => await _coordinator.DisposeAsync();

	[Fact]
	public async Task Start_WhenOnline_SyncsAtOnce()
	{
		_network.IsAvailable = true;

		_coordinator.Start();

		Assert.True(_coordinator.IsSyncing);
		_uploader.Open();
		var result = await _coordinator.SyncNowAsync();
		Assert.Equal(2, result!.Synced);
		Assert.Equal(1, _uploader.Calls);
	}

	[Fact]
	public void Start_WhenOffline_StartsNoSync()
	{
		_coordinator.Start();

		Assert.False(_coordinator.IsSyncing);
	}

	[Fact]
	public void SyncIfOnline_SyncsOnlyWhenTheNetworkIsAvailable()
	{
		_coordinator.SyncIfOnline();
		Assert.False(_coordinator.IsSyncing);

		_network.IsAvailable = true;
		_coordinator.SyncIfOnline();
		Assert.True(_coordinator.IsSyncing);
	}

	[Fact]
	public async Task NetworkRestored_StartsASync_AfterTheReconnectDelay()
	{
		_coordinator.Start();

		_network.Change(available: true);
		_time.Advance(SyncCoordinator.ReconnectDelay - TimeSpan.FromMilliseconds(1));
		Assert.False(_coordinator.IsSyncing);

		_time.Advance(TimeSpan.FromMilliseconds(1));
		Assert.True(_coordinator.IsSyncing);

		_uploader.Open();
		var result = await _coordinator.SyncNowAsync();
		Assert.Equal((2, 2, 0, 0), (result!.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Empty(_repository.ListPending());
		var status = _state.Snapshot();
		Assert.Equal(_time.GetUtcNow(), status.LastSyncUtc);
		Assert.Same(result, status.LastResult);
		Assert.Equal(1, _uploader.Calls);
	}

	[Fact]
	public void RepeatedOrFlappingEvents_AreDebounced()
	{
		_coordinator.Start();
		var checksAfterStart = _network.AvailabilityChecks;

		// Flapping: each event restarts the wait, and a loss cancels it.
		_network.Change(available: true);
		_time.Advance(TimeSpan.FromSeconds(2));
		_network.Change(available: true);
		_time.Advance(TimeSpan.FromSeconds(2));
		_network.Change(available: false);
		_time.Advance(SyncCoordinator.ReconnectDelay * 2);
		Assert.False(_coordinator.IsSyncing);
		Assert.Equal(checksAfterStart, _network.AvailabilityChecks);

		// Several events in a row lead to one check and one run.
		_network.Change(available: true);
		_network.Change(available: true);
		_network.Change(available: true);
		_time.Advance(SyncCoordinator.ReconnectDelay);
		Assert.Equal(checksAfterStart + 1, _network.AvailabilityChecks);
		Assert.True(_coordinator.IsSyncing);
	}

	[Fact]
	public void NetworkGoneAgainWhenTheDelayEnds_StartsNoSync()
	{
		_coordinator.Start();

		_network.Change(available: true);
		_network.IsAvailable = false;
		_time.Advance(SyncCoordinator.ReconnectDelay);

		Assert.False(_coordinator.IsSyncing);
	}

	[Fact]
	public async Task OnlyOneRunAtATime_FurtherRequestsJoinTheRunInProgress()
	{
		_coordinator.Start();
		var first = _coordinator.SyncNowAsync();

		var second = _coordinator.SyncNowAsync();
		_network.Change(available: true);
		_time.Advance(SyncCoordinator.ReconnectDelay);
		_coordinator.SyncIfOnline();
		var third = _coordinator.SyncNowAsync();

		Assert.Same(first, second);
		Assert.Same(first, third);
		_uploader.Open();
		await first;
		Assert.Equal(1, _uploader.Calls);
		Assert.Equal(1, _uploader.MaxConcurrent);
	}

	[Fact]
	public async Task AfterARunEnds_TheNextEventStartsANewRun()
	{
		_coordinator.Start();
		_uploader.Open();
		await _coordinator.SyncNowAsync();
		_repository.Create(TestScans.For(A, 3));

		_network.Change(available: true);
		_time.Advance(SyncCoordinator.ReconnectDelay);
		var result = await _coordinator.SyncNowAsync();

		Assert.Equal(2, _uploader.Calls);
		Assert.Equal(1, result!.Synced);
	}

	[Fact]
	public async Task WhileSignInIsRequired_NoTriggerStartsARun()
	{
		_tokens.Token = null;
		_network.IsAvailable = true;

		_coordinator.Start();
		_coordinator.SyncIfOnline();
		_network.Change(available: true);
		_time.Advance(SyncCoordinator.ReconnectDelay);
		var manual = await _coordinator.SyncNowAsync();

		Assert.Null(manual);
		Assert.False(_coordinator.IsSyncing);
		Assert.Equal(0, _uploader.Calls);
		Assert.Equal(2, _repository.ListPending().Count);

		// After signing in again, the next trigger syncs.
		_tokens.Token = TestValues.ValidToken();
		_uploader.Open();
		Assert.Equal(2, (await _coordinator.SyncNowAsync())!.Synced);
	}

	[Fact]
	public async Task Shutdown_CancelsTheRunInProgress_LeavesScansPending_AndIgnoresLaterEvents()
	{
		_coordinator.Start();
		var running = _coordinator.SyncNowAsync();
		Assert.True(await _uploader.Started.WaitAsync(TimeSpan.FromSeconds(10)), "The run never reached the uploader.");

		await _coordinator.DisposeAsync();

		Assert.Null(await running);
		Assert.Equal(2, _repository.ListPending().Count);
		Assert.Null(_state.Snapshot().LastSyncUtc);
		var checks = _network.AvailabilityChecks;
		_network.Change(available: true);
		_time.Advance(SyncCoordinator.ReconnectDelay);
		Assert.Equal(checks, _network.AvailabilityChecks);
		Assert.Null(await _coordinator.SyncNowAsync());
		Assert.Equal(1, _uploader.Calls);
	}

	[Fact]
	public async Task Shutdown_CancelsAWaitingReconnect()
	{
		_coordinator.Start();
		_network.Change(available: true);
		var checks = _network.AvailabilityChecks;

		await _coordinator.DisposeAsync();
		_time.Advance(SyncCoordinator.ReconnectDelay);

		Assert.Equal(checks, _network.AvailabilityChecks);
		Assert.Equal(0, _uploader.Calls);
	}

	[Fact]
	public async Task AnUnexpectedFailure_IsRecorded_AndDoesNotThrow()
	{
		await using var coordinator = Coordinator(() => throw new InvalidOperationException("The database is closed."));

		Assert.Null(await coordinator.SyncNowAsync());

		var status = _state.Snapshot();
		Assert.Equal(_time.GetUtcNow(), status.LastSyncUtc);
		Assert.Equal(["The sync run failed unexpectedly; scans stay Pending."], status.RecentFailures);
	}

	[Fact]
	public void Start_Twice_IsRefused()
	{
		_coordinator.Start();

		Assert.Throws<InvalidOperationException>(_coordinator.Start);
	}

	private SyncCoordinator Coordinator(Func<OutboxService> createOutbox) =>
		new(
			createOutbox,
			new CourierSession(_tokens, _time, NullLogger<CourierSession>.Instance),
			_state,
			_network,
			_time,
			NullLogger<SyncCoordinator>.Instance);

	// Accepts every scan, but only after the test opens the gate; a cancelled
	// run stops waiting.
	private sealed class GatedUploader : IShipmentUploader
	{
		private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _calls;
		private int _active;
		private int _maxConcurrent;

		public SemaphoreSlim Started { get; } = new(0);

		public int Calls => Volatile.Read(ref _calls);

		public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

		public void Open() => _gate.TrySetResult();

		public async Task<IReadOnlyList<ShipmentUploadResult>> UploadAsync(
			IReadOnlyList<ShipmentScan> scans,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _calls);
			var active = Interlocked.Increment(ref _active);
			InterlockedMax(ref _maxConcurrent, active);
			Started.Release();
			try
			{
				await _gate.Task.WaitAsync(cancellationToken);
				return [.. scans.Select(scan => ShipmentUploadResult.Success(scan.Id))];
			}
			finally
			{
				Interlocked.Decrement(ref _active);
			}
		}

		private static void InterlockedMax(ref int target, int value)
		{
			int current;
			while ((current = Volatile.Read(ref target)) < value
				&& Interlocked.CompareExchange(ref target, value, current) != current)
			{
			}
		}
	}
}
