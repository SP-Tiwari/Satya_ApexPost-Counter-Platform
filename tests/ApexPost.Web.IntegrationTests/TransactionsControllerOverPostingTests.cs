using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Web.IntegrationTests;

// Part F: over-posted Status, Id and audit fields have no effect. Each test reads
// the stored row, because a field ignored by binding could still reach a column
// by another route. Id, Code and Sku are real inputs on the create forms, so there
// the protected values are the identity keys, IsActive and the audit columns.
public sealed partial class TransactionsControllerTests
{
	private const string OverPostedDate = "2000-01-01";
	private const int OverPostedKey = 9999;

	private static readonly (string Name, string Value)[] OverPostedAuditFields =
	[
		("CreatedBy", "attacker"),
		("CreatedAtUtc", OverPostedDate),
		("ModifiedAtUtc", OverPostedDate)
	];

	[Fact]
	public async Task CreateStaff_OverPostedFields_HaveNoEffect()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(
			"/Administration/CreateStaff",
			BranchForm(
				token,
				[
					("Id", "new-staff"),
					("Name", "New Staff"),
					("Email", "new.staff@example.test"),
					("Role", nameof(StaffRole.CounterClerk)),
					("HomeBranchCode", BranchToUpdate),
					("IsActive", "false"),
					("StaffId", "someone-else"),
					.. OverPostedAuditFields
				]));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		await using var context = TestSqlServer.Database.CreateContext();
		var stored = await context.Database
			.SqlQuery<CreatedStaffColumns>(
				$"""
				SELECT IsActive, CreatedBy, CreatedAtUtc, ModifiedAtUtc
				FROM dbo.Staff
				WHERE StaffId = 'new-staff'
				""")
			.SingleAsync();
		Assert.True(stored.IsActive);
		AssertServerControlledAudit(stored.CreatedBy, stored.CreatedAtUtc, stored.ModifiedAtUtc);
		Assert.Equal(0, await context.Database
			.SqlQuery<int>(
				$"SELECT COUNT(*) AS Value FROM dbo.Staff WHERE StaffId = 'someone-else'")
			.SingleAsync());
	}

	[Fact]
	public async Task CreateBranch_OverPostedFields_HaveNoEffect()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(
			"/Administration/CreateBranch",
			BranchForm(
				token,
				[
					("Code", "BR77"),
					("Name", "New Branch"),
					("Region", "North"),
					("OpeningHours", "09:00-17:00"),
					("TerminalId", "COUNTER-77"),
					("TerminalName", "Counter 77"),
					("BranchId", OverPostedKey.ToString()),
					("IsActive", "false"),
					.. OverPostedAuditFields
				]));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		await using var context = TestSqlServer.Database.CreateContext();
		var stored = await context.Database
			.SqlQuery<CreatedBranchColumns>(
				$"""
				SELECT BranchId, IsActive, CreatedBy, CreatedAtUtc, ModifiedAtUtc
				FROM dbo.Branches
				WHERE BranchCode = 'BR77'
				""")
			.SingleAsync();
		Assert.NotEqual(OverPostedKey, stored.BranchId);
		Assert.True(stored.IsActive);
		AssertServerControlledAudit(stored.CreatedBy, stored.CreatedAtUtc, stored.ModifiedAtUtc);
	}

	[Fact]
	public async Task CreateProduct_OverPostedFields_HaveNoEffect()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(
			"/Administration/CreateProduct",
			BranchForm(
				token,
				[
					("Sku", "NEW-01"),
					("Name", "New product"),
					("Category", nameof(ProductCategory.Packaging)),
					("UnitPrice", "10"),
					("Currency", "INR"),
					("TaxCategory", nameof(TaxCategory.Standard)),
					("ProductId", OverPostedKey.ToString()),
					.. OverPostedAuditFields
				]));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		await using var context = TestSqlServer.Database.CreateContext();
		var stored = await context.Database
			.SqlQuery<CreatedProductColumns>(
				$"""
				SELECT ProductId, CreatedBy, CreatedAtUtc, ModifiedAtUtc
				FROM dbo.Products
				WHERE Sku = 'NEW-01'
				""")
			.SingleAsync();
		Assert.NotEqual(OverPostedKey, stored.ProductId);
		AssertServerControlledAudit(stored.CreatedBy, stored.CreatedAtUtc, stored.ModifiedAtUtc);
	}

	[Theory]
	[InlineData("void", "Voided")]
	[InlineData("refund", "Refunded")]
	public async Task Reversal_OverPostedFields_HaveNoEffect(string action, string status)
	{
		var transactionId = await SellThroughTheFormAsync();
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, transactionId);
		var before = await ReadTransactionColumnsAsync(transactionId);

		var response = await client.PostAsync(
			$"/Transactions/{transactionId}/{action}",
			ReversalForm(
				token,
				("Status", "Paid"),
				("PaymentStatus", "Paid"),
				("Id", Guid.NewGuid().ToString()),
				("BranchCode", "BR99"),
				("ClerkStaffId", "someone-else"),
				("OccurredAtUtc", OverPostedDate),
				("ReversalReason", "Changed by the attacker"),
				("CreatedBy", "attacker"),
				("CreatedAtUtc", OverPostedDate),
				("ModifiedAtUtc", OverPostedDate)));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var after = await ReadTransactionColumnsAsync(transactionId);
		Assert.Equal(status, after.Status);
		Assert.Equal(VoidReason, after.ReversalReason);
		Assert.Equal(before.BranchCode, after.BranchCode);
		Assert.Equal(before.ClerkStaffId, after.ClerkStaffId);
		Assert.Equal(before.OccurredAtUtc, after.OccurredAtUtc);
		Assert.Equal(before.CreatedBy, after.CreatedBy);
		Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
		Assert.NotEqual(2000, after.ModifiedAtUtc?.Year);
	}

	// MVC reads form values before route values, so a posted transactionId must
	// not move the action to another sale. The audit reads the route, so it would
	// then name the wrong transaction as well.
	[Theory]
	[InlineData("void", "Voided")]
	[InlineData("refund", "Refunded")]
	public async Task Reversal_PostedTransactionId_CannotRedirectItToAnotherSale(
		string action,
		string status)
	{
		var addressed = await SellThroughTheFormAsync();
		var other = await SellAnotherThroughTheFormAsync();
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, addressed);

		var response = await client.PostAsync(
			$"/Transactions/{addressed}/{action}",
			ReversalForm(token, ("transactionId", other.ToString())));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var audited = Assert.Single(
			await TestSqlServer.ReadAuditRecordsAsync(),
			record => record.Action == (action == "void" ? "Void" : "Refund"));
		Assert.Equal(
			(status, "Paid", addressed.ToString()),
			((await ReadTransactionColumnsAsync(addressed)).Status,
				(await ReadTransactionColumnsAsync(other)).Status,
				audited.RouteValues["transactionId"]));
	}

	[Theory]
	[InlineData("void", "Voided")]
	[InlineData("refund", "Refunded")]
	public async Task Reversal_PostedForeignTransactionId_LeavesTheOtherBranchUntouched(
		string action,
		string status)
	{
		var (localId, _, foreignId, _) = await SeedTransactionsForTwoBranchesAsync();
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, localId);

		var response = await client.PostAsync(
			$"/Transactions/{localId}/{action}",
			ReversalForm(token, ("transactionId", foreignId.ToString())));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(status, (await ReadTransactionColumnsAsync(localId)).Status);
		Assert.Equal("Paid", (await ReadTransactionColumnsAsync(foreignId)).Status);
	}

	// A local ID in the form must not turn a refused foreign URL into a change to
	// the local sale, so the branch rule applies to the sale the URL names.
	[Theory]
	[InlineData("void")]
	[InlineData("refund")]
	public async Task Reversal_ForeignRouteWithPostedLocalId_IsStillRefused(string action)
	{
		var (localId, _, foreignId, _) = await SeedTransactionsForTwoBranchesAsync();
		using var client = CreateManagerClient();
		var token = await GetAntiforgeryTokenAsync(client, localId);

		var response = await client.PostAsync(
			$"/Transactions/{foreignId}/{action}",
			ReversalForm(token, ("transactionId", localId.ToString())));

		Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("Paid", (await ReadTransactionColumnsAsync(localId)).Status);
		Assert.Equal("Paid", (await ReadTransactionColumnsAsync(foreignId)).Status);
	}

	private async Task<Guid> SellAnotherThroughTheFormAsync()
	{
		using var client = CreateManagerClient();
		var (transactionId, token) = await GetCreateFormIdentityAsync(client);

		var response = await client.PostAsync(
			"/Transactions/Create", CreateMixedBasketForm(transactionId, token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		return transactionId;
	}

	private static FormUrlEncodedContent ReversalForm(
		string token,
		params (string Name, string Value)[] extra) =>
		new([
			new("Reason", VoidReason),
			.. extra.Select(field => KeyValuePair.Create(field.Name, field.Value)),
			new("__RequestVerificationToken", token)
		]);

	private static void AssertServerControlledAudit(
		string createdBy,
		DateTime createdAtUtc,
		DateTime? modifiedAtUtc)
	{
		Assert.Equal(AdministratorId, createdBy);
		Assert.NotEqual(2000, createdAtUtc.Year);
		Assert.Null(modifiedAtUtc);
	}

	private static async Task<TransactionColumns> ReadTransactionColumnsAsync(Guid transactionId)
	{
		await using var context = TestSqlServer.Database.CreateContext();
		return await context.Database
			.SqlQuery<TransactionColumns>(
				$"""
				SELECT Status, ReversalReason, BranchCode, ClerkStaffId, OccurredAtUtc,
					CreatedBy, CreatedAtUtc, ModifiedAtUtc
				FROM dbo.CounterTransactions
				WHERE TransactionId = {transactionId}
				""")
			.SingleAsync();
	}

	private sealed record CreatedStaffColumns(
		bool IsActive,
		string CreatedBy,
		DateTime CreatedAtUtc,
		DateTime? ModifiedAtUtc);

	private sealed record CreatedBranchColumns(
		int BranchId,
		bool IsActive,
		string CreatedBy,
		DateTime CreatedAtUtc,
		DateTime? ModifiedAtUtc);

	private sealed record CreatedProductColumns(
		int ProductId,
		string CreatedBy,
		DateTime CreatedAtUtc,
		DateTime? ModifiedAtUtc);

	private sealed record TransactionColumns(
		string Status,
		string? ReversalReason,
		string BranchCode,
		string? ClerkStaffId,
		DateTime OccurredAtUtc,
		string CreatedBy,
		DateTime CreatedAtUtc,
		DateTime? ModifiedAtUtc);
}
