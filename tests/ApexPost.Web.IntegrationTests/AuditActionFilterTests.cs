using System.Security.Claims;
using System.Text.Encodings.Web;
using ApexPost.Application.Auditing;
using ApexPost.Web.Filters;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApexPost.Web.IntegrationTests;

public sealed class AuditActionFilterTests : IAsyncLifetime
{
	private string _storageDirectory = null!;
	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_storageDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-Web-Audit-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_storageDirectory);
		_factory = new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting(
					"Storage:Directory", _storageDirectory);
				builder.UseSetting(
					"Authentication:CredentialFile",
					Path.Combine(_storageDirectory, "credentials.json"));
				builder.ConfigureTestServices(services =>
				{
					services.AddControllersWithViews()
						.AddApplicationPart(typeof(AuditProbeController).Assembly);
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme =
							AuditTestAuthenticationHandler.TestScheme;
						options.DefaultChallengeScheme =
							AuditTestAuthenticationHandler.TestScheme;
						options.DefaultForbidScheme =
							AuditTestAuthenticationHandler.TestScheme;
					}).AddScheme<AuthenticationSchemeOptions,
						AuditTestAuthenticationHandler>(
						AuditTestAuthenticationHandler.TestScheme,
						_ => { });
				});
			});
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		if (Directory.Exists(_storageDirectory))
			Directory.Delete(_storageDirectory, recursive: true);
	}

	[Fact]
	public async Task SuccessfulAnonymousAction_PopulatesRequiredAuditFields()
	{
		const string correlationId = "audit-anonymous-correlation";
		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add(
			"X-Correlation-ID", correlationId);

		var response = await client.GetAsync("/__test/audit/anonymous");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal("AuditProbe", record.Controller);
		Assert.Equal("Anonymous", record.Action);
		Assert.Equal("Executed AuditProbe.Anonymous", record.Description);
		Assert.Equal("Succeeded", record.Outcome);
		Assert.Equal(200, record.StatusCode);
		Assert.True(record.DurationMilliseconds >= 0);
		Assert.Equal("AuditProbe", record.RouteValues["controller"]);
		Assert.Equal("Anonymous", record.RouteValues["action"]);
		Assert.Null(record.StaffId);
		Assert.Null(record.StaffRole);
		Assert.Null(record.BranchCode);
		Assert.Equal(correlationId, record.CorrelationId);
		Assert.Equal(TimeSpan.Zero, record.TimestampUtc.Offset);
	}

	[Fact]
	public async Task AuthenticatedAction_RecordsTrustedStaffRoleAndBranchClaims()
	{
		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add(
			"X-Audit-StaffId", "verified-staff-007");
		client.DefaultRequestHeaders.Add(
			"X-Audit-Role", "BranchManager");
		client.DefaultRequestHeaders.Add(
			"X-Audit-BranchCode", "BR001");

		var response = await client.GetAsync("/__test/audit/authenticated");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal("verified-staff-007", record.StaffId);
		Assert.Equal("BranchManager", record.StaffRole);
		Assert.Equal("BR001", record.BranchCode);
	}

	[Fact]
	public async Task AuthenticatedAction_WithoutOptionalClaims_HandlesMissingValuesSafely()
	{
		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add(
			"X-Audit-StaffId", "verified-staff-007");

		var response = await client.GetAsync("/__test/audit/authenticated");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal("verified-staff-007", record.StaffId);
		Assert.Null(record.StaffRole);
		Assert.Null(record.BranchCode);
	}

	[Fact]
	public async Task RouteValues_AreSanitizedAndSensitiveValuesAreExcluded()
	{
		var client = _factory.CreateClient();

		var response = await client.GetAsync("/__test/audit/route/ABC123/SECRET-KEY/BR002/TOKEN123?password=hidden");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal("ABC123", record.RouteValues["id"]);
		Assert.Equal("BR002", record.RouteValues["branchCode"]);
		Assert.False(record.RouteValues.ContainsKey("apiKey"));
		Assert.False(record.RouteValues.ContainsKey("token"));
		Assert.False(record.RouteValues.ContainsKey("password"));
		Assert.DoesNotContain("SECRET-KEY", await TestSqlServer.ReadAuditTextAsync(), StringComparison.Ordinal);
		Assert.DoesNotContain("TOKEN123", await TestSqlServer.ReadAuditTextAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task FailedAction_CapturesFailureStatusAndDuration_WithoutPersistingExceptionDetails()
	{
		var client = _factory.CreateClient();

		var response = await client.GetAsync("/__test/audit/failure");

		Assert.Equal(
			System.Net.HttpStatusCode.InternalServerError,
			response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal("Failure", record.Action);
		Assert.Equal("Failed", record.Outcome);
		Assert.Equal(500, record.StatusCode);
		Assert.True(record.DurationMilliseconds >= 0);
		Assert.DoesNotContain(
			"private action failure",
			await TestSqlServer.ReadAuditTextAsync(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnauditedAction_WritesNoRecord()
	{
		var client = _factory.CreateClient();

		var response = await client.GetAsync("/__test/audit/page");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		Assert.Empty(await TestSqlServer.ReadAuditRecordsAsync());
	}

	[Fact]
	public async Task Redirect_IsRecordedWithTheStatusActuallySent()
	{
		var client = _factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		var response = await client.GetAsync("/__test/audit/redirect");

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal(302, record.StatusCode);
		Assert.Equal("Succeeded", record.Outcome);
	}

	[Fact]
	public async Task ClientError_IsRecordedAsRejected()
	{
		var client = _factory.CreateClient();

		var response = await client.GetAsync("/__test/audit/conflict");

		Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal(409, record.StatusCode);
		Assert.Equal("Rejected", record.Outcome);
	}

	[Fact]
	public async Task AddAuditValue_KeepsOnlyAllowListedKeys()
	{
		var client = _factory.CreateClient();

		var response = await client.GetAsync("/__test/audit/add-value");

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal(
			"8d1f4c1e-0000-4000-8000-000000000001",
			record.RouteValues["transactionId"]);
		Assert.False(record.RouteValues.ContainsKey("documentNumber"));
		Assert.DoesNotContain(
			"P1234567",
			await TestSqlServer.ReadAuditTextAsync(),
			StringComparison.Ordinal);
	}

	private static Task<AuditRecord[]> ReadAuditRecordsAsync() =>
		TestSqlServer.ReadAuditRecordsAsync();
}

// Audited probes, plus one unaudited action to show the filter is opt-in.
[ApiController]
[Route("__test/audit")]
public sealed class AuditProbeController : ControllerBase
{
	[AllowAnonymous]
	[HttpGet("anonymous")]
	[Audited]
	public IActionResult Anonymous() => Ok();

	[Authorize]
	[HttpGet("authenticated")]
	[Audited]
	public IActionResult Authenticated() => Ok();

	[AllowAnonymous]
	[HttpGet("route/{id}/{apiKey}/{branchCode}/{token}")]
	[Audited]
	public IActionResult RouteValues(
		string id,
		string apiKey,
		string branchCode,
		string token) => Ok();

	[AllowAnonymous]
	[HttpGet("failure")]
	[Audited]
	public IActionResult Failure() =>
		throw new InvalidOperationException("private action failure");

	[AllowAnonymous]
	[HttpGet("redirect")]
	[Audited]
	public IActionResult RedirectAway() => Redirect("/elsewhere");

	[AllowAnonymous]
	[HttpGet("conflict")]
	[Audited]
	public IActionResult Rejected() => Conflict();

	// Adds a form-style identifier, as the sale action does, and tries a
	// key that is not allow-listed.
	[AllowAnonymous]
	[HttpGet("add-value")]
	[Audited]
	public IActionResult AddValue()
	{
		AuditActionFilter.AddAuditValue(
			HttpContext, "transactionId", "8d1f4c1e-0000-4000-8000-000000000001");
		AuditActionFilter.AddAuditValue(
			HttpContext, "documentNumber", "P1234567");
		return Ok();
	}

	[AllowAnonymous]
	[HttpGet("page")]
	public IActionResult PageView()
	{
		// Outside an audited action this is ignored.
		AuditActionFilter.AddAuditValue(HttpContext, "transactionId", "ignored");
		return Ok();
	}
}

public sealed class AuditTestAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(
		options, logger, encoder)
{
	public const string TestScheme = "AuditTest";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var staffId = Request.Headers["X-Audit-StaffId"].ToString();
		var role = Request.Headers["X-Audit-Role"].ToString();
		var branchCode = Request.Headers["X-Audit-BranchCode"].ToString();
		if (string.IsNullOrWhiteSpace(staffId))
			return Task.FromResult(AuthenticateResult.NoResult());

		var claims = new List<Claim>
		{
			new("StaffId", staffId)
		};

		if (!string.IsNullOrWhiteSpace(role))
			claims.Add(new Claim("Role", role));

		if (!string.IsNullOrWhiteSpace(branchCode))
			claims.Add(new Claim("BranchCode", branchCode));

		var identity = new ClaimsIdentity(claims, TestScheme);
		var principal = new ClaimsPrincipal(identity);
		var ticket = new AuthenticationTicket(principal, TestScheme);
		return Task.FromResult(AuthenticateResult.Success(ticket));
	}
}
