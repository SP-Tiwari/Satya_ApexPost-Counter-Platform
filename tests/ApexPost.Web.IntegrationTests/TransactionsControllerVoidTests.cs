using ApexPost.Domain.Enums;
using ApexPost.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Web.IntegrationTests;

// The Branch Manager rule for voids, on a real sale in SQL Server. Rejections
// compare rowversions, which change on any write to the row, even of equal values.
public sealed partial class TransactionsControllerTests
{
	private const string VoidReason = "Entered in error";

	[Theory]
	[InlineData("", System.Net.HttpStatusCode.Unauthorized)]
	[InlineData("CounterClerk", System.Net.HttpStatusCode.Forbidden)]
	[InlineData("Auditor", System.Net.HttpStatusCode.Forbidden)]
	[InlineData("Administrator", System.Net.HttpStatusCode.Forbidden)]
	public async Task Void_ByAnotherRole_IsRejectedAndChangesNothing(
		string role,
		System.Net.HttpStatusCode expected)
	{
		var transactionId = await SellThroughTheFormAsync();
		var before = await ReadVoidStateAsync(transactionId);
		using var client = _factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		if (role.Length > 0)
		{
			client.DefaultRequestHeaders.Add("X-Test-Role", role);
			client.DefaultRequestHeaders.Add("X-Test-StaffId", "manager-1");
		}

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/void", CreateActionForm(VoidReason));

		Assert.Equal(expected, response.StatusCode);
		Assert.Equal(before, await ReadVoidStateAsync(transactionId));
	}

	// The sign-in claim still says Branch Manager, as it would until the next
	// request's revalidation, so only the stored staff row can refuse this.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Void_ByAManagerNoLongerActiveOrInRole_IsRejectedAndChangesNothing(
		bool deactivated)
	{
		var transactionId = await SellThroughTheFormAsync();
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, transactionId);
		await using (var context = TestSqlServer.Database.CreateContext())
		{
			await (deactivated
				? context.Database.ExecuteSqlAsync(
					$"UPDATE dbo.Staff SET IsActive = 0 WHERE StaffId = 'manager-1'")
				: context.Database.ExecuteSqlAsync(
					$"UPDATE dbo.Staff SET RoleId = {(byte)StaffRole.CounterClerk} WHERE StaffId = 'manager-1'"));
		}

		var before = await ReadVoidStateAsync(transactionId);

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/void", CreateActionForm(VoidReason, token));

		Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal(before, await ReadVoidStateAsync(transactionId));
		Assert.Contains(
			await TestSqlServer.ReadAuditRecordsAsync(),
			record => record.Action == "Void" &&
				record.StatusCode == 403 &&
				record.Outcome == "Rejected");
	}

	[Fact]
	public async Task Void_ByABranchManager_ReturnsTheStockAndRecordsTheReversal()
	{
		var transactionId = await SellThroughTheFormAsync();
		var before = await ReadVoidStateAsync(transactionId);
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, transactionId);

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/void", CreateActionForm(VoidReason, token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var after = await ReadVoidStateAsync(transactionId);
		Assert.Equal("Paid", before.Status);
		Assert.Equal(2, before.Quantity);
		Assert.Equal(1, before.Payments);
		Assert.Equal("Voided", after.Status);
		Assert.Equal(4, after.Quantity);
		Assert.Equal(2, after.Payments);
		var basket = Assert.Single((await GetRepositoryAsync()).TransactionBaskets);
		Assert.Equal(
			VoidReason,
			Assert.IsType<TransactionVoidedNotification>(
				Assert.Single(basket.Notifications)).Reason);
		Assert.Contains(
			await TestSqlServer.ReadAuditRecordsAsync(),
			record => record.Action == "Void" && record.Outcome == "Succeeded");
	}

	private async Task<Guid> SellThroughTheFormAsync()
	{
		await SeedMixedBasketDataAsync();
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		return transactionId;
	}

	private static async Task<VoidState> ReadVoidStateAsync(Guid transactionId)
	{
		await using var context = TestSqlServer.Database.CreateContext();
		var database = context.Database;
		return new VoidState(
			await database
				.SqlQuery<string>($"SELECT Status AS Value FROM dbo.CounterTransactions WHERE TransactionId = {transactionId}")
				.SingleAsync(),
			await database
				.SqlQuery<int>($"SELECT Quantity AS Value FROM dbo.BranchStock")
				.SingleAsync(),
			await database
				.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Payments WHERE TransactionId = {transactionId}")
				.SingleAsync(),
			await database
				.SqlQuery<string>($"SELECT CONVERT(varchar(18), RowVersion, 1) AS Value FROM dbo.CounterTransactions WHERE TransactionId = {transactionId}")
				.SingleAsync(),
			await database
				.SqlQuery<string>($"SELECT CONVERT(varchar(18), RowVersion, 1) AS Value FROM dbo.BranchStock")
				.SingleAsync());
	}

	private sealed record VoidState(
		string Status,
		int Quantity,
		int Payments,
		string TransactionRowVersion,
		string StockRowVersion);
}
