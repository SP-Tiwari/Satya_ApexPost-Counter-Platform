using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApexPost.Client;
using ApexPost.Client.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DomainScanType = ApexPost.Domain.Enums.ShipmentScanType;
using DomainStatus = ApexPost.Domain.Enums.ShipmentStatus;

namespace ApexPost.Api.IntegrationTests;

// C6: the client NSwag generated from /openapi/v2.json (src/ApexPost.Client),
// run against the API in memory. Every request it sends is recorded.
public sealed class ClientSdkTests : IDisposable
{
	private const string TrackingNumber = "APX-60000001";

	private readonly ApiFactory _factory = new();
	private readonly List<string> _requests = [];
	private readonly ApexPostApiClient _client;

	public ClientSdkTests() =>
		_client = new ApexPostApiClient(_factory.CreateDefaultClient(new RecordingHandler(_requests)));

	public void Dispose() => _factory.Dispose();

	[Fact]
	public async Task CreateAndGet_ReturnTheTypedV2Shipment()
	{
		var created = await _client.CreateShipmentAsync(NewShipment(TrackingNumber));
		var fetched = await _client.GetShipmentAsync(TrackingNumber);

		foreach (var shipment in new[] { created, fetched })
		{
			Assert.Equal(TrackingNumber, shipment.TrackingNumber);
			Assert.Equal("DEMO Recipient", shipment.Recipient.Name);
			Assert.Equal("+919876543210", shipment.Recipient.Phone);
			Assert.Equal("1 Demo Street", shipment.Recipient.Address);
			Assert.Equal("Pune", shipment.Recipient.City);
			Assert.Equal(2.5m, shipment.WeightKg);
			Assert.Equal(ShipmentStatus.Created, shipment.Status);
			Assert.Null(shipment.LatestScan);
		}
	}

	[Fact]
	public async Task UploadScans_IsIdempotent_AndTheLatestScanIsTyped()
	{
		await _client.CreateShipmentAsync(NewShipment(TrackingNumber));
		var now = DateTimeOffset.UtcNow;
		var scans = new ScanUploadRequest
		{
			Scans =
			[
				Scan(ShipmentScanType.Pickup, now.AddMinutes(-30)),
				Scan(ShipmentScanType.InTransit, now.AddMinutes(-5))
			]
		};

		var first = await _client.UploadShipmentScansAsync(TrackingNumber, scans);
		var again = await _client.UploadShipmentScansAsync(TrackingNumber, scans);
		var shipment = await _client.GetShipmentAsync(TrackingNumber);
		var history = await _client.GetShipmentScansAsync(TrackingNumber);

		Assert.Equal((2, 0, 0), (first.Accepted, first.DuplicatesSkipped, first.Rejected));
		Assert.Equal((0, 2, 0), (again.Accepted, again.DuplicatesSkipped, again.Rejected));
		Assert.Equal(ShipmentScanType.InTransit, shipment.LatestScan!.ScanType);
		Assert.Equal("Pune hub", shipment.LatestScan.Location);
		Assert.Equal([ShipmentScanType.Pickup, ShipmentScanType.InTransit], history.Select(scan => scan.ScanType));
	}

	[Fact]
	public async Task ChangeStatusAndList_SendTheStatusName()
	{
		await _client.CreateShipmentAsync(NewShipment(TrackingNumber));

		var changed = await _client.ChangeShipmentStatusAsync(
			TrackingNumber, new UpdateStatusRequest { Status = ShipmentStatus.PickedUp });
		var page = await _client.ListShipmentsAsync(page: 1, pageSize: 10, status: ShipmentStatus.PickedUp, city: "Pune");

		Assert.Equal(ShipmentStatus.PickedUp, changed.Status);
		Assert.Contains($"PATCH /api/v2/shipments/{TrackingNumber}/status {{\"status\":\"PickedUp\"}}", _requests);
		Assert.Contains("GET /api/v2/shipments?Page=1&PageSize=10&Status=PickedUp&City=Pune", _requests);
		Assert.Equal(1, page.TotalCount);
		Assert.Equal(ShipmentStatus.PickedUp, Assert.Single(page.Items).Status);
	}

