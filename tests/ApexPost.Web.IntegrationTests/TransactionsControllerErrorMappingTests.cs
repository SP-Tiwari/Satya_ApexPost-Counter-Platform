using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using ApexPost.Infrastructure.Persistence.Sql;
using ApexPost.Web.Middleware;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApexPost.Web.IntegrationTests;

// The error mappings on real routes. Duplicate and not-found records on browser
// forms answer with the form and the right status. Exceptions no action catches
// reach ApplicationExceptionHandler and answer with Problem Details.
public sealed partial class TransactionsControllerTests
{
	private const string AdministratorId = "admin-1";
	private const string NewSku = "NEW-SKU-1";

	[Fact]
	public async Task CreateTransaction_SameIdCommittedByAnotherRequest_Returns409()
	{
		var rival = UseDuplicate(
			tracker => tracker.Entries<TransactionBasket>().Any(),
			"""
			INSERT INTO dbo.CounterTransactions (TransactionId, Currency, OccurredAtUtc, BranchCode)
			VALUES (@id, 'INR', SYSUTCDATETIME(), 'BR01')
			""");
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);
		rival.TransactionId = transactionId;
		rival.Armed = true;

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		var html = await AssertConflictViewAsync(response, GeneralMessage);
		AssertSafe(html);
		Assert.Equal(1, rival.Inserts);
		var snapshot = await GetRepositoryAsync();
		Assert.Equal(
			TransactionPaymentStatus.Open,
			Assert.Single(snapshot.TransactionBaskets).PaymentStatus);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task CreateProduct_UniqueViolationDuringTheSave_Returns409()
	{
		var rival = UseDuplicate(
			tracker => tracker.Entries<Product>().Any(entry => entry.State == EntityState.Added),
			"""
			INSERT INTO dbo.Products (Sku, Name, ProductCategoryId, TaxCategory, UnitPrice, Currency)
			VALUES ('NEW-SKU-1', 'Rival', 0, 'Standard', 1, 'INR')
			""");
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		rival.Armed = true;

		var response = await client.PostAsync(
			"/Administration/CreateProduct", CreateProductForm(NewSku, token));

		await AssertErrorViewAsync(
			response, System.Net.HttpStatusCode.Conflict, "This record already exists.");
		Assert.Equal(1, rival.Inserts);
	}

	[Fact]
	public async Task CreateProduct_ExistingSku_Returns409()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(
			"/Administration/CreateProduct", CreateProductForm("BOX-01", token));

