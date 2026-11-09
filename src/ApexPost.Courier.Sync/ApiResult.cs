namespace ApexPost.Courier.Sync;

// The outcome of an API call that reached the server: either the response
// value or an ApiError. A call that did not reach the server (no network,
// DNS, TLS, a timeout) throws instead, so it is never mistaken for an answer.
public sealed class ApiResult<T>
	where T : class
{
	private ApiResult(T? value, ApiError? error)
	{
		Value = value;
		Error = error;
	}

	public T? Value { get; }

	public ApiError? Error { get; }

	public bool Succeeded => Error is null;

	public static ApiResult<T> Success(T value) => new(value, null);

	public static ApiResult<T> Failure(ApiError error) => new(null, error);
}

public enum ApiErrorKind
{
	// 400: the request was invalid; FieldErrors has the messages per field.
	Validation,

	// 401: no token, or the token is invalid or expired. It has been cleared.
	Unauthorized,

	// 403: signed in, but not allowed (wrong role, or another courier's scans).
	Forbidden,

	// 404: the shipment does not exist.
	NotFound,

	// Any other unsuccessful status below 500.
	OtherStatus,

	// 5xx: the server failed.
	ServerError,

	// A success status whose body is missing, malformed or incomplete.
	UnexpectedResponse
}

// An API error in words a courier can act on. Message never contains a token,
// a request body or an internal exception message; for a 5xx it is a fixed
// sentence, because the server's text is not meant for users.
public sealed record ApiError(
	ApiErrorKind Kind,
	int StatusCode,
	string Message,
	IReadOnlyDictionary<string, string[]> FieldErrors);
