namespace ApexPost.Application.Persistence;

// A change kept colliding with other users' changes after every retry. It
// derives from InvalidOperationException because the controllers already
// answer that with 409 and a clear message.
public sealed class ConcurrencyConflictException(
	string message,
	Exception innerException)
	: InvalidOperationException(message, innerException);
