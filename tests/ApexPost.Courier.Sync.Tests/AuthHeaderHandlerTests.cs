using System.Net;
using ApexPost.Courier.Sync.Contracts;

namespace ApexPost.Courier.Sync.Tests;

// D6 and D10: the bearer header comes from the token store, a 401 clears the
// token and signals sign-in, and the token never reaches a log.
public sealed class AuthHeaderHandlerTests
{
	private static readonly ScanUploadRequest EmptyUpload = new([]);

	[Fact]
	public async Task WithAStoredToken_SendsItAsABearerHeader()
	{
		using var host = new ClientHost();
		host.Tokens.Token = TestValues.ValidToken();

		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal($"Bearer {TestValues.Token}", Assert.Single(host.Network.Requests).Authorization);
	}

	[Fact]
	public async Task WithoutAToken_SendsNoAuthorizationHeader()
	{
		using var host = new ClientHost();

		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Null(Assert.Single(host.Network.Requests).Authorization);
	}

	[Fact]
	public async Task EachRequest_ReadsTheCurrentToken()
	{
		using var host = new ClientHost();
		host.Tokens.Token = TestValues.ValidToken("first-token");
		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);
		host.Tokens.Token = TestValues.ValidToken("second-token");
		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal(
			["Bearer first-token", "Bearer second-token"],
			host.Network.Requests.Select(r => r.Authorization));
	}

	[Fact]
	public async Task Unauthorized_ClearsTheToken_AndRaisesSignInRequired()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.Unauthorized));
		host.Tokens.Token = TestValues.ValidToken();
		var raised = 0;
		host.Session.SignInRequired += (_, _) => raised++;
		Assert.False(host.Session.IsSignInRequired);

		var result = await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal(ApiErrorKind.Unauthorized, result.Error!.Kind);
		Assert.Null(host.Tokens.Token);
		Assert.Equal(1, host.Tokens.ClearCount);
		Assert.Equal(1, raised);
		Assert.True(host.Session.IsSignInRequired);
	}

	[Fact]
	public async Task Unauthorized_WithoutAToken_StillRaisesSignInRequired()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.Unauthorized));
		var raised = 0;
		host.Session.SignInRequired += (_, _) => raised++;

		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal(1, raised);
		Assert.Equal(0, host.Tokens.ClearCount);
	}

	[Fact]
	public async Task Unauthorized_ForAnOlderToken_KeepsTheNewerOne()
	{
		using var host = new ClientHost();
		var raised = 0;
		host.Session.SignInRequired += (_, _) => raised++;
		host.Tokens.Token = TestValues.ValidToken("old-token");
		var network = new StubHandler(_ =>
		{
			// The courier signs in again while the old token's request is running.
			host.Tokens.Token = TestValues.ValidToken("new-token");
			return Responses.Empty(HttpStatusCode.Unauthorized);
		});
		using var client = new HttpClient(
			new AuthHeaderHandler(host.Tokens, host.Session) { InnerHandler = network })
		{
			BaseAddress = new Uri(TestValues.BaseUrl)
		};

		await new ApexApiClient(client).UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal("new-token", host.Tokens.Token!.Value);
		Assert.Equal(0, raised);
	}

	[Theory]
	[InlineData(HttpStatusCode.OK)]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData(HttpStatusCode.RequestTimeout)]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	public async Task OtherStatuses_KeepTheToken(HttpStatusCode status)
	{
		using var host = new ClientHost(_ => Responses.Json(
			status,
			"""{"accepted":0,"duplicatesSkipped":0,"rejected":0,"rejections":[]}"""));
		host.Tokens.Token = TestValues.ValidToken();
		var raised = 0;
		host.Session.SignInRequired += (_, _) => raised++;

		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal(TestValues.Token, host.Tokens.Token!.Value);
		Assert.Equal(0, host.Tokens.ClearCount);
		Assert.Equal(0, raised);
	}

	[Fact]
	public async Task AThrowingSignInSubscriber_DoesNotFailTheCall()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.Unauthorized));
		host.Tokens.Token = TestValues.ValidToken();
		host.Session.SignInRequired += (_, _) => throw new InvalidOperationException("subscriber failed");

		var result = await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		Assert.Equal(ApiErrorKind.Unauthorized, result.Error!.Kind);
		Assert.Null(host.Tokens.Token);
	}

	[Theory]
	[InlineData(HttpStatusCode.OK)]
	[InlineData(HttpStatusCode.Unauthorized)]
	public async Task NoLogMessage_ContainsTheToken_EvenAtTraceLevel(HttpStatusCode status)
	{
		using var host = new ClientHost(_ => Responses.Json(
			status,
			"""{"accepted":0,"duplicatesSkipped":0,"rejected":0,"rejections":[]}"""));
		host.Tokens.Token = TestValues.ValidToken();

		await host.Client.UploadScansAsync("APX-00000011", EmptyUpload);

		// IHttpClientFactory logged the request and its headers, redacted.
		Assert.Contains(host.Logs.Messages, m => m.Contains("Authorization: *", StringComparison.Ordinal));
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(TestValues.Token, StringComparison.Ordinal));
	}
}
