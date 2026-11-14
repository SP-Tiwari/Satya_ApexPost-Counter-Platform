namespace ApexPost.Testing;

// A clock the test sets: GetUtcNow always returns the given instant. Timers and
// timestamps still come from the system, so delays and timeouts keep working.
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
	public override DateTimeOffset GetUtcNow() => now;
}
