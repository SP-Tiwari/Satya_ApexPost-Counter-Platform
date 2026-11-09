using System.Net;

namespace ApexPost.Courier.Sync.Tests;

// D10 and D11 Login: reading a token's claims, obtaining or pasting a token,
// storing it, and the session built on the stored token.
public sealed class SignInTests
{
	private static readonly DateTimeOffset Now = TestValues.Now;

	[Theory]
	[InlineData("Courier")]
	[InlineData("Courier", "Admin")]
	public void Reader_AcceptsACourierToken_AndReadsItsCourierIdAndExpiry(params string[] roles)
	{
		var expires = Now.AddMinutes(30);

		var identity = CourierTokenReader.Read(TestJwt.Create("COURIER-DEMO-07", expires, roles), Now, out var error);

		Assert.Equal(new CourierIdentity("COURIER-DEMO-07", expires), identity);
		Assert.Equal("", error);
	}

	[Theory]
	[InlineData("", "This is not an access token: a JWT has three parts separated by dots.")]
	[InlineData("abc", "This is not an access token: a JWT has three parts separated by dots.")]
	[InlineData("a..c", "This is not an access token: a JWT has three parts separated by dots.")]
	[InlineData("a.b.c.d", "This is not an access token: a JWT has three parts separated by dots.")]
	[InlineData("a.%%%.c", "The access token's claims cannot be read.")]
	[InlineData("a.bm90IGpzb24.c", "The access token's claims cannot be read.")]
	public void Reader_RefusesWhatIsNotAJwt(string token, string expected)
	{
		Assert.Null(CourierTokenReader.Read(token, Now, out var error));
		Assert.Equal(expected, error);
	}

	[Fact]
	public void Reader_RefusesNullAndOverlongInput()
	{
		Assert.Null(CourierTokenReader.Read(null, Now, out _));
		Assert.Null(CourierTokenReader.Read(new string('a', 5000) + "." + new string('b', 5000) + ".c", Now, out _));
	}

