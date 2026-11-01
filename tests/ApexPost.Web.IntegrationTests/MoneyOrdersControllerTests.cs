using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApexPost.Web.IntegrationTests;

// Money orders are issued as lines of a counter sale (POST /Transactions/Create)
// and listed on /MoneyOrders/Index. The simulated verifier accepts only the
// configured document types, so its decision is deterministic here.
public sealed class MoneyOrdersControllerTests : IAsyncLifetime
{
	private const string StaffId = MoneyOrdersControllerTestsStaff.Id;
	private const string FullDocumentIdentifier = "DOC-SECRET-987654";
	private const string AcceptedDocumentType = "Passport";
	private string _storageDirectory = null!;
	private WebApplicationFactory<Program> _factory = null!;

	private string DataFilePath =>
		Path.Combine(_storageDirectory, "apexpost.json");

	public Task InitializeAsync()
	{
		_storageDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-MoneyOrders-{Guid.NewGuid():N}");
		_factory = CreateFactory(verificationLatencyMilliseconds: 0);
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		if (Directory.Exists(_storageDirectory))
			Directory.Delete(_storageDirectory, recursive: true);
	}

	[Fact]
	public async Task MoneyOrderList_RequiresBranchManager()
	{
		using var anonymousClient = CreateClient(null);
		Assert.Equal(
			HttpStatusCode.Unauthorized,
			(await anonymousClient.GetAsync("/MoneyOrders/Index")).StatusCode);

		using var clerkClient = CreateClient("CounterClerk");
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await clerkClient.GetAsync("/MoneyOrders/Index")).StatusCode);
	}

	[Fact]
	public async Task StandaloneMoneyOrderForm_NoLongerExists()
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");

		Assert.Equal(
			HttpStatusCode.NotFound,
			(await client.GetAsync("/MoneyOrders/Create")).StatusCode);
	}

	[Fact]
	public async Task Index_ShowsOnlyKnownOrdersFromAuthenticatedManagersPersistedBranch()
	{
		await SeedAsync(
		[
			CreateMoneyOrder("Local Sender", "BR01"),
			CreateMoneyOrder("Other Branch Sender", "BR02"),
			CreateMoneyOrder("Legacy Sender", null)
		]);
		using var client = CreateClient("BranchManager");
		client.DefaultRequestHeaders.Add("X-Money-Order-Branch", "BR02");

		var response = await client.GetAsync("/MoneyOrders/Index");
		var content = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Local Sender", content, StringComparison.Ordinal);
		Assert.DoesNotContain("Other Branch Sender", content, StringComparison.Ordinal);
		Assert.DoesNotContain("Legacy Sender", content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Index_ShowsOnlyTheMaskedDocumentNumber()
	{
		await SeedAsync(
		[
			new MoneyOrder(
				new Money(1500m, "INR"),
				"Verified Sender",
				"Recipient",
				new Money(25m, "INR"),
				new IdentityVerification(
					AcceptedDocumentType, FullDocumentIdentifier, true),
				new BranchCode("BR01"))
		]);
		using var client = CreateClient("BranchManager");

		var content = await (await client.GetAsync("/MoneyOrders/Index"))
			.Content.ReadAsStringAsync();

		Assert.Contains("****7654", content, StringComparison.Ordinal);
		Assert.DoesNotContain(FullDocumentIdentifier, content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Sale_LowValueMoneyOrderIsIssuedAsABasketLineWithConfiguredFee()
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(transactionId, token, 500m));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var snapshot = await LoadSnapshotAsync();
		var order = Assert.Single(snapshot.MoneyOrders);
		Assert.Equal(new Money(500m, "INR"), order.Amount);
		Assert.Equal(new Money(25m, "INR"), order.Fee);
		Assert.Null(order.IdentityVerification);
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(transactionId, basket.Id);
		Assert.Equal(order.Id, Assert.Single(
			basket.LineItems,
			line => line.Type == TransactionItemType.MoneyOrder).MoneyOrder!.Id);
		Assert.Equal(new Money(525m, "INR"), basket.Total);
	}

	[Fact]
	public async Task Sale_SubmittedBranchCannotOverridePersistedHomeBranch()
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(transactionId, token, 500m, branchCode: "BR02"));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(
			new BranchCode("BR01"),
			Assert.Single((await LoadSnapshotAsync()).MoneyOrders)
				.ProcessingBranchCode);
	}

	[Fact]
	public async Task Sale_HighValueMoneyOrderWithAcceptedDocumentStoresOnlyMaskedNumber()
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(
				transactionId, token, 1500m, AcceptedDocumentType));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		var verification = Assert.Single(
			(await LoadSnapshotAsync()).IdentityVerifications);
		Assert.True(verification.IsSuccessful);
		Assert.Equal("****7654", verification.MaskedDocumentIdentifier);
		Assert.DoesNotContain(
			FullDocumentIdentifier,
			await TestSqlServer.ReadMoneyOrderTextAsync(),
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Library card")]
	[InlineData(null)]
	public async Task Sale_HighValueMoneyOrderWithoutSuccessfulVerificationIsRejected(
		string? documentType)
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(transactionId, token, 1500m, documentType));

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		var snapshot = await LoadSnapshotAsync();
		Assert.Empty(snapshot.MoneyOrders);
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Empty(snapshot.IdentityVerifications);
		// The redisplayed form never echoes the full document number.
		Assert.DoesNotContain(
			FullDocumentIdentifier,
			await response.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Sale_FailedCheckBelowThresholdIsIssuedWithFailedVerification()
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(transactionId, token, 500m, "Library card"));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.False(Assert.Single((await LoadSnapshotAsync()).MoneyOrders)
			.IdentityVerification!.IsSuccessful);
	}

	[Fact]
	public async Task Sale_VerificationTimeoutReturns503WithoutIssuing()
	{
		await using var slowFactory = CreateFactory(
			verificationLatencyMilliseconds: 5000,
			timeoutSeconds: "0.05");
		await SeedAsync(factory: slowFactory);
		using var client = CreateClient("BranchManager", slowFactory);
		var (transactionId, token) = await GetCreateFormAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(
				transactionId, token, 1500m, AcceptedDocumentType));

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.Empty((await LoadSnapshotAsync(slowFactory)).MoneyOrders);
	}

	[Fact]
	public async Task Sale_RequiresAntiforgeryToken()
	{
		await SeedAsync();
		using var client = CreateClient("BranchManager");

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateSaleForm(Guid.NewGuid(), token: null, 500m));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty((await LoadSnapshotAsync()).MoneyOrders);
	}

	private WebApplicationFactory<Program> CreateFactory(
		int verificationLatencyMilliseconds,
		string timeoutSeconds = "5") =>
		new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting("Storage:Directory", _storageDirectory);
				builder.UseSetting(
					"Authentication:CredentialFile",
					Path.Combine(_storageDirectory, "credentials.json"));
				builder.UseSetting(
					"Audit:FilePath",
					Path.Combine(_storageDirectory, "audit-events.jsonl"));
				builder.UseSetting("Transactions:TaxRates:Standard", "0.1");
				builder.UseSetting("Transactions:TaxRates:ZeroRated", "0");
				builder.UseSetting("Transactions:PaymentMethods:0", "Cash");
				builder.UseSetting(
					"MoneyOrders:HighValueThreshold:Amount", "1000");
				builder.UseSetting(
					"MoneyOrders:HighValueThreshold:Currency", "INR");
				builder.UseSetting("MoneyOrders:Fee:Amount", "25");
				builder.UseSetting("MoneyOrders:Fee:TaxCategory", "ZeroRated");
				builder.UseSetting(
					"MoneyOrders:Verification:AcceptedDocumentTypes:0",
					AcceptedDocumentType);
				builder.UseSetting(
					"MoneyOrders:Verification:TimeoutSeconds", timeoutSeconds);
				builder.UseSetting(
					"MoneyOrders:Verification:SimulatedLatencyMilliseconds",
					verificationLatencyMilliseconds.ToString(
						System.Globalization.CultureInfo.InvariantCulture));
				builder.ConfigureTestServices(services =>
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme =
							MoneyOrderTestAuthenticationHandler.AuthenticationSchemeName;
						options.DefaultChallengeScheme =
							MoneyOrderTestAuthenticationHandler.AuthenticationSchemeName;
						options.DefaultForbidScheme =
							MoneyOrderTestAuthenticationHandler.AuthenticationSchemeName;
					}).AddScheme<AuthenticationSchemeOptions,
						MoneyOrderTestAuthenticationHandler>(
						MoneyOrderTestAuthenticationHandler.AuthenticationSchemeName,
						_ => { }));
			});

	private HttpClient CreateClient(
		string? role,
		WebApplicationFactory<Program>? factory = null)
	{
		var client = (factory ?? _factory).CreateClient(
			new WebApplicationFactoryClientOptions
			{
				AllowAutoRedirect = false
			});
		if (role is not null)
			client.DefaultRequestHeaders.Add("X-Money-Order-Role", role);
		return client;
	}

	private Task SeedAsync(
		IEnumerable<MoneyOrder>? moneyOrders = null,
		WebApplicationFactory<Program>? factory = null) =>
		(factory ?? _factory).Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[
					new Branch(
						new BranchCode("BR01"),
						"Test Branch",
						"Region",
						"09:00-17:00",
						[new CounterTerminal("COUNTER-1", "Counter")])
				],
				[
					new Staff(
						StaffId,
						"Test Manager",
						"money-order-manager@example.test",
						StaffRole.BranchManager,
						new BranchCode("BR01"))
				],
				[],
				[],
				[],
				[],
				moneyOrders ?? [],
				[],
				[]));

	private async Task<ApexPostSnapshot> LoadSnapshotAsync(
		WebApplicationFactory<Program>? factory = null) =>
		await (factory ?? _factory).Services
			.GetRequiredService<IApexPostRepository>()
			.LoadAsync();

	private static async Task<(Guid TransactionId, string Token)> GetCreateFormAsync(
		HttpClient client)
	{
		var response = await client.GetAsync("/Transactions/Create");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		var token = Regex.Match(
			html,
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
			.Groups[1].Value;
		Assert.False(string.IsNullOrWhiteSpace(token));
		var transactionId = Regex.Match(
			html,
			"name=\"TransactionId\"[^>]*value=\"([A-Fa-f0-9-]+)\"")
			.Groups[1].Value;
		return (Guid.Parse(transactionId), token);
	}

	private static FormUrlEncodedContent CreateSaleForm(
		Guid transactionId,
		string? token,
		decimal amount,
		string? documentType = null,
		string? branchCode = null)
	{
		var values = new List<KeyValuePair<string, string>>
		{
			new("TransactionId", transactionId.ToString()),
			new("CounterTerminalId", "COUNTER-1"),
			new("PaymentMethod", "Cash"),
			new("IncludeMoneyOrder", "true"),
			new("MoneyOrderAmount", amount.ToString(
				System.Globalization.CultureInfo.InvariantCulture)),
			new("MoneyOrderSender", "Test Sender"),
			new("MoneyOrderRecipient", "Test Recipient")
		};
		if (documentType is not null)
		{
			values.Add(new("MoneyOrderDocumentType", documentType));
			values.Add(new("MoneyOrderDocumentIdentifier", FullDocumentIdentifier));
		}

		if (token is not null)
			values.Add(new("__RequestVerificationToken", token));
		if (branchCode is not null)
			values.Add(new("BranchCode", branchCode));

		return new FormUrlEncodedContent(values);
	}

	private static MoneyOrder CreateMoneyOrder(
		string sender,
		string? branchCode) =>
		new(
			new Money(500m, "INR"),
			sender,
			"Recipient",
			new Money(10m, "INR"),
			processingBranchCode: branchCode is null
				? null
				: new BranchCode(branchCode));
}

public sealed class MoneyOrderTestAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(
		options, logger, encoder)
{
	public const string AuthenticationSchemeName = "MoneyOrderTest";

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var role = Request.Headers["X-Money-Order-Role"].ToString();
		if (string.IsNullOrWhiteSpace(role))
			return Task.FromResult(AuthenticateResult.NoResult());

		var identity = new ClaimsIdentity(
			[
				new Claim("StaffId", MoneyOrdersControllerTestsStaff.Id),
				new Claim(ClaimTypes.Role, role)
			],
			AuthenticationSchemeName);
		return Task.FromResult(AuthenticateResult.Success(
			new AuthenticationTicket(
				new ClaimsPrincipal(identity), AuthenticationSchemeName)));
	}
}

internal static class MoneyOrdersControllerTestsStaff
{
	public const string Id = "money-order-manager";
}
