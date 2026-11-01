using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApexPost.Application.Auditing;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.Notifications;
using ApexPost.Domain.ValueObjects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApexPost.Web.IntegrationTests;

// The catalogue page, the shipping-label preview, clerk access to their own
// sale, the sale's audit record, and the real partner parcel-status endpoint.
public sealed class CounterScreensIntegrationTests : IAsyncLifetime
{
	private const string PartnerKey = "test-partner-key-not-a-secret";
	private const string PartnerOrigin = "https://tracking.example.test";
	private const string ShipmentId = "4f1c2b7e9a0d4c33b1e2f3a4b5c6d7e8";
	private static readonly IPAddress AllowedPartnerIp = IPAddress.Parse("192.0.2.10");
	private static readonly JsonSerializerOptions AuditJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};
	private string _storageDirectory = null!;
	private WebApplicationFactory<Program> _factory = null!;


	public Task InitializeAsync()
	{
		_storageDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-Screens-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_storageDirectory);
		_factory = CreateFactory(AllowedPartnerIp);
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		if (Directory.Exists(_storageDirectory))
			Directory.Delete(_storageDirectory, recursive: true);
	}

	[Fact]
	public async Task Catalogue_ShowsProductsWithHomeBranchStock()
	{
		await SeedAsync();
		using var client = CreateClient("CounterClerk", "clerk-1");

		var response = await client.GetAsync("/Catalogue");
		var html = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Matches(@"BOX-01</td>\s*<td>Box</td>\s*<td>Packaging</td>\s*<td>20 INR</td>\s*<td>Standard</td>\s*<td>3</td>", html);
		Assert.Matches(@"PEN-01</td>[\s\S]*?<td>Not stocked</td>", html);
		Assert.DoesNotContain("<td>50</td>", html, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Catalogue_RequiresSignIn()
	{
		using var client = CreateClient(null, null);

		Assert.Equal(
			HttpStatusCode.Unauthorized,
			(await client.GetAsync("/Catalogue")).StatusCode);
	}

	[Fact]
	public async Task ClerkSale_RedirectsToDetailsTheClerkCanOpenReadOnly()
	{
		await SeedAsync();
		using var client = CreateClient("CounterClerk", "clerk-1");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var create = await client.PostAsync(
			"/Transactions/Create", CreateSaleForm(transactionId, token));
		Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
		Assert.Equal(
			$"/Transactions/{transactionId}",
			create.Headers.Location?.OriginalString);

		var details = await client.GetAsync($"/Transactions/{transactionId}");
		var html = await details.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.OK, details.StatusCode);
		Assert.DoesNotContain("Void transaction", html, StringComparison.Ordinal);
		Assert.DoesNotContain("Refund transaction", html, StringComparison.Ordinal);
		Assert.Contains(
			$"/Transactions/{transactionId}/label", html, StringComparison.Ordinal);
		Assert.Equal(
			HttpStatusCode.OK,
			(await client.GetAsync($"/Transactions/{transactionId}/summary")).StatusCode);
	}

	[Fact]
	public async Task ClerkSale_AuditRecordCarriesTheTransactionId()
	{
		await SeedAsync();
		using var client = CreateClient("CounterClerk", "clerk-1");
		var (transactionId, token) = await GetCreateFormAsync(client);

		await client.PostAsync(
			"/Transactions/Create", CreateSaleForm(transactionId, token));

		var record = Assert.Single(await ReadAuditRecordsAsync());
		Assert.Equal("Transactions", record.Controller);
		Assert.Equal("Create", record.Action);
		Assert.Equal(302, record.StatusCode);
		Assert.Equal("clerk-1", record.StaffId);
		Assert.Equal(transactionId.ToString(), record.RouteValues["transactionId"]);
	}

	[Fact]
	public async Task AuditWriteFailure_LeavesTheSaleCommittedAndTheResponseUnchanged()
	{
		await SeedAsync();
		using var failingAudit = _factory.WithWebHostBuilder(builder =>
			builder.ConfigureTestServices(services =>
				services.AddSingleton<ApexPost.Application.Auditing.IAuditStore, FailingAuditStore>()));
		using var client = failingAudit.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		client.DefaultRequestHeaders.Add("X-Screens-Role", "CounterClerk");
		client.DefaultRequestHeaders.Add("X-Screens-StaffId", "clerk-1");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateSaleForm(transactionId, token));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Contains(
			(await LoadSnapshotAsync()).TransactionBaskets,
			basket => basket.Id == transactionId &&
				basket.PaymentStatus == TransactionPaymentStatus.Paid);
		Assert.Empty(await ReadAuditRecordsAsync());
	}

	[Fact]
	public async Task BusinessFailure_RollsBackButKeepsItsAuditRecord()
	{
		await SeedAsync();
		using var client = CreateClient("CounterClerk", "clerk-1");
		var (transactionId, token) = await GetCreateFormAsync(client);
		await client.PostAsync(
			"/Transactions/Create", CreateSaleForm(transactionId, token));
		var stockAfterSale = (await LoadSnapshotAsync()).BranchStocks
			.Select(stock => stock.Quantity)
			.ToArray();

		var repeated = await client.PostAsync(
			"/Transactions/Create", CreateSaleForm(transactionId, token));

		Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
		var snapshot = await LoadSnapshotAsync();
		Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(
			stockAfterSale,
			snapshot.BranchStocks.Select(stock => stock.Quantity).ToArray());
		var records = await ReadAuditRecordsAsync();
		Assert.Contains(records, record =>
			record.StatusCode == 409 && record.Outcome == "Rejected");
		Assert.Contains(records, record => record.StatusCode == 302);
	}

	[Fact]
	public async Task Sale_IgnoresPostedStatusAndAuditFields()
	{
		await SeedAsync();
		using var client = CreateClient("CounterClerk", "clerk-1");
		var (transactionId, token) = await GetCreateFormAsync(client);
		var form = await CreateSaleForm(transactionId, token).ReadAsStringAsync();
		var overPosted = new StringContent(
			form +
			"&Status=Refunded&PaymentStatus=Voided&Id=" + Guid.NewGuid() +
			"&BranchCode=BR99&ClerkStaffId=someone-else" +
			"&CreatedBy=attacker&CreatedAtUtc=2000-01-01&ModifiedAtUtc=2000-01-01" +
			"&OccurredAtUtc=2000-01-01",
			System.Text.Encoding.UTF8,
			"application/x-www-form-urlencoded");

		var response = await client.PostAsync("/Transactions/Create", overPosted);

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var basket = Assert.Single((await LoadSnapshotAsync()).TransactionBaskets);
		Assert.Equal(transactionId, basket.Id);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(new BranchCode("BR01"), basket.BranchCode);
		Assert.Equal("clerk-1", basket.SaleDetails?.ClerkStaffId);
		Assert.NotEqual(2000, basket.OccurredAtUtc?.Year);
	}

	[Fact]
	public async Task PageViews_AreNotAudited()
	{
		var transactionId = await SeedSaleAsync("clerk-1");
		using var client = CreateClient("CounterClerk", "clerk-1");

		await client.GetAsync("/Catalogue");
		await client.GetAsync($"/Transactions/{transactionId}");
		await client.GetAsync($"/Transactions/{transactionId}/label");

		Assert.Empty(await TestSqlServer.ReadAuditRecordsAsync());
	}

	[Fact]
	public async Task Clerk_CannotOpenAnotherClerksSale()
	{
		var transactionId = await SeedSaleAsync("clerk-2");
		using var client = CreateClient("CounterClerk", "clerk-1");

		Assert.Equal(
			HttpStatusCode.NotFound,
			(await client.GetAsync($"/Transactions/{transactionId}")).StatusCode);
		Assert.Equal(
			HttpStatusCode.NotFound,
			(await client.GetAsync($"/Transactions/{transactionId}/label")).StatusCode);
	}

	[Fact]
	public async Task Clerk_GetsForbiddenOnVoidAndTransactionList()
	{
		var transactionId = await SeedSaleAsync("clerk-1");
		using var client = CreateClient("CounterClerk", "clerk-1");

		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await client.GetAsync("/Transactions")).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await client.PostAsync(
				$"/Transactions/{transactionId}/void",
				new FormUrlEncodedContent([new("Reason", "Not allowed")]))).StatusCode);
		Assert.Equal(
			TransactionPaymentStatus.Paid,
			Assert.Single((await LoadSnapshotAsync()).TransactionBaskets).PaymentStatus);
	}

	[Fact]
	public async Task Label_ShowsTheRecordedShipmentForClerkAndManager()
	{
		var transactionId = await SeedSaleAsync("clerk-1");

		foreach (var (role, staffId) in new[]
			{ ("CounterClerk", "clerk-1"), ("BranchManager", "manager-1") })
		{
			using var client = CreateClient(role, staffId);
			var response = await client.GetAsync($"/Transactions/{transactionId}/label");
			var html = await response.Content.ReadAsStringAsync();

			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Contains(ShipmentId, html, StringComparison.Ordinal);
			Assert.Contains("ZONE-A", html, StringComparison.Ordinal);
			Assert.Contains("2 kg", html, StringComparison.Ordinal);
			Assert.Contains("10 x 20 x 30 cm", html, StringComparison.Ordinal);
			Assert.Contains("BR01", html, StringComparison.Ordinal);
			Assert.DoesNotContain("Do not use these labels", html, StringComparison.Ordinal);
			// The CSP blocks inline handlers, so the page must not use one.
			Assert.DoesNotContain("onclick=", html, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task Label_ForVoidedSaleIsMarkedNotForUse()
	{
		var transactionId = await SeedSaleAsync(
			"clerk-1", TransactionPaymentStatus.Voided);
		using var client = CreateClient("BranchManager", "manager-1");

		var html = await (await client.GetAsync(
			$"/Transactions/{transactionId}/label")).Content.ReadAsStringAsync();

		Assert.Contains("This transaction is Voided", html, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Label_ForSaleWithoutShippingIsNotFound()
	{
		var transactionId = await SeedSaleAsync("clerk-1", withShipment: false);
		using var client = CreateClient("CounterClerk", "clerk-1");

		Assert.Equal(
			HttpStatusCode.NotFound,
			(await client.GetAsync($"/Transactions/{transactionId}/label")).StatusCode);
	}

	// No browser engine is available (only xUnit and Mvc.Testing are allowed),
	// so the click itself is not exercised. This checks the wiring instead:
	// the button carries the hook, the page loads site.js from its own origin
	// (allowed by the CSP), and site.js binds that hook to window.print().
	[Fact]
	public async Task Label_PrintButtonIsWiredThroughSiteScript()
	{
		var transactionId = await SeedSaleAsync("clerk-1");
		using var client = CreateClient("CounterClerk", "clerk-1");

		var html = await (await client.GetAsync(
			$"/Transactions/{transactionId}/label")).Content.ReadAsStringAsync();
		var scriptPath = Regex.Match(
			html, "<script src=\"(/js/site\\.js[^\"]*)\"").Groups[1].Value;
		var script = await client.GetAsync(scriptPath);
		var source = await script.Content.ReadAsStringAsync();

		Assert.Matches("<button[^>]*data-print-page[^>]*>Print labels</button>", html);
		Assert.Equal(HttpStatusCode.OK, script.StatusCode);
		Assert.Contains("[data-print-page]", source, StringComparison.Ordinal);
		Assert.Contains("window.print()", source, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Label_RequiresSignIn()
	{
		var transactionId = await SeedSaleAsync("clerk-1");
		using var client = CreateClient(null, null);

		Assert.Equal(
			HttpStatusCode.Unauthorized,
			(await client.GetAsync($"/Transactions/{transactionId}/label")).StatusCode);
	}

	[Fact]
	public async Task ParcelStatus_WithoutApiKeyReturns401()
	{
		await SeedSaleAsync("clerk-1");
		using var client = _factory.CreateClient();

		Assert.Equal(
			HttpStatusCode.Unauthorized,
			(await client.GetAsync($"/integration/parcel-status/{ShipmentId}")).StatusCode);
	}

	[Fact]
	public async Task ParcelStatus_FromBlockedIpReturns403()
	{
		await using var blocked = CreateFactory(IPAddress.Parse("198.51.100.7"));
		using var client = blocked.CreateClient();
		client.DefaultRequestHeaders.Add("X-API-Key", PartnerKey);

		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await client.GetAsync($"/integration/parcel-status/{ShipmentId}")).StatusCode);
	}

	[Fact]
	public async Task ParcelStatus_WithKeyReturnsStoredFactsOnly()
	{
		await SeedSaleAsync("clerk-1");
		using var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-API-Key", PartnerKey);

		var response = await client.GetAsync($"/integration/parcel-status/{ShipmentId}");
		var json = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal(ShipmentId, root.GetProperty("shipmentId").GetString());
		Assert.Equal("Parcel", root.GetProperty("type").GetString());
		Assert.Equal("ZONE-A", root.GetProperty("destinationZone").GetString());
		Assert.Equal("Paid", root.GetProperty("transactionStatus").GetString());
		Assert.Equal(
			"NotConfigured",
			root.GetProperty("courier").GetProperty("outcome").GetString());
		Assert.DoesNotContain("clerk-1", json, StringComparison.Ordinal);
		Assert.DoesNotContain("INR", json, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("0000000000000000000000000000dead", HttpStatusCode.NotFound)]
	[InlineData("bad%20id!", HttpStatusCode.BadRequest)]
	public async Task ParcelStatus_UnknownOrInvalidShipment(
		string shipmentId,
		HttpStatusCode expected)
	{
		await SeedSaleAsync("clerk-1");
		using var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-API-Key", PartnerKey);

		Assert.Equal(
			expected,
			(await client.GetAsync($"/integration/parcel-status/{shipmentId}")).StatusCode);
	}

	[Theory]
	[InlineData("0000000000000000000000000000dead", 404, "Not Found")]
	[InlineData("bad%20id!", 400, "Invalid shipment ID.")]
	public async Task ParcelStatus_ErrorsAreSafeProblemDetails(
		string shipmentId,
		int status,
		string title)
	{
		await SeedSaleAsync("clerk-1");
		using var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-API-Key", PartnerKey);

		var response = await client.GetAsync($"/integration/parcel-status/{shipmentId}");

		Assert.Equal(status, (int)response.StatusCode);
		Assert.StartsWith(
			"application/problem+json",
			response.Content.Headers.ContentType?.ToString());
		var body = await response.Content.ReadAsStringAsync();
		using var problem = JsonDocument.Parse(body);
		Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
		Assert.Equal(title, problem.RootElement.GetProperty("title").GetString());
		Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("clerk-1", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ParcelStatus_AllowsTheConfiguredWidgetOrigin()
	{
		await SeedSaleAsync("clerk-1");
		using var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-API-Key", PartnerKey);
		client.DefaultRequestHeaders.Add("Origin", PartnerOrigin);

		var response = await client.GetAsync($"/integration/parcel-status/{ShipmentId}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(
			PartnerOrigin,
			Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
	}

	private WebApplicationFactory<Program> CreateFactory(IPAddress remoteIp) =>
		new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting("Storage:Directory", _storageDirectory);
				builder.UseSetting(
					"Authentication:CredentialFile",
					Path.Combine(_storageDirectory, "credentials.json"));
				builder.UseSetting("Transactions:TaxRates:Standard", "0.1");
				builder.UseSetting("Transactions:PaymentMethods:0", "Cash");
				builder.UseSetting("Shipping:TaxCategory", "Standard");
				builder.UseSetting(
					"Shipping:MaximumParcelWeightKilograms:Standard", "5");
				builder.UseSetting("ApiSecurity:ApiKey", PartnerKey);
				builder.UseSetting(
					"ApiSecurity:AllowedIpAddresses:0", AllowedPartnerIp.ToString());
				builder.UseSetting("ApiSecurity:PartnerRateLimit:PermitLimit", "100");
				builder.UseSetting("ApiSecurity:PartnerRateLimit:WindowSeconds", "60");
				builder.UseSetting("ParcelTracking:AllowedOrigins:0", PartnerOrigin);
				builder.ConfigureTestServices(services =>
				{
					services.AddSingleton<IStartupFilter>(
						new FixedRemoteIpAddressStartupFilter(remoteIp));
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme = ScreensTestAuthenticationHandler.SchemeName;
						options.DefaultChallengeScheme = ScreensTestAuthenticationHandler.SchemeName;
						options.DefaultForbidScheme = ScreensTestAuthenticationHandler.SchemeName;
					}).AddScheme<AuthenticationSchemeOptions, ScreensTestAuthenticationHandler>(
						ScreensTestAuthenticationHandler.SchemeName, _ => { });
				});
			});

	private HttpClient CreateClient(string? role, string? staffId)
	{
		var client = _factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		if (role is not null)
		{
			client.DefaultRequestHeaders.Add("X-Screens-Role", role);
			client.DefaultRequestHeaders.Add("X-Screens-StaffId", staffId);
		}
		return client;
	}

	private Task SeedAsync(IEnumerable<TransactionBasket>? baskets = null) =>
		_factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[
					new Branch(new BranchCode("BR01"), "Home Branch", "Region",
						"09:00-17:00", [new CounterTerminal("COUNTER-1", "Counter")]),
					new Branch(new BranchCode("BR02"), "Other Branch", "Region",
						"09:00-17:00", [new CounterTerminal("COUNTER-2", "Counter")])
				],
				[
					CreateStaff("clerk-1", StaffRole.CounterClerk),
					CreateStaff("clerk-2", StaffRole.CounterClerk),
					CreateStaff("manager-1", StaffRole.BranchManager)
				],
				[
					new Product(new Sku("BOX-01"), "Box", ProductCategory.Packaging,
						new Money(20m, "INR"), TaxCategory.Standard),
					new Product(new Sku("PEN-01"), "Pen", ProductCategory.Stationery,
						new Money(5m, "INR"), TaxCategory.Standard)
				],
				[
					new BranchStock(new BranchCode("BR01"), new Sku("BOX-01"), 3),
					new BranchStock(new BranchCode("BR02"), new Sku("PEN-01"), 50)
				],
				[],
				[new ShippingRate("ZONE-A", ShippingServiceLevel.Standard,
					new Money(10m, "INR"), new Money(2m, "INR"))],
				[],
				[],
				baskets ?? []));

	private async Task<Guid> SeedSaleAsync(
		string clerkId,
		TransactionPaymentStatus status = TransactionPaymentStatus.Paid,
		bool withShipment = true)
	{
		var id = Guid.NewGuid();
		var line = withShipment
			? new TransactionLineItem(
				"Shipping Parcel to ZONE-A (Standard)",
				TransactionItemType.Service,
				new Money(14m, "INR"),
				1,
				TaxCategory.Standard,
				productSku: null,
				shippingItem: new ShippingItem(
					ShipmentId,
					ShippingItemType.Parcel,
					new Weight(2m),
					new Dimensions(10m, 20m, 30m),
					"ZONE-A",
					ShippingServiceLevel.Standard))
			: new TransactionLineItem(
				"Counter service",
				TransactionItemType.Service,
				new Money(10m, "INR"),
				1,
				TaxCategory.Standard);
		await SeedAsync(
		[
			TransactionBasket.Reconstitute(
				id,
				"INR",
				new Dictionary<TaxCategory, decimal> { [TaxCategory.Standard] = 0.1m },
				[line],
				status,
				status == TransactionPaymentStatus.Voided
					? [new TransactionVoidedNotification("Entered in error")]
					: [],
				new BranchCode("BR01"),
				new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero),
				new CounterSaleDetails("COUNTER-1", clerkId, "Cash"))
		]);
		return id;
	}

	private async Task<ApexPostSnapshot> LoadSnapshotAsync() =>
		await _factory.Services.GetRequiredService<IApexPostRepository>().LoadAsync();

	private static Task<AuditRecord[]> ReadAuditRecordsAsync() =>
		TestSqlServer.ReadAuditRecordsAsync();

	private static Staff CreateStaff(string id, StaffRole role) =>
		new(id, id, $"{id}@example.test", role, new BranchCode("BR01"));

	private static async Task<(Guid TransactionId, string Token)> GetCreateFormAsync(
		HttpClient client)
	{
		var html = await (await client.GetAsync("/Transactions/Create"))
			.Content.ReadAsStringAsync();
		var token = Regex.Match(
			html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
			.Groups[1].Value;
		var transactionId = Regex.Match(
			html, "name=\"TransactionId\"[^>]*value=\"([A-Fa-f0-9-]+)\"")
			.Groups[1].Value;
		return (Guid.Parse(transactionId), token);
	}

	private static FormUrlEncodedContent CreateSaleForm(Guid transactionId, string token) =>
		new(
		[
			new("__RequestVerificationToken", token),
			new("TransactionId", transactionId.ToString()),
			new("CounterTerminalId", "COUNTER-1"),
			new("PaymentMethod", "Cash"),
			new("GoodsSku", "BOX-01"),
			new("GoodsQuantity", "1"),
			new("IncludeShipping", "true"),
			new("ShippingType", nameof(ShippingItemType.Parcel)),
			new("WeightKilograms", "2"),
			new("LengthCm", "10"),
			new("WidthCm", "20"),
			new("HeightCm", "30"),
			new("DestinationZone", "ZONE-A"),
			new("ServiceLevel", nameof(ShippingServiceLevel.Standard))
		]);
}

// Signs in as the staff ID and role in the X-Screens-* headers.
public sealed class ScreensTestAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	public const string SchemeName = "ScreensTest";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var role = Request.Headers["X-Screens-Role"].ToString();
		var staffId = Request.Headers["X-Screens-StaffId"].ToString();
		if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(staffId))
			return Task.FromResult(AuthenticateResult.NoResult());

		var identity = new ClaimsIdentity(
			[
				new Claim("StaffId", staffId),
				new Claim("Role", role),
				new Claim("BranchCode", "BR01"),
				new Claim(ClaimTypes.Role, role)
			],
			SchemeName);
		return Task.FromResult(AuthenticateResult.Success(
			new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
	}
}
