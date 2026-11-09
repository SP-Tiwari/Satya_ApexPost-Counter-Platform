using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace ApexPost.Api.IntegrationTests;

// The built-in OpenAPI documents, one per API version (/openapi/v1.json and
// /openapi/v2.json), and Swagger UI: served in Development only.
public sealed partial class OpenApiDocumentationTests
{
	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task OpenApiDocument_InDevelopment_IsServedWithTheApiInfo(string version)
	{
		using var factory = new ApiFactory("Development");

		var response = await factory.CreateClient().GetAsync($"/openapi/{version}.json");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
		var json = await response.Content.ReadAsStringAsync();
		var read = OpenApiDocument.Parse(json, OpenApiConstants.Json);
		Assert.NotNull(read.Document);
		Assert.Empty(read.Diagnostic?.Errors ?? []);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.StartsWith("3.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
		var info = root.GetProperty("info");
		Assert.Equal("ApexPost Logistics API", info.GetProperty("title").GetString());
		Assert.Equal(version, info.GetProperty("version").GetString());
		Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("description").GetString()));
		var contact = info.GetProperty("contact");
		Assert.Equal("ApexPost Platform Team", contact.GetProperty("name").GetString());
		Assert.EndsWith("@apexpost.example", contact.GetProperty("email").GetString(), StringComparison.Ordinal);
		var paths = root.GetProperty("paths");
		Assert.True(paths.TryGetProperty($"/api/{version}/shipments", out _));
		Assert.True(paths.TryGetProperty($"/api/{version}/shipments/{{trackingNumber}}/scans", out _));
	}

	// C5: one dropdown entry per version, read from the built-in documents,
	// newest first so that Swagger UI opens on version 2.
	[Fact]
	public async Task SwaggerUi_InDevelopment_ListsBothVersionDocuments_V2First()
	{
		using var factory = new ApiFactory("Development");
		var client = factory.CreateClient();

		var page = await client.GetAsync("/swagger/index.html");
		var settings = await client.GetAsync("/swagger/index.js");

		Assert.Equal(HttpStatusCode.OK, page.StatusCode);
		Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
		Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
		var urls = SwaggerUiUrls().Match(await settings.Content.ReadAsStringAsync());
		Assert.True(urls.Success, "Swagger UI has no urls setting.");
		using var entries = JsonDocument.Parse(urls.Groups["urls"].Value);
		Assert.Equal(
			[("/openapi/v2.json", "v2"), ("/openapi/v1.json", "v1 (deprecated)")],
			entries.RootElement.EnumerateArray()
				.Select(entry => (entry.GetProperty("url").GetString(), entry.GetProperty("name").GetString())));

		// Each entry is a document that is served and describes its version.
		foreach (var entry in entries.RootElement.EnumerateArray())
		{
			var document = await client.GetAsync(entry.GetProperty("url").GetString());
			Assert.Equal(HttpStatusCode.OK, document.StatusCode);
			using var json = JsonDocument.Parse(await document.Content.ReadAsStringAsync());
			Assert.StartsWith(
				json.RootElement.GetProperty("info").GetProperty("version").GetString()!,
				entry.GetProperty("name").GetString(),
				StringComparison.Ordinal);
		}
	}

	[GeneratedRegex(@"""urls""\s*:\s*(?<urls>\[[^\]]*\])")]
	private static partial Regex SwaggerUiUrls();

	[Fact]
	public async Task OpenApiDocument_ForAnUnknownVersion_IsNotServed()
	{
		using var factory = new ApiFactory("Development");

		var response = await factory.CreateClient().GetAsync("/openapi/v3.json");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("Production", "/openapi/v1.json")]
	[InlineData("Production", "/openapi/v2.json")]
	[InlineData("Production", "/swagger/index.html")]
	[InlineData("Staging", "/openapi/v1.json")]
	[InlineData("Staging", "/openapi/v2.json")]
	[InlineData("Staging", "/swagger/index.html")]
	public async Task Documentation_OutsideDevelopment_IsNotServed(string environment, string path)
	{
		using var factory = new ApiFactory(environment);

		var response = await factory.CreateClient().GetAsync(path);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	// C3: every operation, with its route name (the operationId), tag, summary
	// and every status code it can return in each version (null: the version
	// does not have it). C5: each version's document has its own operations
	// under its own path prefix. Part D: every operation can return 401, those
	// limited to a role also 403, and version 2 adds the partner operation.
	private static readonly (string Method, string Path, string OperationId, string Tag, string Summary, string? V1Codes, string? V2Codes)[] OperationTable =
	[
		("get", "shipments", "ListShipments", "Shipments", "List shipments by page, status and city", "200 400 401 403 500", "200 400 401 403 500"),
		("get", "shipments/{trackingNumber}", "GetShipment", "Shipments", "Get a shipment by tracking number", "200 401 404 500", "200 401 404 500"),
		("post", "shipments", "CreateShipment", "Shipments", "Create a shipment", "201 400 401 403 409 415 500", "201 400 401 403 409 415 500"),
		("put", "shipments/{trackingNumber}", "ReplaceShipment", "Shipments", "Replace a shipment's editable details", "200 400 401 403 404 409 415 500", "200 400 401 403 404 409 415 500"),
		("patch", "shipments/{trackingNumber}/status", "ChangeShipmentStatus", "Shipments", "Change a shipment's status", "200 400 401 403 404 409 415 500", "200 400 401 403 404 409 415 500"),
		("delete", "shipments/{trackingNumber}", "DeleteShipment", "Shipments", "Delete a shipment in Created status", "204 401 403 404 409 500", "204 401 403 404 409 500"),
		("post", "shipments/{trackingNumber}/scans", "UploadShipmentScans", "Scans", "Upload offline courier scans", "200 400 401 403 404 409 415 500", "200 400 401 403 404 409 415 500"),
		("get", "shipments/{trackingNumber}/scans", "GetShipmentScans", "Scans", "Get a shipment's scan history", "200 401 404 500", "200 401 404 500"),
		("get", "partner/shipments/{trackingNumber}", "GetPartnerShipment", "Partner", "Track a shipment as a partner", null, "200 401 404 500")
	];

	private static IEnumerable<(string Method, string Path, string OperationId, string Tag, string Summary, string StatusCodes)> OperationsOf(string version) =>
		OperationTable
			.Select(operation => (operation.Method, operation.Path, operation.OperationId, operation.Tag, operation.Summary,
				StatusCodes: version == "v1" ? operation.V1Codes : operation.V2Codes))
			.Where(operation => operation.StatusCodes is not null)
			.Select(operation => (operation.Method, operation.Path, operation.OperationId, operation.Tag, operation.Summary, operation.StatusCodes!));

	public static TheoryData<string, string, string, string, string, string, string> Operations
	{
		get
		{
			var data = new TheoryData<string, string, string, string, string, string, string>();
			foreach (var version in new[] { "v1", "v2" })
			{
				foreach (var (method, path, operationId, tag, summary, statusCodes) in OperationsOf(version))
					data.Add(version, method, $"/api/{version}/{path}", operationId, tag, summary, statusCodes);
			}
			return data;
		}
	}

	[Theory]
	[MemberData(nameof(Operations))]
	public async Task OpenApiDocument_EachOperation_HasItsMetadataAndEveryResponseCode(
		string version,
		string method,
		string path,
		string operationId,
		string tag,
		string summary,
		string statusCodes)
	{
		using var document = await GetDocumentAsync(version);

		var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

		Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
		Assert.Equal(new[] { tag }, operation.GetProperty("tags").EnumerateArray().Select(item => item.GetString()));
		Assert.Equal(summary, operation.GetProperty("summary").GetString());
		Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()));
		var responses = operation.GetProperty("responses");
		Assert.Equal(
			statusCodes.Split(' '),
			responses.EnumerateObject().Select(response => response.Name).Order(StringComparer.Ordinal));
		foreach (var response in responses.EnumerateObject())
		{
			Assert.False(string.IsNullOrWhiteSpace(response.Value.GetProperty("description").GetString()));
			var mediaTypes = response.Value.TryGetProperty("content", out var content)
				? content.EnumerateObject().Select(mediaType => mediaType.Name).ToArray()
				: [];
			string[] expected = response.Name switch
			{
				"204" => [],
				['2', ..] => ["application/json"],
				_ => ["application/problem+json"]
			};
			Assert.Equal(expected, mediaTypes);
		}
	}

	// C5: each document has exactly its own version's operations, with
	// the same unique operation IDs, and no path from the other version.
	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task OpenApiDocument_ListsOnlyItsOwnVersion_WithOneUniqueOperationIdPerAction(string version)
	{
		using var document = await GetDocumentAsync(version);

		var paths = document.RootElement.GetProperty("paths").EnumerateObject().ToArray();
		var operations = paths
			.SelectMany(path => path.Value.EnumerateObject()
				.Where(operation => operation.Value.TryGetProperty("responses", out _))
				.Select(operation => (Method: operation.Name, Path: path.Name, Id: operation.Value.GetProperty("operationId").GetString())))
			.ToArray();

		Assert.All(paths, path => Assert.StartsWith($"/api/{version}/", path.Name, StringComparison.Ordinal));
		Assert.Equal(
			OperationsOf(version).Select(operation => (operation.Method, $"/api/{version}/{operation.Path}", (string?)operation.OperationId))
				.Order(),
			operations.Select(operation => (operation.Method, operation.Path, operation.Id)).Order());
		Assert.Equal(operations.Length, operations.Select(operation => operation.Id).Distinct(StringComparer.Ordinal).Count());
	}

	// C5: version 1 is deprecated (C4), so its document marks every operation
	// deprecated and names the version to use instead; version 2 does neither.
	[Theory]
	[InlineData("v1", true)]
	[InlineData("v2", false)]
	public async Task OpenApiDocument_MarksOnlyTheDeprecatedVersion(string version, bool deprecated)
	{
		using var document = await GetDocumentAsync(version);
		var root = document.RootElement;

		var operations = root.GetProperty("paths").EnumerateObject()
			.SelectMany(path => path.Value.EnumerateObject())
			.Where(operation => operation.Value.TryGetProperty("responses", out _))
			.ToArray();

		Assert.Equal(OperationsOf(version).Count(), operations.Length);
		Assert.All(operations, operation => Assert.Equal(
			deprecated,
			operation.Value.TryGetProperty("deprecated", out var flag) && flag.GetBoolean()));
		var description = root.GetProperty("info").GetProperty("description").GetString()!;
		Assert.Equal(
			deprecated,
			description.Contains("Version 1.0 is deprecated; use version 2.0.", StringComparison.Ordinal));
	}

	// C5: the shipment schema is the version's own: flat recipient fields and
	// a numeric status in version 1; a nested recipient, the latest scan and a
	// string status in version 2.
	[Fact]
	public async Task OpenApiDocument_V1_HasThePartBShipmentSchema()
	{
		using var document = await GetDocumentAsync("v1");
		var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

		Assert.Equal(
			["trackingNumber", "recipientName", "recipientPhone", "recipientAddress", "recipientCity", "weightKg", "status"],
			PropertyNames(schemas.GetProperty("ShipmentResponse")));
		Assert.Equal("integer", schemas.GetProperty("ShipmentStatus").GetProperty("type").GetString());
		Assert.False(schemas.TryGetProperty("RecipientResponse", out _));
		Assert.False(schemas.TryGetProperty("LatestScanResponse", out _));
	}

	[Fact]
	public async Task OpenApiDocument_V2_HasTheNestedRecipient_TheLatestScan_AndAStringStatus()
	{
		using var document = await GetDocumentAsync("v2");
		var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

		var shipment = schemas.GetProperty("ShipmentResponse");
		Assert.Equal(
			["trackingNumber", "recipient", "weightKg", "status", "latestScan"],
			PropertyNames(shipment));
		Assert.Equal(
			"#/components/schemas/RecipientResponse",
			shipment.GetProperty("properties").GetProperty("recipient").GetProperty("$ref").GetString());
		Assert.Contains(
			shipment.GetProperty("properties").GetProperty("latestScan").GetProperty("oneOf").EnumerateArray(),
			item => item.TryGetProperty("$ref", out var reference)
				&& reference.GetString() == "#/components/schemas/LatestScanResponse");
		Assert.Equal(["name", "phone", "address", "city"], PropertyNames(schemas.GetProperty("RecipientResponse")));
		Assert.Equal(["scanType", "location", "scannedAtUtc"], PropertyNames(schemas.GetProperty("LatestScanResponse")));
		Assert.Equal(
			["Created", "PickedUp", "InTransit", "OutForDelivery", "Delivered", "Failed"],
			schemas.GetProperty("ShipmentStatus").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
	}

	// C6: ContractSchemaTransformer states the wire types precisely, so that a
	// generated client has enums and decimals: a string enum has its type, a
	// numeric enum its values and names, and a decimal the decimal format.
	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task OpenApiDocument_StatesTheEnumAndDecimalTypes(string version)
	{
		using var document = await GetDocumentAsync(version);
		var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

		AssertNumericEnum(schemas.GetProperty("ShipmentScanType"), ["Pickup", "InTransit", "Delivered", "Failed"]);
		string[] statuses = ["Created", "PickedUp", "InTransit", "OutForDelivery", "Delivered", "Failed"];
		var status = schemas.GetProperty("ShipmentStatus");
		if (version == "v1")
		{
			AssertNumericEnum(status, statuses);
		}
		else
		{
			Assert.Equal("string", status.GetProperty("type").GetString());
			Assert.Equal(statuses, status.GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
			Assert.False(status.TryGetProperty("x-enumNames", out _));
		}

		Assert.All(
			new[] { "ShipmentResponse", "CreateShipmentRequest", "UpdateShipmentRequest" },
			schema => Assert.Equal(
				"decimal",
				schemas.GetProperty(schema).GetProperty("properties").GetProperty("weightKg").GetProperty("format").GetString()));
	}

	private static void AssertNumericEnum(JsonElement schema, string[] names)
	{
		Assert.Equal("integer", schema.GetProperty("type").GetString());
		Assert.Equal(
			Enumerable.Range(0, names.Length),
			schema.GetProperty("enum").EnumerateArray().Select(item => item.GetInt32()));
		Assert.Equal(names, schema.GetProperty("x-enumNames").EnumerateArray().Select(item => item.GetString()));
	}

	private static IEnumerable<string> PropertyNames(JsonElement schema) =>
		schema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task OpenApiDocument_ParametersAndRequestBodies_HaveTheirXmlDescriptions(string version)
	{
		using var document = await GetDocumentAsync(version);
		var paths = document.RootElement.GetProperty("paths");
		var prefix = $"/api/{version}/shipments";

		var list = paths.GetProperty(prefix).GetProperty("get");
		Assert.All(
			list.GetProperty("parameters").EnumerateArray(),
			parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.GetProperty("description").GetString())));
		var trackingNumber = paths.GetProperty($"{prefix}/{{trackingNumber}}").GetProperty("get")
			.GetProperty("parameters").EnumerateArray()
			.Single(parameter => parameter.GetProperty("name").GetString() == "trackingNumber");
		Assert.StartsWith("The tracking number", trackingNumber.GetProperty("description").GetString(), StringComparison.Ordinal);

		// Each body has the request's own description, not the cancellation token's.
		Assert.Equal(
			"The tracking number and the shipment details.",
			paths.GetProperty(prefix).GetProperty("post").GetProperty("requestBody").GetProperty("description").GetString());
		Assert.Equal(
			"The new details.",
			paths.GetProperty($"{prefix}/{{trackingNumber}}").GetProperty("put").GetProperty("requestBody").GetProperty("description").GetString());
		Assert.Equal(
			"The new status.",
			paths.GetProperty($"{prefix}/{{trackingNumber}}/status").GetProperty("patch").GetProperty("requestBody").GetProperty("description").GetString());
		Assert.Equal(
			"One to 100 scans.",
			paths.GetProperty($"{prefix}/{{trackingNumber}}/scans").GetProperty("post").GetProperty("requestBody").GetProperty("description").GetString());
	}

	[Theory]
	[InlineData("v1", "CreateShipmentRequest")]
	[InlineData("v1", "UpdateShipmentRequest")]
	[InlineData("v1", "UpdateStatusRequest")]
	[InlineData("v1", "ScanUploadRequest")]
	[InlineData("v1", "ScanUploadItem")]
	[InlineData("v1", "ShipmentResponse")]
	[InlineData("v1", "ShipmentPageResponse")]
	[InlineData("v1", "ScanResponse")]
	[InlineData("v1", "ScanUploadResponse")]
	[InlineData("v1", "RejectedScanResponse")]
	[InlineData("v2", "UpdateStatusRequest")]
	[InlineData("v2", "ShipmentResponse")]
	[InlineData("v2", "RecipientResponse")]
	[InlineData("v2", "LatestScanResponse")]
	[InlineData("v2", "ShipmentPageResponse")]
	[InlineData("v2", "PartnerShipmentResponse")]
	public async Task OpenApiDocument_EachDto_AndEachOfItsProperties_HasAnXmlDescription(string version, string schemaName)
	{
		using var document = await GetDocumentAsync(version);

		var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);

		Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty("description").GetString()));
		Assert.All(schema.GetProperty("properties").EnumerateObject(), property =>
		{
			// A nullable enum or object is wrapped in oneOf; the description is on the reference.
			var described = property.Value.TryGetProperty("oneOf", out var oneOf)
				? oneOf.EnumerateArray().Single(item => item.TryGetProperty("$ref", out _))
				: property.Value;
			Assert.False(
				string.IsNullOrWhiteSpace(described.TryGetProperty("description", out var description) ? description.GetString() : null),
				$"{schemaName}.{property.Name} has no description.");
		});
	}

	private static async Task<JsonDocument> GetDocumentAsync(string version)
	{
		using var factory = new ApiFactory("Development");
		var response = await factory.CreateClient().GetAsync($"/openapi/{version}.json");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
	}
}
