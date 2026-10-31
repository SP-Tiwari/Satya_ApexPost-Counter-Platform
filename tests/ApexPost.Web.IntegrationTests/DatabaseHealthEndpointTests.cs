using System.Net;
using Microsoft.Extensions.Hosting;

namespace ApexPost.Web.IntegrationTests;

// The configured connection string points at a port with no listener, so
// these tests never reach a real database.
public class DatabaseHealthEndpointTests : IAsyncLifetime
{
	private const string UnreachableConnectionString =
		"Server=tcp:127.0.0.1,1;Database=ApexPost_Unreachable;" +
		"Integrated Security=True;Encrypt=False;Connect Timeout=1;" +
		"Connect Retry Count=0;Pooling=False";

	private readonly string _rootDirectory = Path.Combine(
		Path.GetTempPath(),
		"apexpost-database-health-tests",
		Guid.NewGuid().ToString("N"));

	public Task InitializeAsync()
	{
		Directory.CreateDirectory(_rootDirectory);
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		if (Directory.Exists(_rootDirectory))
			Directory.Delete(_rootDirectory, recursive: true);
		return Task.CompletedTask;
	}

	// Also the guard that tests cannot reach the development database: on a
	// machine with the connection string in user-secrets, this fails if the
	// value becomes visible to Program.
	[Fact]
	public async Task DatabaseHealth_WithoutConnectionString_IsNotMapped()
	{
		using var factory = CreateFactory(Environments.Development, null);
		using var client = CreateClient(factory);

		var response = await client.GetAsync("/health/database");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DatabaseHealth_OutsideDevelopment_IsNotMapped()
	{
		using var factory = CreateFactory(
			Environments.Production, UnreachableConnectionString);
		using var client = CreateClient(factory);

		var response = await client.GetAsync("/health/database");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DatabaseHealth_WhenServerIsUnreachable_Returns503WithoutDetails()
	{
		using var factory = CreateFactory(
			Environments.Development, UnreachableConnectionString);
		using var client = CreateClient(factory);

		var response = await client.GetAsync("/health/database");
		var body = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		Assert.Equal("Unhealthy", body);
	}

	[Fact]
	public async Task ApplicationHealth_DoesNotDependOnTheDatabase()
	{
		using var factory = CreateFactory(
			Environments.Development, UnreachableConnectionString);
		using var client = CreateClient(factory);

		var response = await client.GetAsync("/health");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private WebApplicationFactory<Program> CreateFactory(
		string environment,
		string? connectionString) =>
		new ApexPostWebApplicationFactory().WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment(environment);
			builder.UseSetting(
				"Storage:Directory", Path.Combine(_rootDirectory, "data"));
			builder.UseSetting(
				"Authentication:CredentialFile",
				Path.Combine(_rootDirectory, "credentials.json"));
			if (connectionString is not null)
			{
				builder.UseSetting(
					"ConnectionStrings:ApexPostDb", connectionString);
			}
		});

	private static HttpClient CreateClient(
		WebApplicationFactory<Program> factory) =>
		factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://localhost")
		});
}