		await AssertErrorViewAsync(
			response, System.Net.HttpStatusCode.Conflict, "Product SKU already exists.");
	}

	[Fact]
	public async Task UpdateStaff_UnknownStaff_Returns404()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		using var form = new FormUrlEncodedContent(
		[
			new("staffId", "nobody"),
			new("role", nameof(StaffRole.CounterClerk)),
			new("homeBranchCode", "BR01"),
			new("isActive", "true"),
			new("__RequestVerificationToken", token)
		]);

		var response = await client.PostAsync("/Administration/UpdateStaff", form);

		await AssertErrorViewAsync(
			response, System.Net.HttpStatusCode.NotFound, "Staff 'nobody' was not found.");
	}

	[Fact]
	public async Task SetBranchStatus_UnknownBranch_Returns404()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		using var form = new FormUrlEncodedContent(
		[
			new("code", "NONE"),
			new("isActive", "false"),
			new("__RequestVerificationToken", token)
		]);

		var response = await client.PostAsync("/Administration/SetBranchStatus", form);

		await AssertErrorViewAsync(
			response, System.Net.HttpStatusCode.NotFound, "Branch 'NONE' was not found.");
	}

	[Fact]
	public async Task UpdateStaff_InvalidBranchCode_Returns400WithoutInternalNames()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		using var form = new FormUrlEncodedContent(
		[
			new("staffId", "manager-1"),
			new("role", nameof(StaffRole.BranchManager)),
			new("homeBranchCode", "X"),
			new("isActive", "true"),
			new("__RequestVerificationToken", token)
		]);

		var response = await client.PostAsync("/Administration/UpdateStaff", form);

		await AssertErrorViewAsync(
			response,
			System.Net.HttpStatusCode.BadRequest,
			"Branch code must contain between 2 and 10 characters.");
	}

	[Fact]
	public async Task Details_EmptyTransactionId_ReturnsSafeProblemDetailsWith400()
	{
		await SeedManagerAndBranchAsync();
		using var client = CreateManagerClient();

		var response = await client.GetAsync($"/Transactions/{Guid.Empty}");

		await AssertProblemDetailsAsync(response, 400, "The request is not valid.");
	}

	[Fact]
	public async Task Administration_StaffRowNoLongerExists_ReturnsSafeProblemDetailsWith404()
	{
		await SeedManagerAndBranchAsync();
		using var client = CreateStaffClient("Administrator", "no-such-admin");

		var response = await client.GetAsync("/Administration/Index");

		await AssertProblemDetailsAsync(response, 404, "The record was not found.");
	}

	// An MVC route, which differs from an [ApiController] one here. Once an exception
	// is handled the framework does not log it, so the handler must.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task UnexpectedFailureOnARealRoute_ReturnsSafeProblemDetailsAndIsLogged(bool production)
	{
		var logs = new RecordingLoggerProvider();
		_factory = _factory.WithWebHostBuilder(builder =>
		{
			if (production)
				builder.UseEnvironment(Environments.Production);
			builder.ConfigureTestServices(services =>
			{
				services.AddSingleton<ILoggerProvider>(logs);
				services.ConfigureDbContext<ApexPostDbContext>(options =>
					options.AddInterceptors(new FailAfterSale()));
			});
		});
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		await AssertProblemDetailsAsync(response, 500, "An unexpected error occurred.");
		Assert.Contains(logs.Errors, entry =>
			entry.Category == typeof(ApplicationExceptionHandler).FullName &&
			entry.Exception is SimulatedStorageFailure);
	}

	private DuplicateRival UseDuplicate(Func<ChangeTracker, bool> when, string sql)
	{
		var rival = new DuplicateRival(when, sql);
		_factory = _factory.WithWebHostBuilder(builder =>
			builder.ConfigureTestServices(services =>
				services.ConfigureDbContext<ApexPostDbContext>(options =>
					options.AddInterceptors(rival))));
		return rival;
	}

	private async Task<HttpClient> CreateAdministratorClientAsync()
	{
		await SeedMixedBasketDataAsync();
		await using var context = TestSqlServer.Database.CreateContext();
		context.Staff.Add(new Staff(
			AdministratorId, "Admin", "admin@example.test", StaffRole.Administrator, new BranchCode("BR01")));
		await context.SaveChangesAsync();
		return CreateStaffClient("Administrator", AdministratorId);
	}

	private static async Task<string> GetAdministrationTokenAsync(HttpClient client)
	{
		var response = await client.GetAsync("/Administration/Index");
		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		var token = Regex.Match(
			await response.Content.ReadAsStringAsync(),
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
			.Groups[1].Value;
		Assert.False(string.IsNullOrWhiteSpace(token));
		return token;
	}

	private static FormUrlEncodedContent CreateProductForm(
		string sku,
		string token,
		string unitPrice = "10") => new(
		[
			new("Sku", sku),
			new("Name", "New product"),
			new("Category", nameof(ProductCategory.Packaging)),
			new("UnitPrice", unitPrice),
			new("Currency", "INR"),
			new("TaxCategory", nameof(TaxCategory.Standard)),
			new("__RequestVerificationToken", token)
		]);

	private static async Task AssertErrorViewAsync(
		HttpResponseMessage response,
		System.Net.HttpStatusCode status,
		string message)
	{
		Assert.Equal(status, response.StatusCode);
		Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
		var html = await response.Content.ReadAsStringAsync();
		Assert.Contains(HtmlEncoder.Default.Encode(message), html, StringComparison.Ordinal);
		AssertSafe(html);
	}

	private static async Task AssertProblemDetailsAsync(
		HttpResponseMessage response,
		int status,
		string title)
	{
		Assert.Equal(status, (int)response.StatusCode);
		Assert.StartsWith(
			"application/problem+json",
			response.Content.Headers.ContentType?.ToString());
		var body = await response.Content.ReadAsStringAsync();
		using var problem = JsonDocument.Parse(body);
		var root = problem.RootElement;
		Assert.Equal(status, root.GetProperty("status").GetInt32());
		Assert.Equal(title, root.GetProperty("title").GetString());
		Assert.False(root.TryGetProperty("detail", out _), body);
		Assert.Equal(
			root.GetProperty("correlationId").GetString(),
			Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
		Assert.Equal(
			"nosniff",
			Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
		AssertSafe(body);
	}

	private sealed class RecordingLoggerProvider : ILoggerProvider
	{
		private readonly ConcurrentQueue<(string Category, Exception Exception)> _errors = new();

		public IEnumerable<(string Category, Exception Exception)> Errors => _errors;

		public ILogger CreateLogger(string categoryName) =>
			new RecordingLogger(categoryName, _errors);

		public void Dispose()
		{
		}

		private sealed class RecordingLogger(
			string category,
			ConcurrentQueue<(string Category, Exception Exception)> errors) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state)
				where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

			public void Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				if (exception is not null)
					errors.Enqueue((category, exception));
			}
		}
	}

	private static void AssertSafe(string body)
	{
		foreach (var text in new[]
		{
			"Exception", "StackTrace", "   at ", "Microsoft.", "ApexPost.Infrastructure",
			"SqlClient", "UQ_", "PK_", "constraint", "duplicate key", "dbo.",
			"(Parameter ", @"E:\"
		})
		{
			Assert.DoesNotContain(text, body, StringComparison.OrdinalIgnoreCase);
		}
	}

	// Commits a row with the same key just before the request writes its own.
	// Armed after setup, because seeding adds the same kinds of row.
	private sealed class DuplicateRival(
		Func<ChangeTracker, bool> when,
		string sql) : SaveChangesInterceptor
	{
		public Guid TransactionId { get; set; }
		public bool Armed { get; set; }
		public int Inserts { get; private set; }

		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default)
		{
			if (Armed && Inserts == 0 && when(eventData.Context!.ChangeTracker))
			{
				Inserts++;
				await using var connection = new SqlConnection(
					TestSqlServer.Database.ConnectionString);
				await connection.OpenAsync(cancellationToken);
				await using var command = new SqlCommand(sql, connection);
				command.Parameters.AddWithValue("@id", TransactionId);
				await command.ExecuteNonQueryAsync(cancellationToken);
			}

			return result;
		}
	}
}