	[Fact]
	public async Task ReplaceAndDelete_Work_AndADeletedShipmentIsA404()
	{
		await _client.CreateShipmentAsync(NewShipment(TrackingNumber));

		var replaced = await _client.ReplaceShipmentAsync(
			TrackingNumber,
			new UpdateShipmentRequest
			{
				RecipientName = "DEMO Recipient",
				RecipientPhone = "+919876543210",
				RecipientAddress = "2 Demo Road",
				RecipientCity = "Mumbai",
				WeightKg = 4.75m
			});
		await _client.DeleteShipmentAsync(TrackingNumber);

		Assert.Equal("Mumbai", replaced.Recipient.City);
		Assert.Equal(4.75m, replaced.WeightKg);
		var missing = await Assert.ThrowsAsync<ApiException<ProblemDetails>>(() => _client.GetShipmentAsync(TrackingNumber));
		Assert.Equal((int)HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task UnknownShipment_ThrowsTheGeneratedExceptionWithTheProblemDetails()
	{
		var exception = await Assert.ThrowsAsync<ApiException<ProblemDetails>>(
			() => _client.GetShipmentAsync("APX-99999999"));

		Assert.Equal((int)HttpStatusCode.NotFound, exception.StatusCode);
		Assert.Equal((int)HttpStatusCode.NotFound, exception.Result.Status);
		Assert.False(string.IsNullOrWhiteSpace(exception.Result.Title));
	}

	[Fact]
	public async Task InvalidRequest_ThrowsTheGeneratedExceptionWithTheValidationErrors()
	{
		var exception = await Assert.ThrowsAsync<ApiException<ValidationProblemDetails>>(
			() => _client.CreateShipmentAsync(NewShipment("APX-1")));

		Assert.Equal((int)HttpStatusCode.BadRequest, exception.StatusCode);
		Assert.Contains("TrackingNumber", exception.Result.Errors.Keys);
	}

	[Fact]
	public async Task DuplicateAndRefusedTransition_ThrowTheGenerated409()
	{
		await _client.CreateShipmentAsync(NewShipment(TrackingNumber));

		var duplicate = await Assert.ThrowsAsync<ApiException<ProblemDetails>>(
			() => _client.CreateShipmentAsync(NewShipment(TrackingNumber)));
		var refused = await Assert.ThrowsAsync<ApiException<ProblemDetails>>(
			() => _client.ChangeShipmentStatusAsync(TrackingNumber, new UpdateStatusRequest { Status = ShipmentStatus.Delivered }));

		Assert.Equal((int)HttpStatusCode.Conflict, duplicate.StatusCode);
		Assert.Equal((int)HttpStatusCode.Conflict, refused.StatusCode);
	}

	// The client is generated from version 2 only: every URL it sends is /api/v2.
	[Fact]
	public async Task EveryRequest_GoesToVersion2()
	{
		await _client.CreateShipmentAsync(NewShipment(TrackingNumber));
		await _client.GetShipmentAsync(TrackingNumber);
		await _client.GetShipmentScansAsync(TrackingNumber);
		await _client.ListShipmentsAsync(null, null, null, null);
		await _client.DeleteShipmentAsync(TrackingNumber);

		Assert.Equal(5, _requests.Count);
		Assert.All(_requests, request => Assert.Contains(" /api/v2/", request, StringComparison.Ordinal));
	}

	// The generated code matches the document the API serves now: one method
	// per operationId, the route names of C3, and one type per schema.
	[Fact]
	public async Task GeneratedClient_HasAMethodForEachV2Operation_AndATypeForEachSchema()
	{
		using var document = JsonDocument.Parse(
			await _factory.CreateClient().GetStringAsync("/openapi/v2.json"));
		var root = document.RootElement;

		var operationIds = root.GetProperty("paths").EnumerateObject()
			.SelectMany(path => path.Value.EnumerateObject())
			.Select(operation => operation.Value.GetProperty("operationId").GetString())
			.Order(StringComparer.Ordinal);
		var methods = typeof(IApexPostApiClient).GetMethods()
			.Select(method => method.Name)
			.Distinct()
			.Order(StringComparer.Ordinal);
		Assert.Equal(operationIds.Select(id => $"{id}Async"), methods);

		Assert.All(
			root.GetProperty("components").GetProperty("schemas").EnumerateObject(),
			schema => Assert.NotNull(typeof(IApexPostApiClient).Assembly.GetType($"ApexPost.Client.{schema.Name}")));
	}

	[Fact]
	public void GeneratedModels_HaveTheV2Types()
	{
		var status = typeof(ShipmentResponse).GetProperty(nameof(ShipmentResponse.Status))!;
		Assert.Equal(typeof(ShipmentStatus), status.PropertyType);
		Assert.Equal(
			typeof(JsonStringEnumConverter<ShipmentStatus>),
			Assert.Single(status.GetCustomAttributes(typeof(JsonConverterAttribute), false).Cast<JsonConverterAttribute>()).ConverterType);
		Assert.Equal(typeof(ShipmentStatus?), typeof(UpdateStatusRequest).GetProperty(nameof(UpdateStatusRequest.Status))!.PropertyType);
		Assert.Equal(typeof(RecipientResponse), typeof(ShipmentResponse).GetProperty(nameof(ShipmentResponse.Recipient))!.PropertyType);
		Assert.Equal(typeof(LatestScanResponse), typeof(ShipmentResponse).GetProperty(nameof(ShipmentResponse.LatestScan))!.PropertyType);
		Assert.Equal(typeof(decimal), typeof(ShipmentResponse).GetProperty(nameof(ShipmentResponse.WeightKg))!.PropertyType);
		Assert.Equal(typeof(decimal?), typeof(CreateShipmentRequest).GetProperty(nameof(CreateShipmentRequest.WeightKg))!.PropertyType);
		Assert.Equal(typeof(ShipmentScanType), typeof(LatestScanResponse).GetProperty(nameof(LatestScanResponse.ScanType))!.PropertyType);
		Assert.Equal(typeof(ShipmentScanType?), typeof(ScanUploadItem).GetProperty(nameof(ScanUploadItem.ScanType))!.PropertyType);
		Assert.Equal(typeof(DateTimeOffset), typeof(LatestScanResponse).GetProperty(nameof(LatestScanResponse.ScannedAtUtc))!.PropertyType);

		// The generated enums have the API's names and numbers.
		Assert.Equal(
			Enum.GetValues<DomainStatus>().Select(value => (value.ToString(), (int)value)),
			Enum.GetValues<ShipmentStatus>().Select(value => (value.ToString(), (int)value)));
		Assert.Equal(
			Enum.GetValues<DomainScanType>().Select(value => (value.ToString(), (int)value)),
			Enum.GetValues<ShipmentScanType>().Select(value => (value.ToString(), (int)value)));
	}

	// The console consumer, through its AddHttpClient registration, with the
	// API in memory as the primary handler.
	[Fact]
	public async Task Consumer_ThroughAddHttpClient_CreatesFetchesUploadsAndHandlesThe404()
	{
		var services = new ServiceCollection();
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ApexPostApiClientRegistration.BaseUrlKey] = "http://localhost/"
			})
			.Build();
		services.AddApexPostApiClient(configuration)
			.ConfigurePrimaryHttpMessageHandler(() => _factory.Server.CreateHandler());
		await using var provider = services.BuildServiceProvider();
		using var output = new StringWriter();

