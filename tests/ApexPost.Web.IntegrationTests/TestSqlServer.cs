using ApexPost.Application.Auditing;
using ApexPost.Testing;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

// Tests share one database and one reset, so they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ApexPost.Web.IntegrationTests;

internal sealed class FailingAuditStore : IAuditStore
{
	public Task AppendAsync(
		AuditRecord record,
		CancellationToken cancellationToken = default) =>
		throw new InvalidOperationException("The audit store is unavailable.");
}

// The disposable database for this test run. Every test host uses it, and it
// is dropped when the test process ends.
internal static class TestSqlServer
{
	private static readonly Lazy<DisposableSqlDatabase> Instance = new(() =>
	{
		var database = DisposableSqlDatabase.Create();
		AppDomain.CurrentDomain.ProcessExit += (_, _) => database.Dispose();
		return database;
	});

	public static DisposableSqlDatabase Database => Instance.Value;

	public static async Task<AuditRecord[]> ReadAuditRecordsAsync()
	{
		await using var context = Database.CreateContext();
		return await context.AuditEntries.AsNoTracking().ToArrayAsync();
	}

	public static async Task<string> ReadAuditTextAsync() =>
		JsonSerializer.Serialize(await ReadAuditRecordsAsync());

	// Everything stored about money orders, to prove what never reaches a table.
	public static async Task<string> ReadMoneyOrderTextAsync()
	{
		await using var context = Database.CreateContext();
		var orders = await context.MoneyOrders.AsNoTracking()
			.Select(order => new { order.Sender, order.Recipient })
			.ToArrayAsync();
		var verifications = await context.IdentityVerifications.AsNoTracking()
			.Select(record => new { record.DocumentType, record.MaskedDocumentIdentifier })
			.ToArrayAsync();
		var lines = await context.TransactionLines.AsNoTracking()
			.Select(line => line.Description)
			.ToArrayAsync();
		return JsonSerializer.Serialize(new { orders, verifications, lines });
	}
}
