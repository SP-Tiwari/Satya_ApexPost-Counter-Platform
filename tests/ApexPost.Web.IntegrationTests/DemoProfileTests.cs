using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Web.IntegrationTests;

// The documented demo starts the "demo" launch profile. These tests read that
// profile's real settings, so a sale keeps working on the seed alone.
public sealed class DemoProfileTests
{
	private const string Password = "fictional-demo-test-password";
	private const string Clerk = "demo-staff-01";

	[Fact]
	public async Task DemoProfile_ClerkCompletesASaleOnTheSeedAlone()
	{
		await using var factory = CreateDemoFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://localhost"),
			AllowAutoRedirect = false
		});
		await SignInAsync(client);

		var page = await client.GetStringAsync("/Transactions/Create");
		var transactionId = Regex.Match(
			page, "name=\"TransactionId\" value=\"([^\"]+)\"").Groups[1].Value;
		Assert.False(string.IsNullOrEmpty(transactionId));
		Assert.Contains("value=\"DEMO Cash\"", page, StringComparison.Ordinal);

		using var form = new FormUrlEncodedContent(
		[
			new("TransactionId", transactionId),
			new("CounterTerminalId", "DEMO-T01-01"),
			new("PaymentMethod", "DEMO Cash"),
			new("GoodsSku", "DEMO-SKU-002"),
			new("GoodsQuantity", "1"),
			new("IncludeShipping", "false"),
			new("__RequestVerificationToken", Token(page))
		]);
		var response = await client.PostAsync("/Transactions/Create", form);

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
		await using var context = TestSqlServer.Database.CreateContext();
		var id = Guid.Parse(transactionId);
		Assert.Equal(
			"Paid",
			await context.Database
				.SqlQuery<string>($"SELECT Status AS Value FROM dbo.CounterTransactions WHERE TransactionId = {id}")
				.SingleAsync());
	}

	[Fact]
	public void DemoProfile_RunsInDevelopmentAndHoldsNoSecrets()
	{
		var settings = DemoSettings();

		Assert.Equal("Development", settings["ASPNETCORE_ENVIRONMENT"]);
		Assert.DoesNotContain(
			settings.Keys,
			key => Regex.IsMatch(key, "Password|ApiKey|ConnectionStrings|Secret", RegexOptions.IgnoreCase));
	}

	[Theory]
	[InlineData("Transactions__TaxRates__ZeroRated", "0")]
	[InlineData("Transactions__PaymentMethods__0", "DEMO Cash")]
	public void DemoSaleSettings_ExistOnlyInTheDemoProfile(string key, string value)
	{
		Assert.Equal(value, DemoSettings()[key]);

		using var profiles = ReadProfiles();
		foreach (var profile in profiles.RootElement.GetProperty("profiles").EnumerateObject()
			.Where(profile => profile.Name != "demo"))
		{
			Assert.False(
				profile.Value.GetProperty("environmentVariables").TryGetProperty(key, out _),
				$"{key} is in the {profile.Name} profile.");
		}

		Assert.DoesNotContain(
			"\"Transactions\"",
			File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ApexPost.Web", "appsettings.json")),
			StringComparison.Ordinal);
	}

	private static async Task SignInAsync(HttpClient client)
	{
		var page = await client.GetStringAsync("/Account/SignIn");
		using var form = new FormUrlEncodedContent(
		[
			new("StaffId", Clerk),
			new("Password", Password),
			new("__RequestVerificationToken", Token(page))
		]);

		var response = await client.PostAsync("/Account/SignIn", form);

		Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
	}

	private static string Token(string html)
	{
		var token = Regex.Match(
			html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
		Assert.False(string.IsNullOrEmpty(token));
		return token;
	}

	private static WebApplicationFactory<Program> CreateDemoFactory()
	{
		var settings = DemoSettings();
		return new ApexPostWebApplicationFactory().WithWebHostBuilder(builder =>
		{
			foreach (var (key, value) in settings)
				builder.UseSetting(key.Replace("__", ":", StringComparison.Ordinal), value);
			builder.UseSetting("DemoData:StaffPassword", Password);
		});
	}

	private static Dictionary<string, string> DemoSettings()
	{
		using var profiles = ReadProfiles();
		return profiles.RootElement
			.GetProperty("profiles")
			.GetProperty("demo")
			.GetProperty("environmentVariables")
			.EnumerateObject()
			.ToDictionary(setting => setting.Name, setting => setting.Value.GetString()!);
	}

	private static JsonDocument ReadProfiles() =>
		JsonDocument.Parse(File.ReadAllText(Path.Combine(
			RepositoryRoot(), "src", "ApexPost.Web", "Properties", "launchSettings.json")));

	private static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApexPost.slnx")))
			directory = directory.Parent;
		return directory?.FullName
			?? throw new InvalidOperationException("The repository root was not found.");
	}
}
