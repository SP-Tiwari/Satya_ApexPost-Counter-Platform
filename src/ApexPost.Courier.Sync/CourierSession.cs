using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

// The client's sign-in state. The token store is the single source of truth:
// sign-in is required whenever it holds no usable token. When the API answers
// 401, AuthHeaderHandler reports it here; the rejected token is cleared and
// SignInRequired is raised, so the console can return to its Login menu.
public sealed class CourierSession(ITokenStore tokens, ILogger<CourierSession> logger)
{
	public bool IsSignInRequired => tokens.GetToken() is null;

	// Raised after a 401. It may be raised on any thread.
	public event EventHandler? SignInRequired;

	// rejectedToken is the token the refused request carried, or null if it
	// carried none. A newer token saved while the request was running is kept.
	internal void OnUnauthorized(string? rejectedToken)
	{
		var current = tokens.GetToken();
		if (current is not null && current.Value != rejectedToken)
			return;

		if (current is not null)
			tokens.ClearToken();

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
