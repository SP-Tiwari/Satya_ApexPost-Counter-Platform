using ApexPost.Domain.Entities;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ApexPost.Web.IntegrationTests;

// A sale that fails after its rows reach SQL Server, through the HTTP pipeline.
// See ADR 0022: the unit of work rolls back and the audit entry still commits.
public sealed partial class TransactionsControllerTests
{
	[Fact]
	public async Task CreateTransaction_FailureAfterWriting_RollsBackAndKeepsItsAuditRecord()
	{
		var failure = new FailAfterSale();
		_factory = _factory.WithWebHostBuilder(builder =>
			builder.ConfigureTestServices(services =>
				services.ConfigureDbContext<ApexPostDbContext>(options =>
					options.AddInterceptors(failure))));
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
		Assert.True(failure.RowsWritten > 0);
		var snapshot = await GetRepositoryAsync();
		Assert.Empty(snapshot.TransactionBaskets);
		Assert.Equal(4, Assert.Single(snapshot.BranchStocks).Quantity);
		var audit = Assert.Single(await TestSqlServer.ReadAuditRecordsAsync());
		Assert.Equal("Failed", audit.Outcome);
		Assert.Equal(500, audit.StatusCode);
		Assert.Equal(transactionId.ToString(), audit.RouteValues["transactionId"]);
	}

	private sealed class SimulatedStorageFailure : Exception;

	// Throws once the sale's rows are written and its transaction is still open.
	private sealed class FailAfterSale : SaveChangesInterceptor
	{
		public int RowsWritten { get; private set; }

		public override ValueTask<int> SavedChangesAsync(
			SaveChangesCompletedEventData eventData,
			int result,
			CancellationToken cancellationToken = default)
		{
			if (!eventData.Context!.ChangeTracker.Entries<TransactionBasket>().Any())
				return new ValueTask<int>(result);

			RowsWritten = result;
			throw new SimulatedStorageFailure();
		}
	}
}
