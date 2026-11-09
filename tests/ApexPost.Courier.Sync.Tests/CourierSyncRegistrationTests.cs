using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPost.Courier.Sync.Tests;

// D6: the API root comes from configuration, and only an https URL is accepted.
public sealed class CourierSyncRegistrationTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("localhost:7280")]
	[InlineData("/api/")]
	[InlineData("http://localhost:5280/")]
	[InlineData("ftp://localhost/")]
	public void AddCourierSync_WithoutAnAbsoluteHttpsBaseUrl_FailsWithAClearMessage(string? baseUrl)
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => new ServiceCollection().AddCourierSync(Configuration(baseUrl)));

		Assert.Equal(
			"ApexPostApi:BaseUrl must be an absolute https URL, such as https://localhost:7280/.",
			exception.Message);
	}

	[Theory]
	[InlineData("https://localhost:7280/", "https://localhost:7280/dev/token")]
	[InlineData("https://localhost:7280", "https://localhost:7280/dev/token")]
	[InlineData("https://gateway.apexpost.test/courier-api", "https://gateway.apexpost.test/courier-api/dev/token")]
	public async Task Client_SendsRequestsUnderTheConfiguredBaseUrl(string baseUrl, string expected)
	{
		using var host = new ClientHost(baseUrl: baseUrl);

		await host.Client.RequestCourierTokenAsync(TestValues.CourierId);

		Assert.Equal(expected, Assert.Single(host.Network.Requests).Uri.AbsoluteUri);
	}

	[Fact]
	public void TypedClient_IsCreatedByTheFactory_ForEachResolution()
	{
		using var host = new ClientHost();

		Assert.NotSame(host.Client, host.Client);
	}

	[Fact]
	[SupportedOSPlatform("windows")]
	public void DefaultTokenStore_IsTheDpapiStore()
	{
		var services = new ServiceCollection().AddLogging();
		services.AddCourierSync(Configuration(TestValues.BaseUrl));
		using var provider = services.BuildServiceProvider();

		Assert.IsType<DpapiTokenStore>(provider.GetRequiredService<ITokenStore>());
	}

	private static IConfiguration Configuration(string? baseUrl) =>
		new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[CourierSyncRegistration.BaseUrlKey] = baseUrl
			})
			.Build();
}
