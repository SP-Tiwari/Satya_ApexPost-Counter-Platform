using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ApexPost.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace ApexPost.Api.IntegrationTests;

// Week 4 Part D, D1-D3 and D5: bearer authentication, role authorization, the
// courier ownership rule, the partner API key, HTTPS and HSTS, and the
// security schemes in the version 2 document. Each test chooses its own
// token, or none, instead of the factory's all-roles default.
public sealed class SecurityTests : IDisposable
{
	private const string TrackingNumber = "APX-70000001";
	private const string OtherCourierId = "COURIER-DEMO-99";
	private const string DevToken = "/dev/token";

	private readonly ApiFactory _factory = new();

	public void Dispose() => _factory.Dispose();

	private static string V2(string trackingNumber) => $"/api/v2/shipments/{trackingNumber}";

	private static string Partner(string trackingNumber) => $"/api/v2/partner/shipments/{trackingNumber}";

	private HttpClient ClientWith(string? token) => _factory.CreateClient().WithToken(token);

	private async Task<HttpClient> WithShipmentAsync(string? token)
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber);
		return ClientWith(token);
	}

	private static Dictionary<string, object?> OwnScan(string courierId = TestTokens.CourierId)
	{
		var scan = Api.Scan(Api.NewScanId(), TrackingNumber, DateTimeOffset.UtcNow.AddMinutes(-10));
		scan["courierId"] = courierId;
		return scan;
	}

	// D1: authentication.

	[Fact]
	public async Task Get_WithoutAToken_Returns401ProblemDetails_WithABearerChallenge()
	{
		var client = await WithShipmentAsync(token: null);

		var response = await client.GetAsync(V2(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
		Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
	}

	[Theory]
	[InlineData("expired")]
	[InlineData("not yet valid")]
	[InlineData("wrong issuer")]
	[InlineData("wrong audience")]
	[InlineData("wrong signing key")]
	[InlineData("unsigned")]
	[InlineData("malformed")]
	public async Task Get_WithAnInvalidToken_Returns401ProblemDetails(string problem)
	{
		var now = DateTime.UtcNow;
		string[] roles = [ApexPostClaims.CourierRole];
		var token = problem switch
		{
			// Past the one-minute clock skew.
			"expired" => TestTokens.Create(TestTokens.CourierId, roles, now.AddHours(-2), now.AddMinutes(-5)),
			"not yet valid" => TestTokens.Create(TestTokens.CourierId, roles, now.AddMinutes(10), now.AddHours(1)),
			"wrong issuer" => TestTokens.Create(TestTokens.CourierId, roles, now.AddMinutes(-5), now.AddHours(1), issuer: "someone-else"),
			"wrong audience" => TestTokens.Create(TestTokens.CourierId, roles, now.AddMinutes(-5), now.AddHours(1), audience: "https://other-api.test"),
			"wrong signing key" => TestTokens.Create(
				TestTokens.CourierId, roles, now.AddMinutes(-5), now.AddHours(1), signingKey: RandomNumberGenerator.GetBytes(32)),
			"unsigned" => Unsigned(TestTokens.Courier()),
			_ => "not-a-jwt"
		};
		var client = await WithShipmentAsync(token);

		var response = await client.GetAsync(V2(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
	}

	// The same header and payload with the signature removed and alg "none".
	private static string Unsigned(string token)
	{
		var parts = token.Split('.');
		var header = Convert.ToBase64String("""{"alg":"none","typ":"JWT"}"""u8.ToArray())
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return $"{header}.{parts[1]}.";
	}

	[Fact]
	public void Startup_WithoutASigningKeyForTheIssuer_Fails()
	{
		using var factory = new ApiFactory(settings: new Dictionary<string, string?>
		{
			["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "someone-else"
		});

		var exception = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

		Assert.Contains("SigningKeys", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DevelopmentToken_ForACourier_UploadsTheirOwnScans()
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber);
		var issued = await ClientWith(null).PostAsJsonAsync(
			DevToken, new { courierId = TestTokens.CourierId, role = "Courier" });
		Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
		var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
		Assert.True(body.GetProperty("expiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);

		var response = await ClientWith(body.GetProperty("accessToken").GetString())
			.UploadAsync(TrackingNumber, OwnScan());

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task DevelopmentToken_ForAnAdmin_DeletesAShipment()
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber);
		var issued = await ClientWith(null).PostAsJsonAsync(DevToken, new { role = "Admin" });
		var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

		var response = await ClientWith(token).DeleteAsync(V2(TrackingNumber));

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	[Theory]
	[InlineData("COURIER-DEMO-07", "Manager", "role")]
	[InlineData(null, null, "role")]
	[InlineData(null, "Courier", "courierId")]
	[InlineData(" COURIER-DEMO-07", "Courier", "courierId")]
	public async Task DevelopmentToken_WithAnInvalidRequest_Returns400(string? courierId, string? role, string field)
	{
		var response = await ClientWith(null).PostAsJsonAsync(DevToken, new { courierId, role });

		await response.AssertValidationErrorAsync(field);
	}

	[Fact]
	public async Task DevelopmentToken_OutsideDevelopment_DoesNotExist()
	{
		using var factory = new ApiFactory("Production");
		var request = new { courierId = TestTokens.CourierId, role = "Courier" };

		// No endpoint, so the fallback policy answers an anonymous caller with
		// a 401 and a signed-in one with a 404; no token is ever issued.
		var anonymous = await factory.CreateClient().WithToken(null).PostAsJsonAsync(DevToken, request);
		var signedIn = await factory.CreateClient().PostAsJsonAsync(DevToken, request);

		await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized);
		Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);
	}

	// D2: role authorization.

	[Theory]
	[InlineData("Courier")]
	[InlineData("Admin")]
	public async Task Get_WithAnyRole_Returns200(string role)
	{
		var client = await WithShipmentAsync(role == "Courier" ? TestTokens.Courier() : TestTokens.Admin());

		var response = await client.GetAsync(V2(TrackingNumber));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task ChangeStatus_WithoutTheCourierRole_Returns403ProblemDetails()
	{
		var client = await WithShipmentAsync(TestTokens.Admin());

		var response = await client.PatchAsJsonAsync($"{V2(TrackingNumber)}/status", new { status = "PickedUp" });

		await response.AssertProblemAsync(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task ChangeStatus_AsACourier_Returns200()
	{
		var client = await WithShipmentAsync(TestTokens.Courier());

		var response = await client.PatchAsJsonAsync($"{V2(TrackingNumber)}/status", new { status = "PickedUp" });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Delete_WithoutTheAdminRole_Returns403ProblemDetails_AndKeepsTheShipment()
	{
		var client = await WithShipmentAsync(TestTokens.Courier());

		var response = await client.DeleteAsync(V2(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.Forbidden);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(V2(TrackingNumber))).StatusCode);
	}

	[Fact]
	public async Task Delete_AsAnAdmin_Returns204()
	{
		var client = await WithShipmentAsync(TestTokens.Admin());

		var response = await client.DeleteAsync(V2(TrackingNumber));

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task Upload_WithoutAToken_Returns401ProblemDetails(string version)
	{
		var client = await WithShipmentAsync(token: null);

		var response = await client.PostAsJsonAsync(
			$"/api/{version}/shipments/{TrackingNumber}/scans", new { scans = new[] { OwnScan() } });

		await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Upload_WithoutTheCourierRole_Returns403ProblemDetails()
	{
		var client = await WithShipmentAsync(TestTokens.Create(TestTokens.CourierId, ApexPostClaims.AdminRole));

		var response = await client.UploadAsync(TrackingNumber, OwnScan());

		await response.AssertProblemAsync(HttpStatusCode.Forbidden);
	}

	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task Upload_AsTheCourierOfEveryScan_Returns200(string version)
	{
		var client = await WithShipmentAsync(TestTokens.Courier());

		var response = await client.PostAsJsonAsync(
			$"/api/{version}/shipments/{TrackingNumber}/scans", new { scans = new[] { OwnScan(), OwnScan() } });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var counts = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(2, counts.GetProperty("accepted").GetInt32());
	}

	// D2: one scan under another courierId refuses the whole batch.
	[Fact]
	public async Task Upload_WithAScanOfAnotherCourier_Returns403_AndStoresNoScan()
	{
		var client = await WithShipmentAsync(TestTokens.Courier());

		var response = await client.UploadAsync(TrackingNumber, OwnScan(), OwnScan(OtherCourierId));

		var problem = await response.AssertProblemAsync(HttpStatusCode.Forbidden);
		Assert.Contains(TestTokens.CourierId, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
		var history = await client.GetFromJsonAsync<JsonElement>(Api.Scans(TrackingNumber));
		Assert.Equal(0, history.GetArrayLength());
	}

	[Fact]
	public async Task Upload_WithACourierTokenWithoutACourierId_Returns403()
	{
		var client = await WithShipmentAsync(TestTokens.Create(null, ApexPostClaims.CourierRole));

		var response = await client.UploadAsync(TrackingNumber, OwnScan());

		await response.AssertProblemAsync(HttpStatusCode.Forbidden);
	}

	// Validation runs first: an invalid batch is a 400 whoever sends it.
	[Fact]
	public async Task Upload_AnInvalidBatch_FromAnotherCourier_IsStillA400()
	{
		var client = await WithShipmentAsync(TestTokens.Courier());
		var scan = OwnScan(OtherCourierId);
		scan["location"] = null;

		var response = await client.UploadAsync(TrackingNumber, scan);

		await response.AssertValidationErrorAsync("scans[0].Location");
	}

	// The scan type is read as its number or its name; responses keep the number.
	[Theory]
	[InlineData("1")]
	[InlineData("\"InTransit\"")]
	public async Task Upload_AcceptsTheScanTypeAsANumberOrAName(string scanType)
	{
		var client = await WithShipmentAsync(TestTokens.Courier());
		var scan = OwnScan();
		scan["scanType"] = JsonDocument.Parse(scanType).RootElement;

		var response = await client.UploadAsync(TrackingNumber, scan);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var history = await client.GetFromJsonAsync<JsonElement>(Api.Scans(TrackingNumber));
		Assert.Equal(JsonValueKind.Number, history[0].GetProperty("scanType").ValueKind);
		Assert.Equal(1, history[0].GetProperty("scanType").GetInt32());
	}

	[Theory]
	[InlineData("\"Teleported\"")]
	[InlineData("9")]
	public async Task Upload_AnUnknownScanType_Returns400(string scanType)
	{
		var client = await WithShipmentAsync(TestTokens.Courier());
		var scan = OwnScan();
		scan["scanType"] = JsonDocument.Parse(scanType).RootElement;

		var response = await client.UploadAsync(TrackingNumber, scan);

		await response.AssertProblemAsync(HttpStatusCode.BadRequest);
	}

	// D3: the partner API key.

	private HttpClient PartnerClient(string? apiKey, string? token = null)
	{
		var client = ClientWith(token);
		if (apiKey is not null)
			client.DefaultRequestHeaders.Add(PartnerApiKeyMiddleware.HeaderName, apiKey);
		return client;
	}

	[Fact]
	public async Task Partner_WithAValidKey_Returns200_WithoutTheRecipientsPersonalDetails()
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber, city: "Nagpur");

		var response = await PartnerClient(TestTokens.PartnerApiKey).GetAsync(Partner(TrackingNumber));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(
			["destinationCity", "latestScan", "status", "trackingNumber"],
			body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
		Assert.Equal("Created", body.GetProperty("status").GetString());
		Assert.Equal("Nagpur", body.GetProperty("destinationCity").GetString());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not-a-partner-key")]
	public async Task Partner_WithAMissingOrInvalidKey_Returns401ProblemDetails(string? apiKey)
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber);

		var response = await PartnerClient(apiKey).GetAsync(Partner(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Partner_WithABearerTokenInsteadOfAKey_Returns401()
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber);

		var response = await PartnerClient(apiKey: null, TestTokens.CourierAndAdmin).GetAsync(Partner(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task PartnerKey_DoesNotReplaceTheBearerTokenOnOtherRoutes()
	{
		await _factory.CreateClient().CreateAsync(TrackingNumber);

		var response = await PartnerClient(TestTokens.PartnerApiKey).GetAsync(V2(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Partner_ForAnUnknownTrackingNumber_Returns404()
	{
		var response = await PartnerClient(TestTokens.PartnerApiKey).GetAsync(Partner("APX-79999999"));

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Partner_InVersion1_DoesNotExist()
	{
		// Signed in as well, so the fallback policy's 401 does not hide the 404.
		var response = await PartnerClient(TestTokens.PartnerApiKey, TestTokens.Courier())
			.GetAsync($"/api/v1/partner/shipments/{TrackingNumber}");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Partner_WithNoKeyConfigured_Returns500ProblemDetails()
	{
		using var factory = new ApiFactory(settings: new Dictionary<string, string?>
		{
			[$"PartnerApiKeys:{TestTokens.PartnerName}"] = ""
		});
		var client = factory.CreateClient().WithToken(null);
		client.DefaultRequestHeaders.Add(PartnerApiKeyMiddleware.HeaderName, TestTokens.PartnerApiKey);

		var response = await client.GetAsync(Partner(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.InternalServerError);
	}

	// D5: transport security.

	[Theory]
	[InlineData("Development")]
	[InlineData("Production")]
	public async Task PlainHttp_IsRedirectedToHttps_InEveryEnvironment(string environment)
	{
		using var factory = new ApiFactory(environment, settings: new Dictionary<string, string?> { ["https_port"] = "443" });
		var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		var response = await client.GetAsync(V2(TrackingNumber));

		Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
		Assert.Equal($"https://localhost{V2(TrackingNumber)}", response.Headers.Location?.ToString());
	}

	[Theory]
	[InlineData("Production", true)]
	[InlineData("Development", false)]
	public async Task Hsts_IsSentOutsideDevelopmentOnly(string environment, bool sent)
	{
		using var factory = new ApiFactory(environment);
		var client = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://api.apexpost.test")
		});

		var response = await client.GetAsync(Partner(TrackingNumber));

		Assert.Equal(sent, response.Headers.TryGetValues("Strict-Transport-Security", out var values));
		if (sent)
			Assert.Equal("max-age=2592000", Assert.Single(values!));
	}

	// D5: the security schemes in the OpenAPI documents. Every shipment and
	// scan operation needs a bearer token, and the partner operation its key.

	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task OpenApiDocument_DeclaresTheSchemes_AndEachOperationRequiresOne(string version)
	{
		var document = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/openapi/{version}.json");

		var schemes = document.GetProperty("components").GetProperty("securitySchemes");
		Assert.Equal("http", schemes.GetProperty("Bearer").GetProperty("type").GetString());
		Assert.Equal("bearer", schemes.GetProperty("Bearer").GetProperty("scheme").GetString());
		Assert.Equal("JWT", schemes.GetProperty("Bearer").GetProperty("bearerFormat").GetString());
		if (version == "v2")
		{
			Assert.Equal("apiKey", schemes.GetProperty("PartnerApiKey").GetProperty("type").GetString());
			Assert.Equal("header", schemes.GetProperty("PartnerApiKey").GetProperty("in").GetString());
			Assert.Equal("X-Api-Key", schemes.GetProperty("PartnerApiKey").GetProperty("name").GetString());
		}
		else
		{
			// A document declares only the schemes its operations use.
			Assert.Equal(["Bearer"], schemes.EnumerateObject().Select(scheme => scheme.Name));
		}

		var operations = document.GetProperty("paths").EnumerateObject()
			.SelectMany(path => path.Value.EnumerateObject().Select(operation => (Path: path.Name, Operation: operation.Value)))
			.ToArray();
		Assert.All(operations, item => Assert.Equal(
			item.Path.StartsWith("/api/v2/partner/", StringComparison.Ordinal) ? "PartnerApiKey" : "Bearer",
			Assert.Single(Assert.Single(item.Operation.GetProperty("security").EnumerateArray()).EnumerateObject()).Name));
	}

	// The approved authorization matrix, in both versions: no token is a 401,
	// a token without the required role a 403, both as Problem Details, and
	// the allowed role succeeds. "alias" is the unversioned Part B path,
	// /api/shipments, which is version 1 and must apply the same rules.
	private static readonly Dictionary<string, string> RequiredRole = new()
	{
		["list"] = "any",
		["get"] = "any",
		["create"] = ApexPostClaims.AdminRole,
		["replace"] = ApexPostClaims.AdminRole,
		["status"] = ApexPostClaims.CourierRole,
		["delete"] = ApexPostClaims.AdminRole,
		["upload"] = ApexPostClaims.CourierRole,
		["history"] = "any"
	};

	public static TheoryData<string, string, string> AuthorizationCases
	{
		get
		{
			var data = new TheoryData<string, string, string>();
			foreach (var version in new[] { "v1", "v2", "alias" })
			{
				foreach (var operation in RequiredRole.Keys)
				{
					foreach (var caller in new[] { "none", ApexPostClaims.CourierRole, ApexPostClaims.AdminRole })
						data.Add(version, operation, caller);
				}
			}
			return data;
		}
	}

	[Theory]
	[MemberData(nameof(AuthorizationCases))]
	public async Task AuthorizationMatrix(string version, string operation, string caller)
	{
		var token = caller switch
		{
			ApexPostClaims.CourierRole => TestTokens.Courier(),
			ApexPostClaims.AdminRole => TestTokens.Admin(),
			_ => null
		};
		var client = await WithShipmentAsync(token);
		var shipments = version == "alias" ? "/api/shipments" : $"/api/{version}/shipments";
		var shipment = $"{shipments}/{TrackingNumber}";
		var details = Api.NewShipment(TrackingNumber);
		details.Remove("trackingNumber");

		var (response, success) = operation switch
		{
			"list" => (await client.GetAsync(shipments), HttpStatusCode.OK),
			"get" => (await client.GetAsync(shipment), HttpStatusCode.OK),
			"create" => (await client.PostAsJsonAsync(shipments, Api.NewShipment("APX-70000002")), HttpStatusCode.Created),
			"replace" => (await client.PutAsJsonAsync(shipment, details), HttpStatusCode.OK),
			"status" => (await client.PatchAsJsonAsync($"{shipment}/status",
				version != "v2" ? new { status = (object)1 } : new { status = (object)"PickedUp" }), HttpStatusCode.OK),
			"delete" => (await client.DeleteAsync(shipment), HttpStatusCode.NoContent),
			"upload" => (await client.PostAsJsonAsync($"{shipment}/scans", new { scans = new[] { OwnScan() } }), HttpStatusCode.OK),
			_ => (await client.GetAsync($"{shipment}/scans"), HttpStatusCode.OK)
		};

		var required = RequiredRole[operation];
		if (caller == "none")
			await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
		else if (required != "any" && required != caller)
			await response.AssertProblemAsync(HttpStatusCode.Forbidden);
		else
			Assert.Equal(success, response.StatusCode);
	}

	// The fallback policy: a request that names no rule of its own, including
	// one that matches no endpoint, needs a signed-in user. So an anonymous
	// caller cannot even learn which routes exist.
	[Theory]
	[InlineData("/api/v2/unknown")]
	[InlineData("/api/v3/shipments")]
	[InlineData("/api/v2/shipments/not-a-tracking-number")]
	public async Task Fallback_AnonymousRequestToNoEndpoint_Returns401_AndASignedInOne404(string path)
	{
		var anonymous = await ClientWith(null).GetAsync(path);
		var signedIn = await ClientWith(TestTokens.Courier()).GetAsync(path);

		await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized);
		await signedIn.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	// The Development documentation stays anonymous, so Swagger UI loads and
	// its Authorize button can be used.
	[Theory]
	[InlineData("/openapi/v1.json")]
	[InlineData("/openapi/v2.json")]
	[InlineData("/swagger/index.html")]
	public async Task DevelopmentDocumentation_NeedsNoToken(string path)
	{
		var response = await ClientWith(null).GetAsync(path);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	// The partner route is exempt from the bearer fallback, in every
	// environment, and still needs its key.
	[Theory]
	[InlineData("Development")]
	[InlineData("Production")]
	public async Task Partner_WithAValidKeyAndNoToken_Returns200_InEveryEnvironment(string environment)
	{
		using var factory = new ApiFactory(environment);
		await factory.CreateClient().CreateAsync(TrackingNumber);
		var client = factory.CreateClient().WithToken(null);
		client.DefaultRequestHeaders.Add(PartnerApiKeyMiddleware.HeaderName, TestTokens.PartnerApiKey);

		var response = await client.GetAsync(Partner(TrackingNumber));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}
}
