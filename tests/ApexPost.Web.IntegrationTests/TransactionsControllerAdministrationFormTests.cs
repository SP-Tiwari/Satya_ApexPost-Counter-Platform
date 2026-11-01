using System.Text.RegularExpressions;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Web.IntegrationTests;

// The request DTOs of the staff and branch forms. A rejected form must leave the
// row exactly as it was, so the tests compare the stored columns before and after.
public sealed partial class TransactionsControllerTests
{
	private const string UpdateStaffRoute = "/Administration/UpdateStaff";
	private const string SetBranchStatusRoute = "/Administration/SetBranchStatus";
	private const string StaffToUpdate = "manager-1";
	private const string BranchToUpdate = "BR01";

	[Fact]
	public async Task UpdateStaff_ValidForm_SavesTheRoleAndActiveState()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(
			UpdateStaffRoute,
			StaffForm(token, ("Role", nameof(StaffRole.Auditor)), ("IsActive", "true")));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var staff = await ReadStaffAsync();
		Assert.Equal((byte)StaffRole.Auditor, staff.RoleId);
		Assert.True(staff.IsActive);
	}

	// An unchecked box is not posted, so a missing value must mean inactive.
	[Fact]
	public async Task UpdateStaff_UncheckedActiveBox_DeactivatesTheStaffMember()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(UpdateStaffRoute, StaffForm(token));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.False((await ReadStaffAsync()).IsActive);
	}

	[Theory]
	[InlineData("StaffId", null)]
	[InlineData("StaffId", "")]
	[InlineData("Role", null)]
	[InlineData("Role", "NotARole")]
	[InlineData("HomeBranchCode", null)]
	[InlineData("HomeBranchCode", "BRANCHCODE11")]
	public async Task UpdateStaff_InvalidField_Returns400AndChangesNothing(
		string field,
		string? value)
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		var before = await ReadStaffAsync();
		var fields = new Dictionary<string, string>
		{
			["StaffId"] = StaffToUpdate,
			["Role"] = nameof(StaffRole.Auditor),
			["HomeBranchCode"] = BranchToUpdate,
			["IsActive"] = "true",
			["__RequestVerificationToken"] = token
		};
		if (value is null)
			fields.Remove(field);
		else
			fields[field] = value;

		var response = await client.PostAsync(
			UpdateStaffRoute, new FormUrlEncodedContent(fields));

		Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(before, await ReadStaffAsync());
	}

	[Fact]
	public async Task UpdateStaff_OverPostedFields_HaveNoEffect()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		var before = await ReadStaffAsync();

		var response = await client.PostAsync(
			UpdateStaffRoute,
			StaffForm(
				token,
				("IsActive", "true"),
				("Id", "someone-else"),
				("Name", "Changed"),
				("Email", "changed@example.test"),
				("CreatedBy", "attacker"),
				("CreatedAtUtc", "2000-01-01")));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var after = await ReadStaffAsync();
		Assert.Equal(before.Name, after.Name);
		Assert.Equal(before.Email, after.Email);
		Assert.Equal(before.CreatedBy, after.CreatedBy);
		Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
	}

	[Theory]
	[InlineData("false", false)]
	[InlineData("true", true)]
	public async Task SetBranchStatus_ValidForm_SavesTheStatus(string posted, bool expected)
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);

		var response = await client.PostAsync(
			SetBranchStatusRoute,
			BranchForm(token, ("Code", BranchToUpdate), ("IsActive", posted)));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal(expected, (await ReadBranchAsync()).IsActive);
	}

	[Theory]
	[InlineData(null, "false")]
	[InlineData("", "false")]
	[InlineData("BRANCHCODE11", "false")]
	[InlineData(BranchToUpdate, null)]
	[InlineData(BranchToUpdate, "maybe")]
	public async Task SetBranchStatus_InvalidField_Returns400AndChangesNothing(
		string? code,
		string? isActive)
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		var before = await ReadBranchAsync();
		var fields = new List<(string Name, string Value)>();
		if (code is not null)
			fields.Add(("Code", code));
		if (isActive is not null)
			fields.Add(("IsActive", isActive));

		var response = await client.PostAsync(
			SetBranchStatusRoute, BranchForm(token, [.. fields]));

		Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(before, await ReadBranchAsync());
	}

	[Fact]
	public async Task SetBranchStatus_OverPostedFields_HaveNoEffect()
	{
		using var client = await CreateAdministratorClientAsync();
		var token = await GetAdministrationTokenAsync(client);
		var before = await ReadBranchAsync();

		var response = await client.PostAsync(
			SetBranchStatusRoute,
			BranchForm(
				token,
				("Code", BranchToUpdate),
				("IsActive", "false"),
				("Name", "Changed"),
				("Region", "Changed"),
				("CreatedBy", "attacker")));

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		var after = await ReadBranchAsync();
		Assert.False(after.IsActive);
		Assert.Equal(before.Name, after.Name);
		Assert.Equal(before.Region, after.Region);
		Assert.Equal(before.CreatedBy, after.CreatedBy);
	}

	[Theory]
	[InlineData(UpdateStaffRoute, "StaffId")]
	[InlineData(UpdateStaffRoute, "Role")]
	[InlineData(UpdateStaffRoute, "HomeBranchCode")]
	[InlineData(UpdateStaffRoute, "IsActive")]
	[InlineData(SetBranchStatusRoute, "Code")]
	[InlineData(SetBranchStatusRoute, "IsActive")]
	public async Task Administration_FormsPostTheRequestFields(string route, string field)
	{
		using var client = await CreateAdministratorClientAsync();

		var html = await client.GetStringAsync("/Administration/Index");

		var form = Regex.Match(
			html,
			$"<form[^>]*action=\"{route}\".*?</form>",
			RegexOptions.Singleline);
		Assert.True(form.Success, $"No form posts to {route}.");
		Assert.Contains($"name=\"{field}\"", form.Value, StringComparison.Ordinal);
	}

	private static FormUrlEncodedContent StaffForm(
		string token,
		params (string Name, string Value)[] extra) =>
		new([
			new("StaffId", StaffToUpdate),
			new("HomeBranchCode", BranchToUpdate),
			.. extra.Any(field => field.Name == "Role")
				? (KeyValuePair<string, string>[])[]
				: [new("Role", nameof(StaffRole.BranchManager))],
			.. extra.Select(field => KeyValuePair.Create(field.Name, field.Value)),
			new("__RequestVerificationToken", token)
		]);

	private static FormUrlEncodedContent BranchForm(
		string token,
		params (string Name, string Value)[] fields) =>
		new([
			.. fields.Select(field => KeyValuePair.Create(field.Name, field.Value)),
			new("__RequestVerificationToken", token)
		]);

	private static async Task<StaffColumns> ReadStaffAsync()
	{
		await using var context = TestSqlServer.Database.CreateContext();
		return await context.Database
			.SqlQuery<StaffColumns>(
				$"""
				SELECT Name, Email, RoleId, HomeBranchCode, IsActive, CreatedBy, CreatedAtUtc
				FROM dbo.Staff
				WHERE StaffId = {StaffToUpdate}
				""")
			.SingleAsync();
	}

	private static async Task<BranchColumns> ReadBranchAsync()
	{
		await using var context = TestSqlServer.Database.CreateContext();
		return await context.Database
			.SqlQuery<BranchColumns>(
				$"""
				SELECT Name, Region, IsActive, CreatedBy
				FROM dbo.Branches
				WHERE BranchCode = {BranchToUpdate}
				""")
			.SingleAsync();
	}

	private sealed record StaffColumns(
		string Name,
		string Email,
		byte RoleId,
		string HomeBranchCode,
		bool IsActive,
		string CreatedBy,
		DateTime CreatedAtUtc);

	private sealed record BranchColumns(
		string Name,
		string Region,
		bool IsActive,
		string CreatedBy);
}
