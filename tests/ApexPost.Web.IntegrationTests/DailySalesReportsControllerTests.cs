using System.Net;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace ApexPost.Web.IntegrationTests;

public sealed class DailySalesReportsControllerTests : IAsyncLifetime
{
	private const string ManagerId = "daily-sales-manager";
	private static readonly DateOnly ReportDate = new(2026, 1, 15);
	private string _storageDirectory = null!;
	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_storageDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-DailySales-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_storageDirectory);
		_factory = new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseSetting("Storage:Directory", _storageDirectory);
				builder.UseSetting(
					"Authentication:CredentialFile",
					Path.Combine(_storageDirectory, "credentials.json"));
				builder.ConfigureTestServices(services =>
					services.AddAuthentication(options =>
					{
						options.DefaultAuthenticateScheme =
							LowStockTestAuthenticationHandler.SchemeName;
						options.DefaultChallengeScheme =
							LowStockTestAuthenticationHandler.SchemeName;
						options.DefaultForbidScheme =
							LowStockTestAuthenticationHandler.SchemeName;
					}).AddScheme<AuthenticationSchemeOptions,
						LowStockTestAuthenticationHandler>(
						LowStockTestAuthenticationHandler.SchemeName,
						_ => { }));
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
	public async Task DailySales_ShowsRetailAndServiceTotalsForManagersHomeBranch()
	{
		await SeedAsync(
			[CreateStaff(ManagerId, StaffRole.BranchManager, "BR01")],
			[
				CreatePaidBasket("BR01", 100m, 20m),
				CreatePaidBasket("BR02", 80m, 10m),
				CreatePaidBasket("BR01", 55m, 5m, dayOffset: -1)
			]);
		using var client = CreateClient("BranchManager", ManagerId, "BR01");

		var response = await client.GetAsync(
			$"/Reports/DailySales?businessDate={ReportDate:yyyy-MM-dd}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		Assert.Matches(@"BR01</td>\s*<td>\d{1,2}/\d{1,2}/2026</td>\s*<td>100\.00 INR</td>\s*<td>20\.00 INR</td>\s*<td>120\.00 INR</td>", html);
		Assert.DoesNotContain("BR02", html, StringComparison.Ordinal);
		// The previous day's sale is reported only for its own date.
		Assert.DoesNotContain("55.00 INR", html, StringComparison.Ordinal);
	}

	// 24.05 at 10% tax is 26.455; usp_DailyBranchSales returns it as 26.46.
	[Fact]
	public async Task DailySales_ShowsTotalsRoundedToTwoDecimals()
	{
		await SeedAsync(
			[CreateStaff(ManagerId, StaffRole.BranchManager, "BR01")],
			[CreatePaidBasket("BR01", 24.05m, 1m, taxRate: 0.1m)]);
		using var client = CreateClient("BranchManager", ManagerId, "BR01");

		var response = await client.GetAsync(
			$"/Reports/DailySales?businessDate={ReportDate:yyyy-MM-dd}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		Assert.Matches(@"<td>26\.46 INR</td>\s*<td>1\.10 INR</td>\s*<td>27\.56 INR</td>", html);
		Assert.DoesNotContain("26.455", html, StringComparison.Ordinal);
	}

	// The dashboard rounds each column as the procedure does, so its chart
	// legend matches the Daily Sales report.
	[Fact]
	public async Task Dashboard_ShowsTodaysSalesRoundedLikeTheDailySalesReport()
	{
		var today = DateOnly.FromDateTime(DateTime.UtcNow);
		await SeedAsync(
			[CreateStaff(ManagerId, StaffRole.BranchManager, "BR01")],
			[CreatePaidBasket(
				"BR01", 24.05m, 1m, today.DayNumber - ReportDate.DayNumber, taxRate: 0.1m)]);
		using var client = CreateClient("BranchManager", ManagerId, "BR01");

		var response = await client.GetAsync("/");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var html = await response.Content.ReadAsStringAsync();
		Assert.Contains("<span class=\"chart-legend-value\">26.46</span>", html, StringComparison.Ordinal);
		Assert.Contains("<span class=\"chart-legend-value\">1.1</span>", html, StringComparison.Ordinal);
		Assert.DoesNotContain("26.455", html, StringComparison.Ordinal);
	}

	// The procedure refuses 9999-12-31, which has no next day to end the range.
	[Fact]
	public async Task DailySales_LastRepresentableDateIsABadRequest()
	{
		await SeedAsync(
			[CreateStaff(ManagerId, StaffRole.BranchManager, "BR01")],
			[]);
		using var client = CreateClient("BranchManager", ManagerId, "BR01");

		var response = await client.GetAsync("/Reports/DailySales?businessDate=9999-12-31");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task DailySales_EmptyDataRendersEmptyState()
	{
		await SeedAsync(
			[CreateStaff(ManagerId, StaffRole.BranchManager, "BR01")],
			[]);
		using var client = CreateClient("BranchManager", ManagerId, "BR01");

		var response = await client.GetAsync(
			$"/Reports/DailySales?businessDate={ReportDate:yyyy-MM-dd}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains(
			"No paid transactions were found for this branch on the selected date.",
			await response.Content.ReadAsStringAsync());
	}

	[Theory]
	[InlineData("Clerk", StaffRole.CounterClerk)]
	[InlineData("Auditor", StaffRole.Auditor)]
	public async Task DailySales_NonManagersAreForbidden(
		string claimRole,
		StaffRole persistedRole)
	{
		await SeedAsync(
			[CreateStaff(ManagerId, persistedRole, "BR01")],
			[]);
		using var client = CreateClient(claimRole, ManagerId, "BR01");

		var response = await client.GetAsync("/Reports/DailySales");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	private HttpClient CreateClient(
		string role,
		string? staffId,
		string? branchCode)
	{
		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add("X-Low-Stock-Role", role);
		if (staffId is not null)
			client.DefaultRequestHeaders.Add("X-Low-Stock-StaffId", staffId);
		if (branchCode is not null)
			client.DefaultRequestHeaders.Add("X-Low-Stock-BranchCode", branchCode);
		return client;
	}

	private Task SeedAsync(
		IEnumerable<Staff> staff,
		IEnumerable<TransactionBasket> baskets) =>
		_factory.Services.GetRequiredService<IApexPostRepository>()
			.SaveAsync(new ApexPostSnapshot(
				[], staff, [], [], [], [], [], [], baskets));

	private static Staff CreateStaff(
		string id,
		StaffRole role,
		string branchCode) =>
		new(id, $"Test {id}", $"{id}@example.test", role,
			new BranchCode(branchCode));

	private static TransactionBasket CreatePaidBasket(
		string branchCode,
		decimal retail,
		decimal service,
		int dayOffset = 0,
		decimal taxRate = 0m)
	{
		var taxRates = new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = taxRate
		};
		var basket = new TransactionBasket(
			"INR",
			taxRates,
			new BranchCode(branchCode),
			new DateTimeOffset(
				ReportDate.AddDays(dayOffset), new TimeOnly(10, 0), TimeSpan.Zero));
		basket.AddLineItem(new TransactionLineItem(
			"Retail",
			TransactionItemType.Goods,
			new Money(retail, "INR"),
			1,
			TaxCategory.Standard,
			new Sku("SKU-1")));
		basket.AddLineItem(new TransactionLineItem(
			"Service",
			TransactionItemType.Service,
			new Money(service, "INR"),
			1,
			TaxCategory.Standard));
		basket.Pay();
		return basket;
	}
}