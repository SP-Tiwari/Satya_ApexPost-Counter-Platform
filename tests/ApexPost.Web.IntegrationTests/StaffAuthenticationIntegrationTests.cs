using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApexPost.Application.Persistence;
using ApexPost.Application.Security;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using Microsoft.AspNetCore.Authentication;

namespace ApexPost.Web.IntegrationTests;

public sealed class StaffAuthenticationIntegrationTests : IAsyncLifetime
{
	private const string TestStaffId = "staff-test-01";
	private const string TestPassword = "integration-test-password";
	private const int HashIterations = 600_000;
	private string _rootDirectory = null!;
	private string _credentialFile = null!;
	private WebApplicationFactory<Program> _factory = null!;
	private readonly StaffClaimCapture _claimCapture = new();

	public Task InitializeAsync()
	{
		_rootDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-Auth-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_rootDirectory);
		_credentialFile = Path.Combine(
			_rootDirectory, "staff-credentials.json");
		_factory = CreateFactory();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		if (Directory.Exists(_rootDirectory))
			Directory.Delete(_rootDirectory, recursive: true);
	}

	[Fact]
	public async Task SignIn_WithValidCredentialsIssuesCookieAndAuthorizesManager()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var antiforgeryToken = await GetAntiforgeryTokenAsync(client);

		var response = await PostSignInAsync(
			client, antiforgeryToken, TestStaffId, TestPassword,
			"https://untrusted.example/",
			submittedRole: nameof(StaffRole.Administrator),
			submittedBranchCode: "UNTRUSTED");

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.NotNull(response.Headers.Location);
		Assert.DoesNotContain(
			"untrusted.example",
			response.Headers.Location!.OriginalString,
			StringComparison.Ordinal);
		Assert.Contains(
			response.Headers.GetValues("Set-Cookie"),
			cookie => cookie.Contains(
				".AspNetCore.Cookies=", StringComparison.Ordinal) &&
				cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
				cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) &&
				cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));

		var authorizedPage = await client.GetAsync("/Transactions");
		Assert.Equal(System.Net.HttpStatusCode.OK, authorizedPage.StatusCode);
		Assert.Equal(TestStaffId, _claimCapture.StaffId);
		Assert.Equal(nameof(StaffRole.BranchManager), _claimCapture.Role);
		Assert.Equal("AUTH01", _claimCapture.BranchCode);
	}

	[Fact]
	public async Task SignIn_WithWrongPasswordDoesNotIssueCookie()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await PostSignInAsync(
			client, token, TestStaffId, "wrong-password", null);

		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			response.StatusCode);
		Assert.DoesNotContain(
			GetSetCookies(response),
			cookie => cookie.Contains(
				".AspNetCore.Cookies=",
				StringComparison.Ordinal));
		var responseHtml = await response.Content.ReadAsStringAsync();
		Assert.Contains(
			"Staff ID or password is incorrect",
			responseHtml,
			StringComparison.Ordinal);
		Assert.DoesNotContain("wrong-password", responseHtml, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SignIn_WithInactiveStaffIsRejected()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: false);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await PostSignInAsync(
			client, token, TestStaffId, TestPassword, null);

		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			response.StatusCode);
		Assert.DoesNotContain(
			GetSetCookies(response),
			cookie => cookie.Contains(
				".AspNetCore.Cookies=",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task SignIn_UnknownStaffAndWrongPasswordHaveSameGenericFailure()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var wrongPassword = await PostSignInAsync(
			client, token, TestStaffId, "wrong-password", null);
		var unknownStaff = await PostSignInAsync(
			client, token, "unknown-staff", TestPassword, null);

		Assert.Equal(wrongPassword.StatusCode, unknownStaff.StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			wrongPassword.StatusCode);
		Assert.Contains(
			"Staff ID or password is incorrect",
			await wrongPassword.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
		Assert.Contains(
			"Staff ID or password is incorrect",
			await unknownStaff.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("unsupported-hash-version")]
	[InlineData("invalid-salt-base64")]
	[InlineData("invalid-hash-base64")]
	[InlineData("invalid-salt-length")]
	[InlineData("invalid-hash-length")]
	[InlineData("iterations-below-minimum")]
	[InlineData("iterations-above-maximum")]
	public async Task SignIn_WithInvalidStoredCredential_FailsClosed(
		string invalidCredential)
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await AddCredentialAsync(CreateInvalidCredential(invalidCredential));
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await PostSignInAsync(
			client, token, TestStaffId, TestPassword, null);

		Assert.Equal(
			System.Net.HttpStatusCode.ServiceUnavailable,
			response.StatusCode);
		Assert.DoesNotContain(
			TestPassword,
			await response.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExistingCookieIsRevokedWhenStaffIsDeactivated()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);
		var signIn = await PostSignInAsync(
			client, token, TestStaffId, TestPassword, null);
		Assert.Equal(System.Net.HttpStatusCode.Redirect, signIn.StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.OK,
			(await client.GetAsync("/Transactions")).StatusCode);

		await WriteStaffAsync(StaffRole.BranchManager, isActive: false);

		var revokedResponse = await client.GetAsync("/Transactions");
		Assert.Equal(
			System.Net.HttpStatusCode.Redirect,
			revokedResponse.StatusCode);
		Assert.StartsWith(
			"/Account/SignIn",
			revokedResponse.Headers.Location?.AbsolutePath);
	}

	[Theory]
	[InlineData("role")]
	[InlineData("branch")]
	public async Task ExistingCookieIsRevokedWhenRoleOrBranchChanges(
		string changedField)
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);
		var signIn = await PostSignInAsync(
			client, token, TestStaffId, TestPassword, null);
		Assert.Equal(System.Net.HttpStatusCode.Redirect, signIn.StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.OK,
			(await client.GetAsync("/Transactions")).StatusCode);

		var newRole = changedField == "role"
			? StaffRole.CounterClerk
			: StaffRole.BranchManager;
		var newBranch = changedField == "branch" ? "AUTH02" : "AUTH01";
		await WriteStaffAsync(
			newRole, isActive: true, branchCodeValue: newBranch);

		var response = await client.GetAsync("/Transactions");

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/Account/SignIn", response.Headers.Location?.AbsolutePath);
	}

	[Fact]
	public async Task AuthenticatedNonManagerIsForbiddenFromTransactions()
	{
		await WriteStaffAsync(StaffRole.CounterClerk, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var signIn = await PostSignInAsync(
			client,
			token,
			TestStaffId,
			TestPassword,
			null,
			submittedRole: nameof(StaffRole.BranchManager),
			submittedBranchCode: "UNTRUSTED");
		Assert.Equal(System.Net.HttpStatusCode.Redirect, signIn.StatusCode);

		var response = await client.GetAsync("/Transactions");

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(
			"/Account/AccessDenied",
			response.Headers.Location?.AbsolutePath);
		Assert.Equal(nameof(StaffRole.CounterClerk), _claimCapture.Role);
		Assert.Equal("AUTH01", _claimCapture.BranchCode);

		// Following the redirect, the access-denied page itself answers 403.
		var deniedPage = await client.GetAsync(response.Headers.Location);
		Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedPage.StatusCode);
		Assert.Contains(
			"<h1",
			await deniedPage.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task AccessDeniedPage_Returns403EvenWhenOpenedDirectly()
	{
		var client = CreateSecureClient();

		var response = await client.GetAsync("/Account/AccessDenied");

		Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal(
			"text/html",
			response.Content.Headers.ContentType?.MediaType);
	}

	[Fact]
	public async Task SignIn_ExceedingConfiguredPerIpLimitReturns429()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		await WriteCredentialAsync(TestStaffId, TestPassword);
		await _factory.DisposeAsync();
		_factory = CreateFactory(permitLimit: 2);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var first = await PostSignInAsync(
			client, token, TestStaffId, "wrong-password", null);
		var second = await PostSignInAsync(
			client, token, TestStaffId, "wrong-password", null);
		var limited = await PostSignInAsync(
			client, token, TestStaffId, "wrong-password", null);

		Assert.Equal(System.Net.HttpStatusCode.Unauthorized, first.StatusCode);
		Assert.Equal(System.Net.HttpStatusCode.Unauthorized, second.StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.TooManyRequests,
			limited.StatusCode);
	}

	[Fact]
	public async Task SignIn_WithoutAStoredCredential_IsRefused()
	{
		await WriteStaffAsync(StaffRole.BranchManager, isActive: true);
		var client = CreateSecureClient();
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await PostSignInAsync(
			client, token, TestStaffId, TestPassword, null);

		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			response.StatusCode);
		Assert.DoesNotContain(
			GetSetCookies(response),
			cookie => cookie.Contains(
				".AspNetCore.Cookies=",
				StringComparison.Ordinal));
	}

	private WebApplicationFactory<Program> CreateFactory(int permitLimit = 5) =>
		new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting(
					"Storage:Directory",
					Path.Combine(_rootDirectory, "domain-data"));
				builder.UseSetting(
					"Authentication:CredentialFile", _credentialFile);
				builder.UseSetting(
					"Authentication:SignInRateLimit:PermitLimit",
					permitLimit.ToString(
						System.Globalization.CultureInfo.InvariantCulture));
				builder.UseSetting(
					"Authentication:SignInRateLimit:WindowSeconds", "60");
				builder.ConfigureTestServices(services =>
					services.AddSingleton<IClaimsTransformation>(
						_claimCapture));
			});

	private HttpClient CreateSecureClient() =>
		_factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://localhost"),
			AllowAutoRedirect = false
		});

	private async Task WriteStaffAsync(
		StaffRole role,
		bool isActive,
		string branchCodeValue = "AUTH01")
	{
		var branchCode = new BranchCode(branchCodeValue);
		var staff = new Staff(
			TestStaffId,
			"Test Staff",
			"test.staff@example.test",
			role,
			branchCode);
		if (!isActive)
			staff.Deactivate();

		var repository = _factory.Services.GetRequiredService<IApexPostRepository>();
		await repository.SaveAsync(new ApexPostSnapshot(
			[
				new Branch(
					branchCode,
					"Authentication test branch",
					"Test",
					"09:00-17:00",
					[
						new CounterTerminal(
							$"{branchCodeValue}-COUNTER", "Counter")
					])
			],
			[staff],
			[],
			[],
			[],
			[],
			[],
			[],
			[]));
	}

	private async Task WriteCredentialAsync(string staffId, string password)
	{
		var salt = RandomNumberGenerator.GetBytes(16);
		var hash = Rfc2898DeriveBytes.Pbkdf2(
			password,
			salt,
			HashIterations,
			HashAlgorithmName.SHA256,
			32);
		await AddCredentialAsync(new StaffCredential(
			staffId,
			1,
			HashIterations,
			Convert.ToBase64String(salt),
			Convert.ToBase64String(hash)));
	}

	private async Task AddCredentialAsync(StaffCredential credential)
	{
		using var scope = _factory.Services.CreateScope();
		await scope.ServiceProvider
			.GetRequiredService<IStaffCredentialProvisioning>()
			.AddMissingAsync([credential]);
	}

	private static async Task<string> GetAntiforgeryTokenAsync(
		HttpClient client)
	{
		var response = await client.GetAsync("/Account/SignIn");
		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		var match = Regex.Match(
			html,
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
		Assert.True(match.Success, "The sign-in form should contain an antiforgery token.");
		return match.Groups[1].Value;
	}

	private static Task<HttpResponseMessage> PostSignInAsync(
		HttpClient client,
		string antiforgeryToken,
		string staffId,
		string password,
		string? returnUrl,
		string? submittedRole = null,
		string? submittedBranchCode = null) =>
		client.PostAsync(
			"/Account/SignIn",
			new FormUrlEncodedContent(
			[
				new KeyValuePair<string, string>(
					"__RequestVerificationToken", antiforgeryToken),
				new KeyValuePair<string, string>("StaffId", staffId),
				new KeyValuePair<string, string>("Password", password),
				new KeyValuePair<string, string>(
					"ReturnUrl", returnUrl ?? string.Empty),
				new KeyValuePair<string, string>(
					"Role", submittedRole ?? string.Empty),
				new KeyValuePair<string, string>(
					"BranchCode", submittedBranchCode ?? string.Empty)
			]));

	private static IEnumerable<string> GetSetCookies(
		HttpResponseMessage response) =>
		response.Headers.TryGetValues("Set-Cookie", out var values)
			? values
			: [];

	private static StaffCredential CreateInvalidCredential(string scenario)
	{
		var salt = Convert.ToBase64String(new byte[16]);
		var hash = Convert.ToBase64String(new byte[32]);
		var iterations = StaffPasswordHashVerifier.MinimumIterations;

		return scenario switch
		{
			"unsupported-hash-version" =>
				new StaffCredential(TestStaffId, 2, iterations, salt, hash),
			"invalid-salt-base64" =>
				new StaffCredential(TestStaffId, 1, iterations, "not-base64!", hash),
			"invalid-hash-base64" =>
				new StaffCredential(TestStaffId, 1, iterations, salt, "not-base64!"),
			"invalid-salt-length" => new StaffCredential(
				TestStaffId, 1, iterations, Convert.ToBase64String(new byte[15]), hash),
			"invalid-hash-length" => new StaffCredential(
				TestStaffId, 1, iterations, salt, Convert.ToBase64String(new byte[31])),
			"iterations-below-minimum" =>
				new StaffCredential(TestStaffId, 1, iterations - 1, salt, hash),
			"iterations-above-maximum" =>
				new StaffCredential(TestStaffId, 1, 2_000_001, salt, hash),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario))
		};
	}

	private sealed class StaffClaimCapture : IClaimsTransformation
	{
		public string? StaffId { get; private set; }
		public string? Role { get; private set; }
		public string? BranchCode { get; private set; }

		public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
		{
			StaffId = principal.FindFirst("StaffId")?.Value;
			Role = principal.FindFirst("Role")?.Value;
			BranchCode = principal.FindFirst("BranchCode")?.Value;
			return Task.FromResult(principal);
		}
	}
}