	[Theory]
	[InlineData("""{"courierId":"C","role":"Courier"}""", "The access token has no valid expiry time.")]
	[InlineData("""{"courierId":"C","role":"Courier","exp":"soon"}""", "The access token has no valid expiry time.")]
	[InlineData("""[1,2]""", "The access token has no valid expiry time.")]
	[InlineData("""{"courierId":"C","role":"Admin","exp":4102444800}""", "The access token is not a Courier's token.")]
	[InlineData("""{"courierId":"C","exp":4102444800}""", "The access token is not a Courier's token.")]
	[InlineData("""{"role":"Courier","exp":4102444800}""", "The access token has no courierId.")]
	[InlineData("""{"courierId":" ","role":"Courier","exp":4102444800}""", "The access token has no courierId.")]
	public void Reader_RefusesATokenWithoutUsableClaims(string claims, string expected)
	{
		var token = $"{TestJwt.Part("{}")}.{TestJwt.Part(claims)}.c2ln";

		Assert.Null(CourierTokenReader.Read(token, Now, out var error));
		Assert.Equal(expected, error);
		Assert.DoesNotContain("Courier\"", error);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-60)]
	public void Reader_RefusesAnExpiredToken(int secondsLeft)
	{
		var token = TestJwt.Create("C", Now.AddSeconds(secondsLeft), "Courier");

		Assert.Null(CourierTokenReader.Read(token, Now, out var error));
		Assert.Equal("The access token has expired.", error);
	}

	[Fact]
	public async Task DevelopmentToken_IsRequested_Checked_AndStored()
	{
		var issued = TestJwt.Courier();
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accessToken":"{{issued}}","tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00Z"}"""));

		var result = await host.Get<CourierSignIn>().RequestDevelopmentTokenAsync(" COURIER-DEMO-07 ");

		Assert.Equal(new SignInResult(true, "Signed in as COURIER-DEMO-07.", "COURIER-DEMO-07"), result);
		Assert.Equal(new AccessToken(issued, new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero)), host.Tokens.Token);
		Assert.Equal("""{"courierId":"COURIER-DEMO-07","role":"Courier"}""", host.Network.Requests[0].Body);
		Assert.Equal("COURIER-DEMO-07", host.Session.CourierId);
		Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(issued, StringComparison.Ordinal));
	}

	[Fact]
	public async Task DevelopmentToken_ForAnotherCourier_IsNotStored()
	{
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accessToken":"{{TestJwt.Courier("COURIER-DEMO-99")}}","tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00Z"}"""));

		var result = await host.Get<CourierSignIn>().RequestDevelopmentTokenAsync("COURIER-DEMO-07");

		Assert.Equal(SignInResult.Failure("The server issued a token for another courier."), result);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public async Task DevelopmentToken_ThatIsNotACourierJwt_IsNotStored()
	{
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			"""{"accessToken":"opaque-token","tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00Z"}"""));

		var result = await host.Get<CourierSignIn>().RequestDevelopmentTokenAsync("COURIER-DEMO-07");

		Assert.Equal(
			SignInResult.Failure("The server's token cannot be used: This is not an access token: a JWT has three parts separated by dots."),
			result);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public async Task DevelopmentToken_RefusedByTheServer_ShowsItsMessage()
	{
		using var host = new ClientHost(_ => Responses.Problem(
			HttpStatusCode.BadRequest,
			"""{"title":"One or more validation errors occurred.","status":400,"errors":{"courierId":["The courierId must be 1 to 50 characters."]}}"""));

		var result = await host.Get<CourierSignIn>().RequestDevelopmentTokenAsync("COURIER-DEMO-07");

		Assert.Equal(SignInResult.Failure("One or more validation errors occurred."), result);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public async Task DevelopmentToken_WithoutAnAnswer_SaysTheApiCouldNotBeReached()
	{
		using var host = new ClientHost();
		host.Network.Throw = new HttpRequestException("No route to host.");

		var result = await host.Get<CourierSignIn>().RequestDevelopmentTokenAsync("COURIER-DEMO-07");

		Assert.Equal(SignInResult.Failure("The API could not be reached."), result);
		Assert.Single(host.Network.Requests);
	}

	[Fact]
	public void PastedToken_IsStoredUntilItsOwnExpiry()
	{
		using var host = new ClientHost();
		var expires = TestValues.Now.AddMinutes(45);
		var token = TestJwt.Create("COURIER-DEMO-07", expires, "Courier");

		var result = host.Get<CourierSignIn>().UseToken($"  {token}  ");

		Assert.True(result.Succeeded);
		Assert.Equal(new AccessToken(token, expires), host.Tokens.Token);
		Assert.Empty(host.Network.Requests);
	}

	[Theory]
	[InlineData("not-a-token")]
	[InlineData("")]
	public void PastedToken_ThatCannotWork_IsNotStored(string token)
	{
		using var host = new ClientHost();

		var result = host.Get<CourierSignIn>().UseToken(token);

		Assert.False(result.Succeeded);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public void PastedAdminOrExpiredToken_IsNotStored()
	{
		using var host = new ClientHost();
		var signIn = host.Get<CourierSignIn>();

		var admin = signIn.UseToken(TestJwt.Create(null, TestValues.Now.AddHours(1), "Admin"));
		var expired = signIn.UseToken(TestJwt.Create("COURIER-DEMO-07", TestValues.Now.AddMinutes(-1), "Courier"));

		Assert.Equal("The access token is not a Courier's token.", admin.Message);
		Assert.Equal("The access token has expired.", expired.Message);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public void Session_ReadsTheCourierFromTheStoredToken_AndSignOutDeletesIt()
	{
		using var host = new ClientHost();
		Assert.Null(host.Session.CourierId);
		Assert.True(host.Session.IsSignInRequired);

		host.Tokens.Token = TestValues.ValidToken(TestJwt.Courier("COURIER-DEMO-12"));
		Assert.Equal("COURIER-DEMO-12", host.Session.CourierId);
		Assert.False(host.Session.IsSignInRequired);

		host.Session.SignOut();
		Assert.Null(host.Tokens.Token);
		Assert.Null(host.Session.CourierId);
	}
}
