using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.Notifications;
using ApexPost.Domain.ValueObjects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApexPost.Web.IntegrationTests;

public sealed partial class TransactionsControllerTests : IAsyncLifetime
{
	private string _storageDirectory = null!;
	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_storageDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-Web-{Guid.NewGuid():N}");
		_factory = new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting("Storage:Directory", _storageDirectory);
				builder.UseSetting("Transactions:TaxRates:Standard", "0.1");
				builder.UseSetting("Shipping:TaxCategory", "Standard");
				builder.UseSetting("Transactions:PaymentMethods:0", "Cash");
				builder.UseSetting(
					"Shipping:MaximumParcelWeightKilograms:Standard", "5");
				builder.ConfigureTestServices(services =>
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestScheme;
						options.DefaultChallengeScheme = TestAuthenticationHandler.TestScheme;
						options.DefaultForbidScheme = TestAuthenticationHandler.TestScheme;
					}).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
						TestAuthenticationHandler.TestScheme, _ => { }));
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
	public async Task Index_RequiresBranchManagerRole()
	{
		var client = _factory.CreateClient();

		var anonymousResponse = await client.GetAsync("/Transactions");
		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			anonymousResponse.StatusCode);

		client.DefaultRequestHeaders.Add("X-Test-Role", "CounterClerk");
		var clerkResponse = await client.GetAsync("/Transactions");
		Assert.Equal(System.Net.HttpStatusCode.Forbidden, clerkResponse.StatusCode);
	}

	[Fact]
	public async Task SignIn_RejectsPostWithoutAntiforgeryToken()
	{
		var storageDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-Web-NoAuth-{Guid.NewGuid():N}");
		await using var factory = new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting("Storage:Directory", storageDirectory);
			});
		try
		{
			var client = factory.CreateClient(
				new WebApplicationFactoryClientOptions
				{
					AllowAutoRedirect = false
				});
			var signInResponse = await client.GetAsync("/Account/SignIn");
			Assert.Equal(System.Net.HttpStatusCode.OK, signInResponse.StatusCode);
			var signInHtml = await signInResponse.Content.ReadAsStringAsync();
			Assert.Contains("name=\"StaffId\"", signInHtml);
			Assert.Contains("name=\"Password\"", signInHtml);

			var submittedIdentity = await client.PostAsync(
				"/Account/SignIn",
				new FormUrlEncodedContent(
				[
					new KeyValuePair<string, string>("staffId", "manager-1"),
					new KeyValuePair<string, string>(
						"password", "valid-test-password"),
					new KeyValuePair<string, string>(
						"returnUrl", "/Transactions"),
					new KeyValuePair<string, string>("role", "BranchManager")
				]));
			Assert.Equal(
				System.Net.HttpStatusCode.BadRequest,
				submittedIdentity.StatusCode);
			Assert.Empty(await submittedIdentity.Content.ReadAsStringAsync());

			var transactionResponse = await client.GetAsync("/Transactions");
			Assert.Equal(
				System.Net.HttpStatusCode.Redirect,
				transactionResponse.StatusCode);
			Assert.StartsWith(
				"/Account/SignIn",
				transactionResponse.Headers.Location?.AbsolutePath);
		}
		finally
		{
			if (Directory.Exists(storageDirectory))
				Directory.Delete(storageDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task SignOut_RequiresAntiforgeryTokenAndClearsCookie()
	{
		await SeedManagerAndBranchAsync();
		var client = _factory.CreateClient(
			new WebApplicationFactoryClientOptions
			{
				AllowAutoRedirect = false
			});
		client.DefaultRequestHeaders.Add("X-Test-Role", "BranchManager");
		var page = await client.GetAsync("/Transactions");
		Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
		var html = await page.Content.ReadAsStringAsync();
		var token = Regex.Match(
			html,
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
			.Groups[1].Value;
		Assert.False(string.IsNullOrWhiteSpace(token));

		var invalidRequest = await client.PostAsync(
			"/Account/SignOut",
			new FormUrlEncodedContent([]));
		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest,
			invalidRequest.StatusCode);

		var signOut = await client.PostAsync(
			"/Account/SignOut",
			new FormUrlEncodedContent(
			[
				new KeyValuePair<string, string>(
					"__RequestVerificationToken", token)
			]));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, signOut.StatusCode);
		Assert.StartsWith(
			"/Account/SignIn",
			signOut.Headers.Location?.OriginalString);
		Assert.Contains(
			signOut.Headers.GetValues("Set-Cookie"),
			cookie => cookie.Contains(
				".AspNetCore.Cookies=",
				StringComparison.Ordinal) &&
				cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task IndexAndDetails_ShowPersistedTransactionForManager()
	{
		var transactionId = Guid.NewGuid();
		var basket = TransactionBasket.Reconstitute(
			transactionId,
			"INR",
			new Dictionary<TaxCategory, decimal>
			{
				[TaxCategory.Standard] = 0.1m
			},
			[
				new TransactionLineItem(
					"Counter service",
					TransactionItemType.Service,
					new Money(100m, "INR"),
					1,
					TaxCategory.Standard)
			],
			TransactionPaymentStatus.Paid,
			[],
			new BranchCode("BR01"));
		var branch = CreateBranch("BR01");
		var manager = CreateStaff(
			"manager-1", StaffRole.BranchManager, "BR01");
		var repository = _factory.Services.GetRequiredService<IApexPostRepository>();
		await repository.SaveAsync(new ApexPostSnapshot(
			[branch], [manager], [], [], [], [], [], [], [basket]));

		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", "BranchManager");

		var listResponse = await client.GetAsync("/Transactions");
		Assert.Equal(System.Net.HttpStatusCode.OK, listResponse.StatusCode);
		var listContent = await listResponse.Content.ReadAsStringAsync();
		Assert.Contains(transactionId.ToString(), listContent);
		Assert.Contains("110", listContent);

		var detailsResponse = await client.GetAsync(
			$"/Transactions/{transactionId}");
		Assert.Equal(System.Net.HttpStatusCode.OK, detailsResponse.StatusCode);
		var detailsContent = await detailsResponse.Content.ReadAsStringAsync();
		Assert.Contains("Counter service", detailsContent);
		Assert.Contains("100", detailsContent);
		Assert.Contains("10", detailsContent);
	}

	[Fact]
	public async Task Details_WhenTransactionDoesNotExist_ReturnsNotFound()
	{
		await SeedManagerAndBranchAsync();
		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-Test-Role", "BranchManager");

		var response = await client.GetAsync($"/Transactions/{Guid.NewGuid()}");

		Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task CreateTransactionPage_RequiresAuthenticatedStaff()
	{
		var client = _factory.CreateClient();

		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			(await client.GetAsync("/Transactions/Create")).StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			(await client.PostAsync(
				"/Transactions/Create",
				new FormUrlEncodedContent([]))).StatusCode);

		client.DefaultRequestHeaders.Add("X-Test-Role", "CounterClerk");
		Assert.Equal(
			System.Net.HttpStatusCode.Forbidden,
			(await client.GetAsync("/Transactions/Create")).StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest,
			(await client.PostAsync(
				"/Transactions/Create",
				new FormUrlEncodedContent([]))).StatusCode);
	}

	[Fact]
	public async Task CreateTransaction_CounterClerkSaleUsesPersistedHomeBranch()
	{
		await SeedMixedBasketDataAsync(StaffRole.CounterClerk);
		using var client = CreateStaffClient(
			"CounterClerk", "manager-1", "BR02");
		var (transactionId, token) =
			await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create?branchCode=BR02",
			CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(
			$"/Transactions/{transactionId}",
			response.Headers.Location?.OriginalString);
		var snapshot = await GetRepositoryAsync();
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(new BranchCode("BR01"), basket.BranchCode);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Theory]
	[InlineData("CounterClerk", "manager-1", true)]
	[InlineData("CounterClerk", "unknown-clerk", false)]
	public async Task CreateTransaction_InactiveOrUnknownClerkIsForbidden(
		string role,
		string staffId,
		bool inactive)
	{
		await SeedMixedBasketDataAsync(
			StaffRole.CounterClerk,
			inactiveStaff: inactive);
		using var client = CreateStaffClient(role, staffId);

		var response = await client.GetAsync("/Transactions/Create");

		Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task CreateTransaction_ClosedBranchCannotAcceptSale()
	{
		await SeedMixedBasketDataAsync(closeBranch: true);
		using var client = CreateManagerClient();

		var response = await client.GetAsync("/Transactions/Create");

		Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
		Assert.Empty((await GetRepositoryAsync()).TransactionBaskets);
		Assert.Equal(4, Assert.Single(
			(await GetRepositoryAsync()).BranchStocks).Quantity);
	}

	[Fact]
	public async Task TransactionListAndDetails_AreScopedToAuthoritativeStaffBranch()
	{
		var (localId, foreignOpenId, foreignPaidId, legacyId) =
			await SeedTransactionsForTwoBranchesAsync();
		using var client = CreateManagerClient();

		var listResponse = await client.GetAsync("/Transactions");
		Assert.Equal(System.Net.HttpStatusCode.OK, listResponse.StatusCode);
		var listContent = await listResponse.Content.ReadAsStringAsync();
		Assert.Contains(localId.ToString(), listContent);
		Assert.DoesNotContain(foreignOpenId.ToString(), listContent);
		Assert.DoesNotContain(foreignPaidId.ToString(), listContent);
		Assert.DoesNotContain(legacyId.ToString(), listContent);

		foreach (var inaccessibleId in new[]
			{ foreignOpenId, foreignPaidId, legacyId, Guid.NewGuid() })
		{
			var response = await client.GetAsync(
				$"/Transactions/{inaccessibleId}");
			Assert.Equal(
				System.Net.HttpStatusCode.NotFound, response.StatusCode);
			Assert.DoesNotContain(
				"Foreign branch transaction",
				await response.Content.ReadAsStringAsync(),
				StringComparison.Ordinal);
		}

		var localDetails = await client.GetAsync($"/Transactions/{localId}");
		Assert.Equal(System.Net.HttpStatusCode.OK, localDetails.StatusCode);
	}

	[Theory]
	[InlineData("void", TransactionPaymentStatus.Open)]
	[InlineData("refund", TransactionPaymentStatus.Paid)]
	public async Task StatusChange_CannotMutateAnotherBranchesTransaction(
		string action,
		TransactionPaymentStatus status)
	{
		var (localId, foreignOpenId, foreignPaidId, _) =
			await SeedTransactionsForTwoBranchesAsync();
		var foreignId = action == "void" ? foreignOpenId : foreignPaidId;
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, localId);

		var response = await client.PostAsync(
			$"/Transactions/{foreignId}/{action}",
			CreateActionForm("Cross-branch attempt", token));

		Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal(
			status,
			Assert.Single(
				(await GetRepositoryAsync()).TransactionBaskets,
				basket => basket.Id == foreignId).PaymentStatus);
	}

	[Fact]
	public async Task TransactionAccess_RejectsInactivePersistedStaff()
	{
		var manager = CreateStaff(
			"manager-1", StaffRole.BranchManager, "BR01");
		manager.Deactivate();
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[CreateBranch("BR01")],
				[manager],
				[], [], [], [], [], [], []));
		using var client = CreateManagerClient();

		Assert.Equal(
			System.Net.HttpStatusCode.Forbidden,
			(await client.GetAsync("/Transactions")).StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.Forbidden,
			(await client.GetAsync("/Transactions/Create")).StatusCode);
	}

	[Fact]
	public async Task TransactionAccess_DoesNotTrustManagerRoleClaimWithoutPersistedManagerRole()
	{
		var branch = CreateBranch("BR01");
		var staff = CreateStaff(
			"manager-1", StaffRole.CounterClerk, "BR01");
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[branch], [staff], [], [], [], [], [], [], []));
		using var client = CreateManagerClient();

		Assert.Equal(
			System.Net.HttpStatusCode.Forbidden,
			(await client.GetAsync("/Transactions")).StatusCode);
	}

	[Fact]
	public async Task CreateTransaction_MixedBasketProcessesAndPersistsBranchAndShipment()
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) =
			await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(
			$"/Transactions/{transactionId}",
			response.Headers.Location?.OriginalString);
		var snapshot = await GetRepositoryAsync();
		var basket = Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus);
		Assert.Equal(new BranchCode("BR01"), basket.BranchCode);
		Assert.Equal(new Money(61.6m, "INR"), basket.Total);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Single(snapshot.ShippingItems);
		var persistedShippingItem = Assert.Single(snapshot.ShippingItems);
		var transactionShippingItem = basket.LineItems.Single(
			item => item.ShippingItem is not null).ShippingItem!;
		Assert.Equal(persistedShippingItem.Id, transactionShippingItem.Id);
		Assert.Equal(
			persistedShippingItem.Weight, transactionShippingItem.Weight);
		Assert.Equal(
			persistedShippingItem.DestinationZone,
			transactionShippingItem.DestinationZone);
	}

	[Theory]
	[InlineData("6", "2")]
	[InlineData("3", "5")]
	public async Task CreateTransaction_InvalidParcelOrInsufficientStockIsNotPersisted(
		string weight,
		string quantity)
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) =
			await GetCreateFormIdentityAsync(client);
		var form = CreateMixedBasketForm(
			transactionId, token, weightKilograms: weight, goodsQuantity: quantity);

		var response = await client.PostAsync("/Transactions/Create", form);

		Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
		var snapshot = await GetRepositoryAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Empty(snapshot.ShippingItems);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task CreateTransaction_InvalidMvcForm_RedisplaysViewAndValidationMessage()
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) =
			await GetCreateFormIdentityAsync(client);
		using var form = new FormUrlEncodedContent(
		[
			new("__RequestVerificationToken", token),
			new("TransactionId", transactionId.ToString()),
			new("IncludeShipping", "false")
		]);

		var response = await client.PostAsync(
			"/Transactions/Create", form);

		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		Assert.Contains("<h1>Create transaction</h1>", html, StringComparison.Ordinal);
		Assert.Contains(
			"Add at least one goods item, shipping service, or money order.",
			html,
			StringComparison.Ordinal);
		Assert.Empty((await GetRepositoryAsync()).TransactionBaskets);
		Assert.Equal(4, Assert.Single(
			(await GetRepositoryAsync()).BranchStocks).Quantity);
	}

	[Fact]
	public async Task CreateTransaction_RejectsMixedCurrencies()
	{
		await SeedMixedBasketDataAsync(shippingRate: new ShippingRate(
			"ZONE-A",
			ShippingServiceLevel.Standard,
			new Money(10m, "USD"),
			new Money(2m, "USD")));
		using var client = CreateManagerClient();
		var (transactionId, token) =
			await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
		Assert.Empty((await GetRepositoryAsync()).TransactionBaskets);
	}

	[Fact]
	public async Task CreateTransaction_RejectsRepeatedSubmissionWithoutSecondDeduction()
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) =
			await GetCreateFormIdentityAsync(client);
		var formFields = CreateMixedBasketForm(transactionId, token);

		var firstResponse = await client.PostAsync(
			"/Transactions/Create", formFields);
		Assert.Equal(
			System.Net.HttpStatusCode.Redirect, firstResponse.StatusCode);

		var duplicateResponse = await client.PostAsync(
			"/Transactions/Create",
			CreateMixedBasketForm(transactionId, token));

		Assert.Equal(
			System.Net.HttpStatusCode.Conflict, duplicateResponse.StatusCode);
		var snapshot = await GetRepositoryAsync();
		Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(2, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Single(snapshot.ShippingItems);
	}

	[Fact]
	public async Task CreateTransaction_RequiresAntiforgeryToken()
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, _) =
			await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create",
			CreateMixedBasketForm(transactionId, token: null));

		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty((await GetRepositoryAsync()).TransactionBaskets);
	}

	[Theory]
	[InlineData("void")]
	[InlineData("refund")]
	public async Task StatusChange_RequiresBranchManager(string action)
	{
		var transactionId = await SeedTransactionAsync(
			action == "refund"
				? TransactionPaymentStatus.Paid
				: TransactionPaymentStatus.Open);
		using var client = _factory.CreateClient(
			new WebApplicationFactoryClientOptions
			{
				AllowAutoRedirect = false
			});

		var anonymousResponse = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			CreateActionForm("Valid reason"));
		Assert.Equal(
			System.Net.HttpStatusCode.Unauthorized,
			anonymousResponse.StatusCode);

		client.DefaultRequestHeaders.Add("X-Test-Role", "CounterClerk");
		var clerkResponse = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			CreateActionForm("Valid reason"));
		Assert.Equal(
			System.Net.HttpStatusCode.Forbidden,
			clerkResponse.StatusCode);
	}

	[Theory]
	[InlineData("void", "")]
	[InlineData("refund", "")]
	[InlineData("void", " ")]
	public async Task StatusChange_RejectsMissingOrInvalidReason(
		string action,
		string reason)
	{
		var transactionId = await SeedTransactionAsync(
			action == "refund"
				? TransactionPaymentStatus.Paid
				: TransactionPaymentStatus.Open);
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, transactionId);

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			CreateActionForm(reason, token));

		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest,
			response.StatusCode);
		Assert.Contains(
			"Enter a reason",
			await response.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("void", TransactionPaymentStatus.Open)]
	[InlineData("void", TransactionPaymentStatus.Paid)]
	[InlineData("refund", TransactionPaymentStatus.Paid)]
	public async Task StatusChange_ManagerRequestPersistsTransitionAndRedirects(
		string action,
		TransactionPaymentStatus initialStatus)
	{
		var transactionId = await SeedTransactionAsync(initialStatus);
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, transactionId);
		const string reason = "Customer requested this transaction change";

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			CreateActionForm(reason, token));

		Assert.Equal(
			System.Net.HttpStatusCode.Redirect,
			response.StatusCode);
		Assert.Equal(
			$"/Transactions/{transactionId}",
			response.Headers.Location?.OriginalString);
		var detailsResponse = await client.GetAsync(
			response.Headers.Location);
		Assert.Contains(
			action == "void" ? "Transaction voided." : "Transaction refunded.",
			await detailsResponse.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
		var basket = Assert.Single(
			(await GetRepositoryAsync()).TransactionBaskets,
			item => item.Id == transactionId);
		Assert.Equal(
			action == "void"
				? TransactionPaymentStatus.Voided
				: TransactionPaymentStatus.Refunded,
			basket.PaymentStatus);
		Assert.Equal(
			reason,
			action == "void"
				? Assert.IsType<TransactionVoidedNotification>(
					Assert.Single(basket.Notifications)).Reason
				: Assert.IsType<TransactionRefundedNotification>(
					Assert.Single(basket.Notifications)).Reason);
	}

	[Theory]
	[InlineData("void")]
	[InlineData("refund")]
	public async Task StatusChange_NonexistentTransactionReturnsNotFound(
		string action)
	{
		var existingId = await SeedTransactionAsync(
			TransactionPaymentStatus.Open);
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, existingId);

		var response = await client.PostAsync(
			$"/Transactions/{Guid.NewGuid()}/{action}",
			CreateActionForm("Valid reason", token));

		Assert.Equal(
			System.Net.HttpStatusCode.NotFound,
			response.StatusCode);
	}

	[Fact]
	public async Task StatusChange_RequiresAntiforgeryToken()
	{
		var transactionId = await SeedTransactionAsync(
			TransactionPaymentStatus.Open);
		using var client = CreateManagerClient();

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/void",
			CreateActionForm("Valid reason"));

		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest,
			response.StatusCode);
		Assert.Equal(
			TransactionPaymentStatus.Open,
			Assert.Single(
				(await GetRepositoryAsync()).TransactionBaskets,
				item => item.Id == transactionId).PaymentStatus);
	}

	[Fact]
	public async Task StatusChange_WithEmptyTransactionIdReturnsBadRequest()
	{
		var existingTransactionId = await SeedTransactionAsync(
			TransactionPaymentStatus.Open);
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(
			client, existingTransactionId);
		var response = await client.PostAsync(
			$"/Transactions/{Guid.Empty}/void",
			CreateActionForm("Valid reason", token));

		Assert.Equal(
			System.Net.HttpStatusCode.BadRequest,
			response.StatusCode);
	}

	[Theory]
	[InlineData("void", "void")]
	[InlineData("void", "refund")]
	[InlineData("refund", "void")]
	[InlineData("refund", "refund")]
	public async Task StatusChange_WhenAlreadyReversedReturnsConflict(
		string firstAction,
		string repeatedAction)
	{
		var transactionId = await SeedTransactionAsync(
			TransactionPaymentStatus.Paid);
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, transactionId);
		var firstResponse = await client.PostAsync(
			$"/Transactions/{transactionId}/{firstAction}",
			CreateActionForm("First request", token));
		Assert.Equal(
			System.Net.HttpStatusCode.Redirect,
			firstResponse.StatusCode);

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/{repeatedAction}",
			CreateActionForm("Repeated request", token));

		Assert.Equal(
			System.Net.HttpStatusCode.Conflict,
			response.StatusCode);
		Assert.Contains(
			"current state",
			await response.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
		var basket = Assert.Single(
			(await GetRepositoryAsync()).TransactionBaskets,
			item => item.Id == transactionId);
		Assert.Equal(
			firstAction == "void"
				? TransactionPaymentStatus.Voided
				: TransactionPaymentStatus.Refunded,
			basket.PaymentStatus);
		Assert.Single(basket.Notifications);
	}

	[Theory]
	[InlineData("void")]
	[InlineData("refund")]
	public async Task StatusChange_CompletedSaleReturnsStockOnceEndToEnd(
		string action)
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, createToken) =
			await GetCreateFormIdentityAsync(client);
		var createResponse = await client.PostAsync(
			"/Transactions/Create",
			CreateMixedBasketForm(transactionId, createToken));
		Assert.Equal(
			System.Net.HttpStatusCode.Redirect,
			createResponse.StatusCode);
		Assert.Equal(2, Assert.Single(
			(await GetRepositoryAsync()).BranchStocks).Quantity);
		var token = await GetAntiforgeryTokenAsync(client, transactionId);

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			CreateActionForm("Customer returned the goods", token));
		var repeatedResponse = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			CreateActionForm("Repeated request", token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(
			System.Net.HttpStatusCode.Conflict,
			repeatedResponse.StatusCode);
		var snapshot = await GetRepositoryAsync();
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
		Assert.Equal(
			action == "void"
				? TransactionPaymentStatus.Voided
				: TransactionPaymentStatus.Refunded,
			Assert.Single(snapshot.TransactionBaskets).PaymentStatus);
	}

	[Fact]
	public async Task Details_PaidSaleOffersBothVoidAndRefund()
	{
		var transactionId = await SeedTransactionAsync(
			TransactionPaymentStatus.Paid);
		using var client = CreateManagerClient();

		var html = await (await client.GetAsync(
			$"/Transactions/{transactionId}")).Content.ReadAsStringAsync();

		Assert.Contains($"/Transactions/{transactionId}/void", html,
			StringComparison.Ordinal);
		Assert.Contains($"/Transactions/{transactionId}/refund", html,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Details_ShowsAvailableManagerActionWithAntiforgeryForm()
	{
		var transactionId = await SeedTransactionAsync(
			TransactionPaymentStatus.Open);
		using var client = CreateManagerClient();

		var response = await client.GetAsync($"/Transactions/{transactionId}");
		var html = await response.Content.ReadAsStringAsync();

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Void transaction", html, StringComparison.Ordinal);
		Assert.Contains("name=\"__RequestVerificationToken\"", html,
			StringComparison.Ordinal);
		Assert.Contains($"/Transactions/{transactionId}/void", html,
			StringComparison.Ordinal);
	}

	private async Task SeedMixedBasketDataAsync(
		StaffRole staffRole = StaffRole.BranchManager,
		bool inactiveStaff = false,
		bool closeBranch = false,
		ShippingRate? shippingRate = null)
	{
		var branchCode = new BranchCode("BR01");
		var product = new Product(
			new Sku("BOX-01"),
			"Box",
			ProductCategory.Packaging,
			new Money(20m, "INR"),
			TaxCategory.Standard);
		var branch = new Branch(
			branchCode,
			"Test Branch",
			"Test Region",
			"09:00-17:00",
			[new CounterTerminal("COUNTER-1", "Counter")]);
		if (closeBranch)
			branch.Close();
		var manager = new Staff(
			"manager-1",
			"Test Manager",
			"manager@example.test",
			staffRole,
			branchCode);
		if (inactiveStaff)
			manager.Deactivate();
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[branch],
				[manager],
				[product],
				[new BranchStock(branchCode, product.Sku, 4)],
				[],
				[shippingRate ?? new ShippingRate(
					"ZONE-A",
					ShippingServiceLevel.Standard,
					new Money(10m, "INR"),
					new Money(2m, "INR"))],
				[],
				[],
				[]));
	}

	private async Task SeedManagerAndBranchAsync()
	{
		var branch = CreateBranch("BR01");
		var manager = CreateStaff(
			"manager-1", StaffRole.BranchManager, "BR01");
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[branch], [manager], [], [], [], [], [], [], []));
	}

	private async Task<(Guid LocalId, Guid ForeignOpenId,
		Guid ForeignPaidId, Guid LegacyId)> SeedTransactionsForTwoBranchesAsync()
	{
		var localId = Guid.NewGuid();
		var foreignOpenId = Guid.NewGuid();
		var foreignPaidId = Guid.NewGuid();
		var legacyId = Guid.NewGuid();
		var baskets = new[]
		{
			CreateBasket(localId, "BR01", TransactionPaymentStatus.Paid,
				"Local branch transaction"),
			CreateBasket(foreignOpenId, "BR02", TransactionPaymentStatus.Open,
				"Foreign branch transaction"),
			CreateBasket(foreignPaidId, "BR02", TransactionPaymentStatus.Paid,
				"Foreign branch transaction"),
			CreateBasket(legacyId, null, TransactionPaymentStatus.Paid,
				"Legacy branch transaction")
		};
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[CreateBranch("BR01"), CreateBranch("BR02")],
				[
					CreateStaff("manager-1", StaffRole.BranchManager, "BR01"),
					CreateStaff("manager-2", StaffRole.BranchManager, "BR02")
				],
				[], [], [], [], [], [], baskets));
		return (localId, foreignOpenId, foreignPaidId, legacyId);
	}

	private static async Task<(Guid TransactionId, string Token)>
		GetCreateFormIdentityAsync(HttpClient client)
	{
		var response = await client.GetAsync("/Transactions/Create");
		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
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
		Assert.True(Guid.TryParse(transactionId, out var parsedTransactionId));
		return (parsedTransactionId, token);
	}

	private static FormUrlEncodedContent CreateMixedBasketForm(
		Guid transactionId,
		string? token,
		string weightKilograms = "3",
		string goodsQuantity = "2")
	{
		var values = new List<KeyValuePair<string, string>>
		{
			new("TransactionId", transactionId.ToString()),
			new("BranchCode", "BR02"),
			new("CounterTerminalId", "COUNTER-1"),
			new("PaymentMethod", "Cash"),
			new("GoodsSku", "BOX-01"),
			new("GoodsQuantity", goodsQuantity),
			new("IncludeShipping", "true"),
			new("ShippingType", nameof(ShippingItemType.Parcel)),
			new("WeightKilograms", weightKilograms),
			new("LengthCm", "10"),
			new("WidthCm", "20"),
			new("HeightCm", "30"),
			new("DestinationZone", "ZONE-A"),
			new("ServiceLevel", nameof(ShippingServiceLevel.Standard))
		};
		if (token is not null)
		{
			values.Add(new(
				"__RequestVerificationToken", token));
		}

		return new FormUrlEncodedContent(values);
	}

	private HttpClient CreateManagerClient(string staffId = "manager-1") =>
		CreateStaffClient("BranchManager", staffId);

	private HttpClient CreateStaffClient(
		string role,
		string staffId,
		string? branchCode = null)
	{
		var client = _factory.CreateClient(
			new WebApplicationFactoryClientOptions
			{
				AllowAutoRedirect = false
			});
		client.DefaultRequestHeaders.Add(
			"X-Test-Role", role);
		client.DefaultRequestHeaders.Add("X-Test-StaffId", staffId);
		if (branchCode is not null)
			client.DefaultRequestHeaders.Add(
				"X-Test-BranchCode", branchCode);
		return client;
	}

	private async Task<Guid> SeedTransactionAsync(
		TransactionPaymentStatus status)
	{
		var transactionId = Guid.NewGuid();
		var taxRates = new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = 0m
		};
		var lineItems = status == TransactionPaymentStatus.Paid
			? new[]
			{
				new TransactionLineItem(
					"Counter service",
					TransactionItemType.Service,
					new Money(10m, "INR"),
					1,
					TaxCategory.Standard)
			}
			: [];
		var basket = TransactionBasket.Reconstitute(
			transactionId,
			"INR",
			taxRates,
			lineItems,
			status,
			[],
			new BranchCode("BR01"));
		var manager = CreateStaff(
			"manager-1", StaffRole.BranchManager, "BR01");
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[CreateBranch("BR01")],
				[manager],
				[], [], [], [], [], [], [basket]));
		return transactionId;
	}

	private async Task<ApexPostSnapshot> GetRepositoryAsync() =>
		await _factory.Services.GetRequiredService<IApexPostRepository>()
			.LoadAsync();

	private static async Task<string> GetAntiforgeryTokenAsync(
		HttpClient client,
		Guid transactionId)
	{
		var response = await client.GetAsync($"/Transactions/{transactionId}");
		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		var token = Regex.Match(
			html,
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
			.Groups[1].Value;
		Assert.False(string.IsNullOrWhiteSpace(token));
		return token;
	}

	private static FormUrlEncodedContent CreateActionForm(
		string reason,
		string? token = null)
	{
		var values = new List<KeyValuePair<string, string>>
		{
			new("Reason", reason)
		};
		if (token is not null)
		{
			values.Add(new(
				"__RequestVerificationToken", token));
		}

		return new FormUrlEncodedContent(values);
	}

	private sealed class TestAuthenticationHandler(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger,
		UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(
			options, logger, encoder)
	{
		public const string TestScheme = "Test";

		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			var role = Request.Headers["X-Test-Role"].ToString();
			if (string.IsNullOrWhiteSpace(role))
				return Task.FromResult(AuthenticateResult.NoResult());
			var staffId = Request.Headers["X-Test-StaffId"].ToString();
			if (string.IsNullOrWhiteSpace(staffId))
				staffId = "manager-1";

			var identity = new ClaimsIdentity(
				[
					new Claim(ClaimTypes.NameIdentifier, "not-the-staff-id"),
					new Claim("StaffId", staffId),
					new Claim("Role", role),
					new Claim(
						"BranchCode",
						Request.Headers["X-Test-BranchCode"].FirstOrDefault()
							?? "BR02"),
					new Claim(ClaimTypes.Role, role)
				],
				TestScheme);
			var ticket = new AuthenticationTicket(
				new ClaimsPrincipal(identity), TestScheme);
			return Task.FromResult(AuthenticateResult.Success(ticket));
		}
	}

	private static TransactionBasket CreateBasket(
		Guid id,
		string? branchCode,
		TransactionPaymentStatus status,
		string description)
	{
		var items = new[]
		{
			new TransactionLineItem(
				description,
				TransactionItemType.Service,
				new Money(10m, "INR"),
				1,
				TaxCategory.Standard)
		};
		return TransactionBasket.Reconstitute(
			id,
			"INR",
			new Dictionary<TaxCategory, decimal>
			{
				[TaxCategory.Standard] = 0m
			},
			items,
			status,
			[],
			branchCode is null ? null : new BranchCode(branchCode));
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

	private static Staff CreateStaff(
		string staffId,
		StaffRole role,
		string branchCode) =>
		new(
			staffId,
			$"Test {staffId}",
			$"{staffId}@example.test",
			role,
			new BranchCode(branchCode));
}
