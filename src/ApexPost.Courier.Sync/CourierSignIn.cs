using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

// D10 and D11 Login: obtains or accepts a development access token, checks
// that it is a usable Courier token (CourierTokenReader) and stores it in the
// ITokenStore (DPAPI, never plaintext). The token is never logged or shown.
public sealed class CourierSignIn(
	ApexApiClient api,
	ITokenStore tokens,
	TimeProvider timeProvider,
	ILogger<CourierSignIn> logger)
{
	// Login option 1: POST /dev/token for this courier (Development API only).
	public async Task<SignInResult> RequestDevelopmentTokenAsync(
		string courierId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(courierId);
		courierId = courierId.Trim();

		ApiResult<AccessToken> answer;
		try
		{
			answer = await api.RequestCourierTokenAsync(courierId, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (ApiErrors.NoAnswer(exception) is { } reason)
		{
			return SignInResult.Failure(reason);
		}

		if (answer.Value is not { } token)
			return SignInResult.Failure(answer.Error!.Message);

		var identity = CourierTokenReader.Read(token.Value, timeProvider.GetUtcNow(), out var error);
		if (identity is null)
			return SignInResult.Failure($"The server's token cannot be used: {error}");
		if (identity.CourierId != courierId)
			return SignInResult.Failure("The server issued a token for another courier.");

		return Store(token, identity);
	}

	// Login option 2: a token pasted by the courier, such as one from
	// dotnet user-jwts. It is stored until its own exp claim.
	public SignInResult UseToken(string token)
	{
		var identity = CourierTokenReader.Read(token, timeProvider.GetUtcNow(), out var error);
		return identity is null
			? SignInResult.Failure(error)
			: Store(new AccessToken(token.Trim(), identity.ExpiresAtUtc), identity);
	}

	private SignInResult Store(AccessToken token, CourierIdentity identity)
	{
		tokens.SaveToken(token);
		logger.LogInformation(
			"Signed in as {CourierId} until {ExpiresAtUtc:O}.",
			identity.CourierId,
			identity.ExpiresAtUtc);
		return new SignInResult(true, $"Signed in as {identity.CourierId}.", identity.CourierId);
	}
}

public sealed record SignInResult(bool Succeeded, string Message, string? CourierId)
{
	public static SignInResult Failure(string message) => new(false, message, null);
}
