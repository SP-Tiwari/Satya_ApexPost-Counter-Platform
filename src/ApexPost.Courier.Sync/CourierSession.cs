using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

// The client's sign-in state. The token store is the single source of truth:
// sign-in is required whenever it holds no usable token. When the API answers
// 401, AuthHeaderHandler reports it here; the rejected token is cleared and
// SignInRequired is raised, so the console returns to its Login menu.
public sealed class CourierSession(
	ITokenStore tokens,
	TimeProvider timeProvider,
	ILogger<CourierSession> logger)
{
	public bool IsSignInRequired => tokens.GetToken() is null;

	// The signed-in courier, from the stored token's courierId claim; null when
	// no usable token is stored.
	public string? CourierId =>
		tokens.GetToken() is { } token
			? CourierTokenReader.Read(token.Value, timeProvider.GetUtcNow(), out _)?.CourierId
			: null;

	// Raised after a 401. It may be raised on any thread.
	public event EventHandler? SignInRequired;

	// Logout: deletes the stored token.
	public void SignOut()
	{
		tokens.ClearToken();
		logger.LogInformation("Signed out; the access token was deleted.");
	}

	// rejectedToken is the token the refused request carried, or null if it
	// carried none. The rejected token is deleted only if it is still the
	// stored one (one atomic step), so a newer token saved while the request
	// was running is kept, and then sign-in is not required.
	internal void OnUnauthorized(string? rejectedToken)
	{
		var cleared = rejectedToken is not null && tokens.ClearToken(rejectedToken);
		if (!cleared && tokens.GetToken() is not null)
			return;

		logger.LogWarning("The API answered 401 Unauthorized; the access token was cleared and sign-in is required.");
		try
		{
			SignInRequired?.Invoke(this, EventArgs.Empty);
		}
		catch (Exception exception)
		{
			// A subscriber's failure must not turn into a failed HTTP call.
			logger.LogError(exception, "A sign-in-required handler threw.");
		}
	}
}
