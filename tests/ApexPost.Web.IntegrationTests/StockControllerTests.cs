using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApexPost.Application.Auditing;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApexPost.Web.IntegrationTests;

public sealed class StockControllerTests : IAsyncLifetime
{
	private static readonly JsonSerializerOptions AuditJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};
	private const string ManagerId = "stock-manager";
	private const string ProductSku = "BOX-01";
	private string _storageDirectory = null!;
	private string _auditFilePath = null!;
	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_storageDirectory = CreateStorageDirectory();
		_auditFilePath = Path.Combine(
			_storageDirectory, "audit-events.jsonl");
		_factory = CreateFactory(_storageDirectory, _auditFilePath);
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		DeleteStorageDirectory(_storageDirectory);
	}

	[Fact]
	public async Task Adjust_PersistsOwnBranchChangeAndWritesSafeAuditRecord()
	{
		await SeedAsync([CreateManager()]);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId, "BR01");
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await client.PostAsync(
			"/Stock/Adjust?branchCode=BR02",
			CreateForm(ProductSku, 3, token, "BR02"));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/Stock/Adjust", response.Headers.Location?.OriginalString);
		var snapshot = await LoadSnapshotAsync();
		Assert.Equal(7, Assert.Single(
			snapshot.BranchStocks,
			stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
		Assert.Equal(20, Assert.Single(
			snapshot.BranchStocks,
			stock => stock.BranchCode == new BranchCode("BR02")).Quantity);

		var records = await TestSqlServer.ReadAuditRecordsAsync();
		var auditJson = await TestSqlServer.ReadAuditTextAsync();
		Assert.Contains(
			records,
			item => item.Controller == "Stock" &&
				item.Action == "Adjust" &&
				item.Outcome == "Succeeded" &&
				item.StaffId == ManagerId);
		Assert.DoesNotContain(ProductSku, auditJson, StringComparison.Ordinal);
		Assert.DoesNotContain("QuantityChange", auditJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Adjust_RemovalCannotMakeHomeBranchStockNegative()
	{
		await SeedAsync([CreateManager()]);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId, "BR01");
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm(ProductSku, -5, token));

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(4, Assert.Single(
			(await LoadSnapshotAsync()).BranchStocks,
			stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
	}

	[Fact]
	public async Task Adjust_RejectsZeroQuantityWithoutMutation()
	{
		await SeedAsync([CreateManager()]);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId, "BR01");
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm(ProductSku, 0, token));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(4, Assert.Single(
			(await LoadSnapshotAsync()).BranchStocks,
			stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
	}

	[Fact]
	public async Task Adjust_RejectsQuantityOverflowWithoutMutation()
	{
		await SeedAsync([CreateManager()], homeStockQuantity: int.MaxValue);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId, "BR01");
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm(ProductSku, 1, token));

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(
			int.MaxValue,
			Assert.Single(
				(await LoadSnapshotAsync()).BranchStocks,
				stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
	}

	[Fact]
	public async Task Adjust_RejectsMissingProductWithoutMutation()
	{
		await SeedAsync([CreateManager()]);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId, "BR01");
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm("NOT-FOUND", 1, token));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal(4, Assert.Single(
			(await LoadSnapshotAsync()).BranchStocks,
			stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
	}

	[Fact]
	public async Task Adjust_RejectsMissingHomeBranchStockWithoutMutation()
	{
		await SeedAsync([CreateManager()], includeHomeStock: false);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId, "BR01");
		var token = await GetAntiforgeryTokenAsync(client);

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm(ProductSku, 1, token));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal(20, Assert.Single(
			(await LoadSnapshotAsync()).BranchStocks).Quantity);
	}

	[Fact]
	public async Task Adjust_RejectsCounterClerkAndUnauthenticatedUser()
	{
		await SeedAsync([CreateManager()]);
		using var anonymousClient = CreateClient(
			_factory, null, null);
		using var clerkClient = CreateClient(
			_factory, "CounterClerk", "stock-clerk");

		Assert.Equal(
			HttpStatusCode.Unauthorized,
			(await anonymousClient.GetAsync("/Stock/Adjust")).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await clerkClient.GetAsync("/Stock/Adjust")).StatusCode);
	}

	[Fact]
	public async Task Adjust_PostRejectsCounterClerkWithoutChangingStock()
	{
		await SeedAsync([CreateManager()]);
		using var client = CreateClient(
			_factory, "CounterClerk", "stock-clerk");

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm(ProductSku, 3, token: null));

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		var stocks = (await LoadSnapshotAsync()).BranchStocks;
		Assert.Equal(4, Assert.Single(
			stocks,
			stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
		Assert.Equal(20, Assert.Single(
			stocks,
			stock => stock.BranchCode == new BranchCode("BR02")).Quantity);
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task Adjust_RejectsInactiveOrUnknownManager(
		bool inactive,
		bool unknown)
	{
		var manager = CreateManager();
		if (inactive)
			manager.Deactivate();
		await SeedAsync([manager]);
		using var client = CreateClient(
			_factory,
			"BranchManager",
			unknown ? "unknown-manager" : ManagerId);

		var response = await client.GetAsync("/Stock/Adjust");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Adjust_RequiresAntiforgeryToken()
	{
		await SeedAsync([CreateManager()]);
		using var client = CreateClient(
			_factory, "BranchManager", ManagerId);

		var response = await client.PostAsync(
			"/Stock/Adjust",
			CreateForm(ProductSku, 1, token: null));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(4, Assert.Single(
			(await LoadSnapshotAsync()).BranchStocks,
			stock => stock.BranchCode == new BranchCode("BR01")).Quantity);
	}

	private async Task SeedAsync(
		IEnumerable<Staff> staff,
		bool includeHomeStock = true,
		int homeStockQuantity = 4) =>
		await SeedAsync(
			_factory, staff, includeHomeStock, homeStockQuantity);

	private static Task SeedAsync(
		WebApplicationFactory<Program> factory,
		IEnumerable<Staff> staff,
		bool includeHomeStock = true,
		int homeStockQuantity = 4)
	{
		var branchOne = CreateBranch("BR01");
		var branchTwo = CreateBranch("BR02");
		var product = new Product(
			new Sku(ProductSku),
			"Box",
			ProductCategory.Packaging,
			new Money(10m, "INR"),
			TaxCategory.Standard);
		var stocks = new List<BranchStock>
		{
			new(new BranchCode("BR02"), product.Sku, 20)
		};
		if (includeHomeStock)
			stocks.Insert(
				0,
				new BranchStock(
					new BranchCode("BR01"),
					product.Sku,
					homeStockQuantity));

		return factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[branchOne, branchTwo],
				staff,
				[product],
				stocks,
				[],
				[],
				[],
				[],
				[]));
	}

	private async Task<ApexPostSnapshot> LoadSnapshotAsync() =>
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.LoadAsync();

	private static WebApplicationFactory<Program> CreateFactory(
		string storageDirectory,
		string auditFilePath) =>
		new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting("Storage:Directory", storageDirectory);
				builder.UseSetting(
					"Authentication:CredentialFile",
					Path.Combine(storageDirectory, "credentials.json"));
				builder.UseSetting("Audit:FilePath", auditFilePath);
				builder.ConfigureTestServices(services =>
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme =
							StockTestAuthenticationHandler.SchemeName;
						options.DefaultChallengeScheme =
							StockTestAuthenticationHandler.SchemeName;
						options.DefaultForbidScheme =
							StockTestAuthenticationHandler.SchemeName;
					}).AddScheme<AuthenticationSchemeOptions,
						StockTestAuthenticationHandler>(
						StockTestAuthenticationHandler.SchemeName,
						_ => { }));
			});

	private static HttpClient CreateClient(
		WebApplicationFactory<Program> factory,
		string? role,
		string? staffId,
		string? branchCode = null)
	{
		var client = factory.CreateClient(
			new WebApplicationFactoryClientOptions
			{
				AllowAutoRedirect = false
			});
		if (role is not null)
			client.DefaultRequestHeaders.Add("X-Stock-Role", role);
		if (staffId is not null)
			client.DefaultRequestHeaders.Add("X-Stock-StaffId", staffId);
		if (branchCode is not null)
			client.DefaultRequestHeaders.Add("X-Stock-BranchCode", branchCode);
		return client;
	}

	private static async Task<string> GetAntiforgeryTokenAsync(
		HttpClient client)
	{
		var response = await client.GetAsync("/Stock/Adjust");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		var token = Regex.Match(
			html,
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
			.Groups[1].Value;
		Assert.False(string.IsNullOrWhiteSpace(token));
		return token;
	}

	private static FormUrlEncodedContent CreateForm(
		string productSku,
		int quantityChange,
		string? token,
		string? branchCode = null)
	{
		var values = new List<KeyValuePair<string, string>>
		{
			new("ProductSku", productSku),
			new(
				"QuantityChange",
				quantityChange.ToString(
					System.Globalization.CultureInfo.InvariantCulture))
		};
		if (token is not null)
			values.Add(new("__RequestVerificationToken", token));
		if (branchCode is not null)
			values.Add(new("BranchCode", branchCode));
		return new FormUrlEncodedContent(values);
	}

	private static Branch CreateBranch(string branchCode)
	{
		var code = new BranchCode(branchCode);
		return new Branch(
			code,
			$"Branch {branchCode}",
			"Test Region",
			"09:00-17:00",
			[new CounterTerminal($"COUNTER-{branchCode}", "Counter")]);
	}

	private static Staff CreateManager() =>
		new(
			ManagerId,
			"Test Manager",
			"manager@example.test",
			StaffRole.BranchManager,
			new BranchCode("BR01"));

	private static string CreateStorageDirectory()
	{
		var path = Path.Combine(
			Path.GetTempPath(), $"ApexPost-Stock-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteStorageDirectory(string path)
	{
		if (Directory.Exists(path))
			Directory.Delete(path, recursive: true);
	}
}

public sealed class StockTestAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(
		options, logger, encoder)
{
	public const string SchemeName = "StockTest";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var role = Request.Headers["X-Stock-Role"].ToString();
		if (string.IsNullOrWhiteSpace(role))
			return Task.FromResult(AuthenticateResult.NoResult());

		var claims = new List<Claim>
		{
			new(ClaimTypes.Role, role)
		};
		var staffId = Request.Headers["X-Stock-StaffId"].ToString();
		if (!string.IsNullOrWhiteSpace(staffId))
			claims.Add(new Claim("StaffId", staffId));

		var branchCode = Request.Headers["X-Stock-BranchCode"].ToString();
		if (!string.IsNullOrWhiteSpace(branchCode))
			claims.Add(new Claim("BranchCode", branchCode));

		return Task.FromResult(AuthenticateResult.Success(
			new AuthenticationTicket(
				new ClaimsPrincipal(
					new ClaimsIdentity(claims, SchemeName)),
				SchemeName)));
	}
}
