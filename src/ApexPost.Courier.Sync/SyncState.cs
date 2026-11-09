using ApexPost.Courier.Offline.Application;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace ApexPost.Courier.Sync;

// What the Sync Status screen (D11) shows: when the last sync run finished and
// its counts, the most recent failure reasons, and the circuit breaker's state.
// SyncCoordinator records each run; CourierResilience's circuit-breaker
// callbacks record the circuit state, so it is not tracked anywhere else.
// Thread-safe; Changed may be raised on any thread.
public sealed class SyncState(ILogger<SyncState> logger)
{
	public const int MaxRecentFailures = 10;

	private readonly Lock _gate = new();
	private readonly LinkedList<string> _recentFailures = new();

	private DateTimeOffset? _lastSyncUtc;
	private OutboxRunResult? _lastResult;
	private CircuitState _circuitState = CircuitState.Closed;

	// All the values at one moment, read under one lock, so they always belong
	// together (the last result is the one of the last sync time).
	public SyncSnapshot Snapshot()
	{
		lock (_gate)
			return new SyncSnapshot(_lastSyncUtc, _lastResult, _circuitState, [.. _recentFailures]);
	}

	public event EventHandler? Changed;

	internal void RecordRun(OutboxRunResult result, DateTimeOffset finishedAtUtc)
	{
		lock (_gate)
		{
			_lastSyncUtc = finishedAtUtc;
			_lastResult = result;
			foreach (var failure in result.Failures)
				AddFailure($"{failure.TrackingNumber} {failure.ScanId}: {failure.Reason}");
			if (result.StoppedBecause is { } reason)
				AddFailure($"Sync stopped, {result.Pending} scans still Pending: {reason}");
		}

		RaiseChanged();
	}

	internal void RecordRunFailure(string reason, DateTimeOffset finishedAtUtc)
	{
		lock (_gate)
		{
			_lastSyncUtc = finishedAtUtc;
			_lastResult = null;
			AddFailure(reason);
		}

		RaiseChanged();
	}

	internal void SetCircuitState(CircuitState state)
	{
		lock (_gate)
			_circuitState = state;

		RaiseChanged();
	}

	// A subscriber's failure must not break a sync run or a circuit-breaker callback.
	private void RaiseChanged()
	{
		try
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "A sync-state handler threw.");
		}
	}

	private void AddFailure(string text)
	{
		_recentFailures.AddFirst(text);
		while (_recentFailures.Count > MaxRecentFailures)
			_recentFailures.RemoveLast();
	}
}

// LastSyncUtc: when the last run ended (null before the first). LastResult:
// its counts, or null if it failed unexpectedly. RecentFailures: newest first,
// at most SyncState.MaxRecentFailures.
public sealed record SyncSnapshot(
	DateTimeOffset? LastSyncUtc,
	OutboxRunResult? LastResult,
	CircuitState CircuitState,
	IReadOnlyList<string> RecentFailures);
