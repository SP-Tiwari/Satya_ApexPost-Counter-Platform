using System.Text.Json;
using ApexPost.Application.Persistence;
using ApexPost.Domain.ValueObjects;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using BranchStock = ApexPost.Domain.Entities.BranchStock;

namespace ApexPost.Web.IntegrationTests;

// Real rowversion conflicts through the HTTP pipeline. A second, independent
// connection commits a stock change after the request has read the row and just
// before it writes, as a rival clerk would. See ADR 0021.
public sealed partial class TransactionsControllerTests
{
	private const string ConflictPath = "/__test/stock/remove-one";
	private const string BumpRowVersion =
		"UPDATE dbo.BranchStock SET Quantity = Quantity WHERE BranchCode = 'BR01' AND Sku = 'BOX-01'";
	private const int MaximumAttempts = 5;
	private const string ConflictMessage =
		"The transaction was not processed because another change was made at the same time. Review the form and submit it again.";
	private const string GeneralMessage = "The transaction could not be processed.";

	[Fact]
	public async Task CreateTransaction_RivalCommitsFirst_RetriesOnCurrentStock()
	{
		var rival = UseRival(
			"UPDATE dbo.BranchStock SET Quantity = Quantity - 1 WHERE BranchCode = 'BR01' AND Sku = 'BOX-01'",
			times: 1);
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(1, rival.Changes);
		var snapshot = await GetRepositoryAsync();
		Assert.Single(snapshot.TransactionBaskets);
		Assert.Equal(1, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	[Fact]
	public async Task CreateTransaction_RivalTakesTheStock_Returns409ViewAndNeverGoesNegative()
	{
		var rival = UseRival(
			"UPDATE dbo.BranchStock SET Quantity = 0 WHERE BranchCode = 'BR01' AND Sku = 'BOX-01'",
			times: 1);
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		var html = await AssertConflictViewAsync(response, GeneralMessage);
		Assert.DoesNotContain(ConflictMessage, html, StringComparison.Ordinal);
		Assert.Equal(1, rival.Changes);
		var snapshot = await GetRepositoryAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(0, Assert.Single(snapshot.BranchStocks).Quantity);
	}

	// Stock stays available, so only the rowversion rejects each attempt.
	[Fact]
	public async Task CreateTransaction_ConflictOnEveryAttempt_Returns409ViewAndSavesNothing()
	{
		var rival = UseRival(BumpRowVersion, times: int.MaxValue);
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		var html = await AssertConflictViewAsync(response, ConflictMessage);
		Assert.DoesNotContain(GeneralMessage, html, StringComparison.Ordinal);
		Assert.Matches($"name=\"TransactionId\"[^>]*value=\"{transactionId}\"", html);
		Assert.Matches("name=\"GoodsQuantity\"[^>]*value=\"2\"", html);
		Assert.Matches("name=\"WeightKilograms\"[^>]*value=\"3\"", html);
		Assert.Equal(MaximumAttempts, rival.Changes);
		var snapshot = await GetRepositoryAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
		var audit = Assert.Single(
			await TestSqlServer.ReadAuditRecordsAsync(),
			record => record.StatusCode == 409);
		Assert.Equal("Rejected", audit.Outcome);
	}

	// No real route lets this exception reach the handler: each one that writes a
	// rowversion table catches InvalidOperationException. This endpoint proves the
	// handler's payload for a real conflict, in case a route ever does.
	[Fact]
	public async Task UncaughtConflict_ReturnsSafeProblemDetailsWith409()
	{
		const string correlationId = "conflict-test-correlation";
		var rival = UseRival(BumpRowVersion, times: int.MaxValue, Environments.Production);
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		client.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

		var response = await client.PostAsync(ConflictPath, null);

		Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
		Assert.StartsWith(
			"application/problem+json",
			response.Content.Headers.ContentType?.ToString());
		var body = await response.Content.ReadAsStringAsync();
		using var problem = JsonDocument.Parse(body);
		var root = problem.RootElement;
		Assert.Equal(409, root.GetProperty("status").GetInt32());
		Assert.Equal(
			"The record was changed by someone else.",
			root.GetProperty("title").GetString());
		Assert.Equal(correlationId, root.GetProperty("correlationId").GetString());
		Assert.False(root.TryGetProperty("detail", out _), body);
		Assert.DoesNotContain("Another change", body, StringComparison.Ordinal);
		Assert.DoesNotContain("BranchStock", body, StringComparison.Ordinal);
		Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
		Assert.Equal(MaximumAttempts, rival.Changes);
		Assert.Equal(4, Assert.Single((await GetRepositoryAsync()).BranchStocks).Quantity);
	}

	private RivalChange UseRival(
		string sql,
		int times,
		string? environment = null)
	{
		var rival = new RivalChange(sql, times);
		_factory = _factory.WithWebHostBuilder(builder =>
		{
			if (environment is not null)
				builder.UseEnvironment(environment);
			builder.ConfigureTestServices(services =>
			{
				services.ConfigureDbContext<ApexPostDbContext>(options =>
					options.AddInterceptors(rival));
				services.AddControllersWithViews()
					.AddApplicationPart(typeof(ConflictTestController).Assembly);
			});
		});
		return rival;
	}

	// The clerk's form, redisplayed with its error. These routes answer with a
	// view, which the brief allows, and not with Problem Details.
	private static async Task<string> AssertConflictViewAsync(
		HttpResponseMessage response,
		string message)
	{
		Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
		Assert.StartsWith(
			"text/html", response.Content.Headers.ContentType?.ToString());
		var html = await response.Content.ReadAsStringAsync();
		Assert.Contains(message, html, StringComparison.Ordinal);
		return html;
	}

	private sealed class RivalChange(string sql, int times) : SaveChangesInterceptor
	{
		public int Changes { get; private set; }

		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default)
		{
			if (Changes < times && WritesStock(eventData.Context!.ChangeTracker))
			{
				Changes++;
				await using var connection = new SqlConnection(
					TestSqlServer.Database.ConnectionString);
				await connection.OpenAsync(cancellationToken);
				await using var command = new SqlCommand(sql, connection);
				await command.ExecuteNonQueryAsync(cancellationToken);
			}

			return result;
		}

		private static bool WritesStock(ChangeTracker tracker) =>
			tracker.Entries<BranchStock>().Any(entry => entry.State == EntityState.Modified);
	}
}

[ApiController]
[Route("__test/stock")]
public sealed class ConflictTestController(
	IUnitOfWork unitOfWork,
	IStockRepository stock) : ControllerBase
{
	[HttpPost("remove-one")]
	public async Task<IActionResult> RemoveOne(CancellationToken cancellationToken)
	{
		await unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			var row = await stock.FindAsync(new BranchCode("BR01"), new Sku("BOX-01"), token);
			row!.RemoveStock(1);
			await unitOfWork.SaveChangesAsync(token);
		}, cancellationToken);
		return NoContent();
	}
}
