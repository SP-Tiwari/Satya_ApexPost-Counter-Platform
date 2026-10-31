namespace ApexPost.Application.Caching;

public sealed record ReferenceData(
	IReadOnlyList<Product> Products,
	IReadOnlyList<ShippingRate> ShippingRates,
	DateTimeOffset LoadedAtUtc);

// In-memory cache of the product catalogue and zone rates, which are read on
// every dashboard and counter-screen view but change rarely. Products and
// rates are immutable, so one cached copy can be shared safely.
//
// GetAsync returns ValueTask: a cache hit completes synchronously without
// allocating a Task or querying the database; only a miss goes to the source.
// The cache is for display only: sale pricing always reads current prices
// inside the sale's unit of work.
public sealed class ReferenceDataCache
{
	private readonly IReferenceDataSource _source;
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _timeToLive;
	private volatile ReferenceData? _cached;

	public ReferenceDataCache(
		IReferenceDataSource source,
		TimeProvider timeProvider,
		TimeSpan timeToLive)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(timeProvider);
		if (timeToLive < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(
				nameof(timeToLive), "The cache lifetime cannot be negative.");

		_source = source;
		_timeProvider = timeProvider;
		_timeToLive = timeToLive;
	}

	public ValueTask<ReferenceData> GetAsync(
		CancellationToken cancellationToken = default)
	{
		var cached = _cached;
		if (cached is not null &&
			_timeProvider.GetUtcNow() - cached.LoadedAtUtc < _timeToLive)
		{
			return ValueTask.FromResult(cached);
		}

		return new ValueTask<ReferenceData>(LoadAsync(cancellationToken));
	}

	// Called after a successful write that changes products or rates.
	public void Invalidate() => _cached = null;

	private async Task<ReferenceData> LoadAsync(
		CancellationToken cancellationToken)
	{
		// Concurrent misses may each load once; the last result wins, which
		// is harmless because the data is equivalent.
		var (products, shippingRates) = await _source.LoadAsync(cancellationToken)
			.ConfigureAwait(false);
		var loaded = new ReferenceData(
			products,
			shippingRates,
			_timeProvider.GetUtcNow());
		_cached = loaded;
		return loaded;
	}
}
