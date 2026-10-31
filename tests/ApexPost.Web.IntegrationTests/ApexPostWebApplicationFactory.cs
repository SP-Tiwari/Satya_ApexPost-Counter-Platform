using ApexPost.Application.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace ApexPost.Web.IntegrationTests;

// The test host runs in Development, where the web project's user-secrets
// are loaded. A developer's local secrets (a demo password, a partner key)
// must not change test results, so that source is removed; each test sets
// the configuration it needs explicitly.
//
// ConfigureAppConfiguration runs when the host is built, after Program has
// already read builder.Configuration, so removing the source alone does not
// hide a secret from Program. UseSetting applies early enough, so it replaces
// the development connection string with the disposable test database: tests
// must never reach ApexPostDb. A test can still override it with UseSetting.
public class ApexPostWebApplicationFactory : WebApplicationFactory<Program>
{
	// Each factory starts from an empty database.
	public ApexPostWebApplicationFactory() =>
		TestSqlServer.Database.Reset();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseSetting(
			"ConnectionStrings:ApexPostDb",
			TestSqlServer.Database.ConnectionString);

		builder.ConfigureAppConfiguration((_, configuration) =>
		{
			var userSecrets = configuration.Sources
				.OfType<JsonConfigurationSource>()
				.Where(IsUserSecrets)
				.ToArray();
			foreach (var source in userSecrets)
				configuration.Sources.Remove(source);
		});

		builder.ConfigureTestServices(services =>
			services.AddSingleton<IApexPostRepository>(provider =>
				new SqlSnapshotRepository(
					provider.GetRequiredService<IServiceScopeFactory>())));
	}

	internal static bool IsUserSecrets(FileConfigurationSource source) =>
		string.Equals(source.Path, "secrets.json", StringComparison.OrdinalIgnoreCase);
}
