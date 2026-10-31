namespace ApexPost.Application.Persistence;

// A save broke a unique key, such as two simultaneous submissions of one
// transaction ID. It derives from InvalidOperationException because the
// controllers already answer that with 409.
public sealed class DuplicateRecordException(
	string message,
	Exception innerException)
	: InvalidOperationException(message, innerException);
