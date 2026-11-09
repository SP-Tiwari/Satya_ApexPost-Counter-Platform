using System.Buffers.Text;
using System.Text.Json;

namespace ApexPost.Courier.Sync;

// The courier a token belongs to and when it expires.
public sealed record CourierIdentity(string CourierId, DateTimeOffset ExpiresAtUtc);

// Reads the claims of a JWT access token that the client needs: courierId, the
// Courier role and the expiry (exp). It does not check the signature: only the
// API, which holds the signing key, can; a token the API refuses comes back as
// a 401 and is cleared. These checks only stop the client from storing a token
// that cannot work (malformed, expired, not a Courier's), and give each scan
// the courierId the API will compare it with (D2). Nothing here logs or
// returns the token's contents.
public static class CourierTokenReader
{
	// A JWT this long is not one the API issues.
	private const int MaximumLength = 8192;

	public static CourierIdentity? Read(string? token, DateTimeOffset nowUtc, out string error)
	{
		error = "";
		var parts = token?.Trim().Split('.') ?? [];
		if (token is null || token.Length > MaximumLength || parts.Length != 3 || parts.Any(part => part.Length == 0))
		{
			error = "This is not an access token: a JWT has three parts separated by dots.";
			return null;
		}

		JsonDocument payload;
		try
		{
			payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
		}
		catch (Exception exception) when (exception is FormatException or JsonException)
		{
			error = "The access token's claims cannot be read.";
			return null;
		}

		using (payload)
		{
			var claims = payload.RootElement;
			if (claims.ValueKind != JsonValueKind.Object
				|| !claims.TryGetProperty("exp", out var exp)
				|| exp.ValueKind != JsonValueKind.Number
				|| !exp.TryGetInt64(out var expSeconds)
				|| expSeconds is < 0 or > 253402300799)
			{
				error = "The access token has no valid expiry time.";
				return null;
			}

			var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
			if (expiresAtUtc <= nowUtc)
			{
				error = "The access token has expired.";
				return null;
			}

			if (!HasCourierRole(claims))
			{
				error = "The access token is not a Courier's token.";
				return null;
			}

			if (!claims.TryGetProperty("courierId", out var courierId)
				|| courierId.ValueKind != JsonValueKind.String
				|| string.IsNullOrWhiteSpace(courierId.GetString()))
			{
				error = "The access token has no courierId.";
				return null;
			}

			return new CourierIdentity(courierId.GetString()!, expiresAtUtc);
		}
	}

	// The role claim is a string for one role and an array for several.
	private static bool HasCourierRole(JsonElement claims) =>
		claims.TryGetProperty("role", out var role)
		&& role.ValueKind switch
		{
			JsonValueKind.String => role.GetString() == ApexApiClient.CourierRole,
			JsonValueKind.Array => role.EnumerateArray().Any(item =>
				item.ValueKind == JsonValueKind.String && item.GetString() == ApexApiClient.CourierRole),
			_ => false
		};
}
