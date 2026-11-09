namespace ApexPost.Courier.Sync;

// A bearer access token and when it expires. ToString never includes the
// token, so a token passed to a log message or printed by mistake shows only
// "<redacted>".
public sealed record AccessToken
{
	public AccessToken(string value, DateTimeOffset expiresAtUtc)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		Value = value;
		ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
	}

	public string Value { get; }

	public DateTimeOffset ExpiresAtUtc { get; }

	public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

	public override string ToString() =>
		$"AccessToken {{ Value = <redacted>, ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}