		await ShipmentClientDemo.RunAsync(
			provider.GetRequiredService<IApexPostApiClient>(), output, CancellationToken.None);

		var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(4, lines.Length);
		Assert.Matches(@"^1\. Created APX-\d{8}: status Created, recipient DEMO Recipient, Pune, 2\.5 kg$", lines[0]);
		Assert.Matches(@"^2\. Fetched APX-\d{8}: status Created, latest scan none$", lines[1]);
		Assert.Equal("3. Uploaded two scans: accepted 2, duplicates skipped 0, rejected 0", lines[2]);
		Assert.StartsWith(
			$"4. {ShipmentClientDemo.UnknownTrackingNumber}: 404 ",
			lines[3],
			StringComparison.Ordinal);
		Assert.EndsWith("(handled as ApiException<ProblemDetails>)", lines[3], StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("localhost:7280")]
	public void Consumer_WithoutAnAbsoluteBaseUrl_FailsAtRegistration(string? baseUrl)
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ApexPostApiClientRegistration.BaseUrlKey] = baseUrl
			})
			.Build();

		Assert.Throws<InvalidOperationException>(
			() => new ServiceCollection().AddApexPostApiClient(configuration));
	}

	private static CreateShipmentRequest NewShipment(string trackingNumber) => new()
	{
		TrackingNumber = trackingNumber,
		RecipientName = "DEMO Recipient",
		RecipientPhone = "+919876543210",
		RecipientAddress = "1 Demo Street",
		RecipientCity = "Pune",
		WeightKg = 2.5m
	};

	private static ScanUploadItem Scan(ShipmentScanType scanType, DateTimeOffset scannedAtUtc) => new()
	{
		ScanId = $"scan::{Guid.NewGuid()}",
		TrackingNumber = TrackingNumber,
		ScanType = scanType,
		CourierId = "COURIER-DEMO-07",
		Location = "Pune hub",
		ScannedAtUtc = scannedAtUtc
	};

	// Records each request as "METHOD /path?query", plus the body of a PATCH.
	private sealed class RecordingHandler(List<string> requests) : DelegatingHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var line = $"{request.Method} {request.RequestUri!.PathAndQuery}";
			if (request.Method == HttpMethod.Patch && request.Content is not null)
				line += $" {await request.Content.ReadAsStringAsync(cancellationToken)}";
			requests.Add(line);
			return await base.SendAsync(request, cancellationToken);
		}
	}
}
