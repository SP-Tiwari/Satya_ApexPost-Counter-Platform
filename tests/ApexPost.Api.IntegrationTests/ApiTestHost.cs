using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ApexPost.Api.Models;
using ApexPost.Api.Security;
using ApexPost.Courier.Sync;
using ApexPost.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Tests share one database and reset it, so they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ApexPost.Api.IntegrationTests;

// The disposable ApexPost_Test_* database for this run, built by the real
// migrations and dropped when the test process ends. It is never ApexPostDb.
internal static class ApiTestDatabase
{
	private static readonly Lazy<DisposableSqlDatabase> Instance = new(() =>
	{
		var database = DisposableSqlDatabase.Create();
		AppDomain.CurrentDomain.ProcessExit += (_, _) => database.Dispose();
		return database;
	});

	public static DisposableSqlDatabase Database => Instance.Value;
}

// A clock the test sets, for the strict future-time boundary.
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
	public override DateTimeOffset GetUtcNow() => now;
}

// The API host on the disposable database, starting empty. User Secrets are
// removed, and UseSetting replaces the connection string before Program reads
// it, so a developer's own database is never used. The bearer scheme validates
// TestTokens (a key made for this run), and one partner key is configured;
// settings can replace either. Every client it creates is signed in with
// TestTokens.CourierAndAdmin, so the behaviour tests exercise the endpoints as
// a caller with every role. SecurityTests sends no token, or a narrower one.
internal sealed class ApiFactory(
	string environment = "Development",
	Action<IServiceCollection>? configureServices = null,
	IReadOnlyDictionary<string, string?>? settings = null)
	: WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		ApiTestDatabase.Database.Reset();
		builder.UseEnvironment(environment);
		builder.UseSetting("ConnectionStrings:ApexPostDb", ApiTestDatabase.Database.ConnectionString);
		builder.ConfigureAppConfiguration((_, configuration) =>
		{
			foreach (var source in configuration.Sources
				.OfType<JsonConfigurationSource>()
				.Where(source => string.Equals(source.Path, "secrets.json", StringComparison.OrdinalIgnoreCase))
				.ToArray())
			{
				configuration.Sources.Remove(source);
			}

			// Added last, so it replaces appsettings.Development.json.
			var values = new Dictionary<string, string?>(TestTokens.Settings);
			values[$"PartnerApiKeys:{TestTokens.PartnerName}"] = TestTokens.PartnerApiKey;
			foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
				values[key] = value;
			configuration.AddInMemoryCollection(values);
		});
		if (configureServices is not null)
			builder.ConfigureTestServices(configureServices);
	}

	protected override void ConfigureClient(HttpClient client)
	{
		base.ConfigureClient(client);
		client.WithToken(TestTokens.CourierAndAdmin);
	}

	public static ApiFactory WithClock(DateTimeOffset now) =>
		new(configureServices: services =>
			services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FixedTimeProvider(now))));
}

// Access tokens for the API under test, signed with a key generated for this
// test run, so no key is stored anywhere. The claims are the ones dotnet
// user-jwts and /dev/token issue: sub, role (one or more) and courierId.
internal static class TestTokens
{
	public const string Issuer = "apexpost-tests";
	public const string Audience = "https://api.apexpost.test";
	public const string CourierId = "COURIER-DEMO-07";
	public const string PartnerName = "TestPartner";

