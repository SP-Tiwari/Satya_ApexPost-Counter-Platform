using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApexPost.Api.Models;
using ApexPost.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
// it, so a developer's own database is never used.
internal sealed class ApiFactory(
	string environment = "Development",
	Action<IServiceCollection>? configureServices = null)
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
		});
		if (configureServices is not null)
			builder.ConfigureTestServices(configureServices);
	}

	public static ApiFactory WithClock(DateTimeOffset now) =>
		new(configureServices: services =>
			services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FixedTimeProvider(now))));
}

internal static class Api
{
	public const string Shipments = "/api/shipments";

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
