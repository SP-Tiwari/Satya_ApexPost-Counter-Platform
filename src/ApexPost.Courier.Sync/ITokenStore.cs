namespace ApexPost.Courier.Sync;

// Where the courier client keeps its access token between runs. The console
// client's substitute for MAUI SecureStorage (D10); see DpapiTokenStore.
public interface ITokenStore
{
	// The stored token, or null when none is stored, it has expired, or it
	// cannot be read. An expired or unreadable token is deleted.
	AccessToken? GetToken();

	void SaveToken(AccessToken token);

	// Signs out: deletes the stored token. Does nothing when none is stored.
	void ClearToken();
}