	private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);

	public static readonly string PartnerApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

	// Valid from long before any test clock (ScanUploadTests fixes the clock
	// in 2026) until an hour from now.
	private static readonly DateTime ValidFrom = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	public static IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
	{
		["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
		["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
		["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
		["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(SigningKey)
	};

	public static string CourierAndAdmin { get; } = Create(CourierId, ApexPostClaims.CourierRole, ApexPostClaims.AdminRole);

	public static string Courier() => Create(CourierId,ApexPostClaims.CourierRole);

	// The courier client's token store (ApexPost.Courier.Sync), holding a valid
	// Courier token for the whole test.
	public static ITokenStore CourierTokenStore() =>
		new FixedTokenStore(new AccessToken(Courier(), DateTimeOffset.UtcNow.AddHours(1)));

	public static string Admin() => Create(null, ApexPostClaims.AdminRole);

	public static string Create(string? courierId, params string[] roles) =>
		Create(courierId, roles, ValidFrom, DateTime.UtcNow.AddHours(1));

	public static string Create(
		string? courierId,
		string[] roles,
		DateTime notBefore,
		DateTime expires,
		string issuer = Issuer,
		string audience = Audience,
		byte[]? signingKey = null)
	{
		var claims = new List<Claim> { new(ApexPostClaims.Subject, courierId ?? "admin") };
		claims.AddRange(roles.Select(role => new Claim(ApexPostClaims.Role, role)));
		if (courierId is not null)
			claims.Add(new Claim(ApexPostClaims.CourierId, courierId));

		return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = issuer,
			Audience = audience,
			Subject = new ClaimsIdentity(claims),
			IssuedAt = notBefore,
			NotBefore = notBefore,
			Expires = expires,
			SigningCredentials = new SigningCredentials(
				new SymmetricSecurityKey(signingKey ?? SigningKey), SecurityAlgorithms.HmacSha256)
		});
	}

	// Sends the token on every request, or no token when it is null.
	public static HttpClient WithToken(this HttpClient client, string? token)
	{
		client.DefaultRequestHeaders.Authorization =
			token is null ? null : new AuthenticationHeaderValue("Bearer", token);
		return client;
	}

	private sealed class FixedTokenStore(AccessToken token) : ITokenStore
	{
		public AccessToken? GetToken() => token;

		public void SaveToken(AccessToken newToken) => throw new NotSupportedException();

		public void ClearToken() => throw new NotSupportedException();
	}
}

internal static class Api
{
	// Version 1, the Part B contract. ApiVersioningTests covers version 2.
	public const string Shipments = "/api/v1/shipments";

	public static string Shipment(string trackingNumber) => $"{Shipments}/{trackingNumber}";

	public static string Status(string trackingNumber) => $"{Shipment(trackingNumber)}/status";

	public static string Scans(string trackingNumber) => $"{Shipment(trackingNumber)}/scans";

	public static readonly ShipmentStatus[] DeliveredPath =
		[ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered];

	// Fictional details that pass every rule.
	public static Dictionary<string, object?> NewShipment(
		string trackingNumber,
		string city = "Pune") =>
		new()
		{
			["trackingNumber"] = trackingNumber,
			["recipientName"] = "DEMO Recipient",
			["recipientPhone"] = "+919876543210",
			["recipientAddress"] = "1 Demo Street",
			["recipientCity"] = city,
			["weightKg"] = 2.5m
		};

	public static Dictionary<string, object?> Scan(
		string scanId,
		string trackingNumber,
		DateTimeOffset scannedAtUtc,
		ShipmentScanType scanType = ShipmentScanType.Pickup) =>
		new()
		{
			["scanId"] = scanId,
			["trackingNumber"] = trackingNumber,
			["scanType"] = scanType,
			["courierId"] = "COURIER-DEMO-07",
			["location"] = "Pune hub",
			["scannedAtUtc"] = scannedAtUtc,
			["notes"] = null
		};

	public static string NewScanId() => $"scan::{Guid.NewGuid():D}";

	public static async Task<ShipmentResponse> CreateAsync(
		this HttpClient client,
		string trackingNumber,
		string city = "Pune")
	{
		var response = await client.PostAsJsonAsync(Shipments, NewShipment(trackingNumber, city));
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<ShipmentResponse>())!;
	}

	public static async Task AdvanceAsync(
		this HttpClient client,
		string trackingNumber,
		params ShipmentStatus[] statuses)
	{
		foreach (var status in statuses)
		{
			var response = await client.PatchAsJsonAsync(Status(trackingNumber), new { status });
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		}
	}

	public static Task<HttpResponseMessage> UploadAsync(
		this HttpClient client,
		string trackingNumber,
		params Dictionary<string, object?>[] scans) =>
		client.PostAsJsonAsync(Scans(trackingNumber), new { scans });

	// Every error is Problem Details: the media type, the status and a title.
	public static async Task<JsonElement> AssertProblemAsync(
		this HttpResponseMessage response,
		HttpStatusCode status)
	{
		Assert.Equal(status, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
		Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
		return problem;
	}

	// A 400 ValidationProblemDetails with an error for the named field.
	public static async Task AssertValidationErrorAsync(
		this HttpResponseMessage response,
		string field)
	{
		var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest);
		var errors = problem.GetProperty("errors");
		Assert.Contains(
			errors.EnumerateObject(),
			error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
	}
}
