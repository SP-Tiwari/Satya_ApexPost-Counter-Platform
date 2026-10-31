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
// hide a secret from Program. UseSetting applies early enough, so it blanks
// the development connection string: tests must never reach that database.
// A test that needs a connection string sets its own with UseSetting.
public class ApexPostWebApplicationFactory : WebApplicationFactory<Program>
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseSetting("ConnectionStrings:ApexPostDb", string.Empty);

		builder.ConfigureAppConfiguration((_, configuration) =>
		{
			var userSecrets = configuration.Sources
				.OfType<JsonConfigurationSource>()
				.Where(IsUserSecrets)
				.ToArray();
			foreach (var source in userSecrets)
				configuration.Sources.Remove(source);
		});
	}

	internal static bool IsUserSecrets(FileConfigurationSource source) =>
		string.Equals(source.Path, "secrets.json", StringComparison.OrdinalIgnoreCase);
}
