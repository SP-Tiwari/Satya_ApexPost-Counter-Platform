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

	public DateTimeOffset? LastSyncUtc
	{
		get
		{
			lock (_gate)
				return field;
		}

		private set;
	}

	public OutboxRunResult? LastResult
	{
		get
		{
			lock (_gate)
				return field;
		}

		private set;
	}

	public CircuitState CircuitState
	{
		get
		{
			lock (_gate)
				return field;
		}

		private set;
	} = CircuitState.Closed;

	// Newest first.
	public IReadOnlyList<string> RecentFailures
	{
		get
		{
			lock (_gate)
				return [.. _recentFailures];
		}
	}

	public event EventHandler? Changed;

	internal void RecordRun(OutboxRunResult result, DateTimeOffset finishedAtUtc)
	{
		lock (_gate)
		{
			LastSyncUtc = finishedAtUtc;
			LastResult = result;
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
			LastSyncUtc = finishedAtUtc;
			LastResult = null;
			AddFailure(reason);
		}

		RaiseChanged();
	}

	internal void SetCircuitState(CircuitState state)
	{
		lock (_gate)
			CircuitState = state;

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
