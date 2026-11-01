using ApexPost.Application.Persistence;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ApexPost.Infrastructure.Tests.Sql.SqlTestHost;

namespace ApexPost.Infrastructure.Tests.Sql;

// A sale that fails after reaching SQL Server. A sale refused by a business rule
// fails before it writes anything, so only these tests exercise a real rollback.
[Collection(SqlCollection.Name)]
public sealed class SaleRollbackTests(SqlTestHost host) : IAsyncLifetime
{
	private static readonly SaleRows NoSale = new(0, 0, 0, 0, 0, 0, 0, 10);

	public Task InitializeAsync() => host.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task FailureAfterEveryRowIsWritten_RollsTheWholeSaleBack()
	{
		var request = Sale(Stationery, 10, withShippingAndMoneyOrder: true);
		var events = CountEvents();
		SaleRows? insideTheTransaction = null;

		await Assert.ThrowsAsync<SimulatedFailure>(() =>
			host.InRequestAsync(services => host.Sales(new WithUnitOfWork(
				services,
				new FailingUnitOfWork(
					services.GetRequiredService<IUnitOfWork>(),
					async () => insideTheTransaction = await ReadRowsAsync(
						services.GetRequiredService<ApexPostDbContext>()))))
				.CreateAndProcessAsync(ClerkId, request)));

		var written = Assert.IsType<SaleRows>(insideTheTransaction);
		Assert.Equal(1, written.Transactions);
		Assert.True(written.Lines > 0 && written.TaxRates > 0);
		Assert.Equal(1, written.Payments);
		Assert.Equal(1, written.Shipments);
		Assert.Equal(1, written.MoneyOrders);
		Assert.Equal(1, written.Verifications);
		Assert.Equal(0, written.Stock);
		Assert.Equal(NoSale, await host.QueryAsync(ReadRowsAsync));
		Assert.Equal(0, events());

		// The same request now succeeds, so the failure left nothing behind.
		await host.InRequestAsync(services =>
			host.Sales(services).CreateAndProcessAsync(ClerkId, request));

		Assert.Equal(written, await host.QueryAsync(ReadRowsAsync));
		Assert.True(events() > 0);
	}

	[Fact]
	public async Task SqlServerErrorDuringTheSave_RollsTheWholeSaleBack()
	{
		var events = CountEvents();
		await ExecuteAsync(
			"ALTER TABLE dbo.TransactionTaxRates WITH NOCHECK ADD CONSTRAINT CK_Test_RefuseRate CHECK ([Rate] < 0)");
		try
		{
			await Assert.ThrowsAsync<DbUpdateException>(() =>
				host.InRequestAsync(services => host.Sales(services).CreateAndProcessAsync(
					ClerkId, Sale(Stationery, 10, withShippingAndMoneyOrder: true))));
		}
		finally
		{
			await ExecuteAsync(
				"ALTER TABLE dbo.TransactionTaxRates DROP CONSTRAINT CK_Test_RefuseRate");
		}

		Assert.Equal(NoSale, await host.QueryAsync(ReadRowsAsync));
		Assert.Equal(0, events());
	}

	private Func<int> CountEvents()
	{
		var published = 0;
		host.Events.TransactionCompleted += (_, _) => published++;
		host.Events.StockLow += (_, _) => published++;
		host.Events.MoneyOrderIssued += (_, _) => published++;
		host.Events.HighValueMoneyOrderIssued += (_, _) => published++;
		return () => published;
	}

	private Task ExecuteAsync(string sql) =>
		host.QueryAsync(context => context.Database.ExecuteSqlRawAsync(sql));

	private static async Task<SaleRows> ReadRowsAsync(ApexPostDbContext context) => new(
		await context.CounterTransactions.AsNoTracking().CountAsync(),
		await context.TransactionLines.AsNoTracking().CountAsync(),
		await context.Database
			.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.TransactionTaxRates")
			.SingleAsync(),
		await context.Payments.AsNoTracking().CountAsync(),
		await context.Shipments.AsNoTracking().CountAsync(),
		await context.MoneyOrders.AsNoTracking().CountAsync(),
		await context.IdentityVerifications.AsNoTracking().CountAsync(),
		(await context.BranchStock.AsNoTracking()
			.SingleAsync(row => row.ProductSku == Stationery)).Quantity);

	private sealed record SaleRows(
		int Transactions,
		int Lines,
		int TaxRates,
		int Payments,
		int Shipments,
		int MoneyOrders,
		int Verifications,
		int Stock);

	private sealed class SimulatedFailure : Exception;

	// Fails after the use case has saved, while its transaction is still open.
	private sealed class FailingUnitOfWork(
		IUnitOfWork inner,
		Func<Task> beforeFailing) : IUnitOfWork
	{
		public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
			inner.SaveChangesAsync(cancellationToken);

		public Task<TResult> ExecuteInTransactionAsync<TResult>(
			Func<CancellationToken, Task<TResult>> work,
			CancellationToken cancellationToken = default) =>
			inner.ExecuteInTransactionAsync<TResult>(async token =>
			{
				await work(token);
				await beforeFailing();
				throw new SimulatedFailure();
			}, cancellationToken);
	}

	private sealed class WithUnitOfWork(
		IServiceProvider services,
		IUnitOfWork unitOfWork) : IServiceProvider
	{
		public object? GetService(Type serviceType) =>
			serviceType == typeof(IUnitOfWork)
				? unitOfWork
				: services.GetService(serviceType);
	}
}
