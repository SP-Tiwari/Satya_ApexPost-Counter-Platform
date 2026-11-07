using ApexPost.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPost.Client.Consumer;

public static class ApexPostApiClientRegistration
{
	public const string BaseUrlKey = "ApexPostApi:BaseUrl";

	// Registers the generated client as a typed client: IHttpClientFactory
	// creates and pools its HttpClient, whose BaseAddress is the API root. The
	// generated client builds each URL relative to it, such as api/v2/shipments.
	public static IHttpClientBuilder AddApexPostApiClient(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		if (!Uri.TryCreate(configuration[BaseUrlKey], UriKind.Absolute, out var baseAddress)
			|| baseAddress.Scheme is not ("http" or "https"))
		{
			throw new InvalidOperationException(
				$"{BaseUrlKey} must be an absolute http or https URL, such as https://localhost:7280/.");
		}

		return services.AddHttpClient<IApexPostApiClient, ApexPostApiClient>(
			client => client.BaseAddress = baseAddress);
	}
}
