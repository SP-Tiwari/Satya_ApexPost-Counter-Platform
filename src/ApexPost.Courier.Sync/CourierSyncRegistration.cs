using ApexPost.Courier.Offline.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

public static class CourierSyncRegistration
{
	public const string BaseUrlKey = "ApexPostApi:BaseUrl";

	// Registers ApexApiClient as a typed client (D6): IHttpClientFactory creates
	// and pools its HttpClient, whose BaseAddress is ApexPostApi:BaseUrl,
	// AuthHeaderHandler adds the bearer token, and CourierResilience adds the
	// retry, circuit breaker and timeouts (D7). Also registers the token store,
	// the session and a TimeProvider, unless the host registered its own, and
	// the offline sync (D9): ApiShipmentUploader as the IShipmentUploader, the
	// OutboxService, the network status, the sync state and the
	// SyncCoordinator. The host registers the device's IScanRepository
	// (ApexPost.Courier.Offline) and starts the coordinator.
	// Returns the client builder so the host can extend the pipeline.
	public static IHttpClientBuilder AddCourierSync(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		var baseAddress = ReadBaseAddress(configuration);

		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<ITokenStore>(CreateTokenStore);
		services.TryAddSingleton<CourierSession>();
		services.TryAddTransient<AuthHeaderHandler>();
		services.TryAddSingleton<SyncState>();
		services.TryAddSingleton<INetworkStatus, SystemNetworkStatus>();
		services.TryAddTransient<IShipmentUploader, ApiShipmentUploader>();
		services.TryAddTransient<OutboxService>();
		services.TryAddSingleton(provider => new SyncCoordinator(
			provider.GetRequiredService<OutboxService>,
			provider.GetRequiredService<SyncState>(),
			provider.GetRequiredService<INetworkStatus>(),
			provider.GetRequiredService<TimeProvider>(),
			provider.GetRequiredService<ILogger<SyncCoordinator>>()));

		// AuthHeaderHandler is outside the resilience handler, so every attempt
		// carries the same token and a 401 (never retried) is handled once.
		// HttpClient.Timeout keeps its 100 s default, above the pipeline's
		// overall timeout, so the pipeline decides when a call has failed.
		return services
			.AddHttpClient<ApexApiClient>(client => client.BaseAddress = baseAddress)
			.AddHttpMessageHandler<AuthHeaderHandler>()
			.AddCourierResilience()
			// IHttpClientFactory's request logs never show a header value, so the
			// Authorization header cannot reach a log.
			.RedactLoggedHeaders(_ => true);
	}

	// Bearer tokens must never travel over plain HTTP (SECURITY.md, D5), so the
	// base URL must be https. A missing trailing slash is added, so relative
	// paths such as api/v2/... keep any path in the base URL.
	private static Uri ReadBaseAddress(IConfiguration configuration)
	{
		var configured = configuration[BaseUrlKey];
		if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseAddress)
			|| baseAddress.Scheme != Uri.UriSchemeHttps)
		{
			throw new InvalidOperationException(
				$"{BaseUrlKey} must be an absolute https URL, such as https://localhost:7280/.");
		}

		return baseAddress.AbsoluteUri.EndsWith('/')
			? baseAddress
			: new Uri(baseAddress.AbsoluteUri + "/");
	}

	private static ITokenStore CreateTokenStore(IServiceProvider services)
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"The courier client stores its access token with Windows DPAPI, which is available only on Windows.");
		}

		return new DpapiTokenStore(
			DpapiTokenStore.DefaultDirectory,
			services.GetRequiredService<TimeProvider>(),
			services.GetRequiredService<ILogger<DpapiTokenStore>>());
	}
}
