using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace ApexPost.Courier.Sync;

// D7: the resilience pipeline of ApexApiClient, built with
// Microsoft.Extensions.Http.Resilience (Polly). From the outside in:
//   1. overall timeout   - bounds one call, retries and delays included;
//   2. retry             - 3 retries, exponential backoff with jitter;
//   3. circuit breaker   - stops calling an API that keeps failing;
//   4. attempt timeout   - bounds each single attempt.
// Only transient failures count: no response (network error or attempt
// timeout), HTTP 408 and HTTP 5xx. A 4xx, 429 included, is an answer and is
// never retried. Only a request marked safe to repeat is retried; the scan
// upload is, because the API stores each scan ID once and reports a repeat as
// a duplicate (Part B). The values and their reasons are in RESILIENCE.md.
// The logs name the method, the path and the failure, never a header or body.
public static class CourierResilience
{
	public const string PipelineName = "apexpost-api";
	public const int MaxRetryAttempts = 3;

	public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);
	public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(4);
	public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

	public const double FailureRatio = 0.5;
	public const int MinimumThroughput = 5;
	public static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(30);
	public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

	private static readonly HttpRequestOptionsKey<bool> RetrySafeKey = new("ApexPost.Courier.RetrySafe");

	// Marks a request whose repetition the server tolerates, so a transient
	// failure may be retried. Requests are not retried unless marked.
	internal static void MarkRetrySafe(HttpRequestMessage request) =>
		request.Options.Set(RetrySafeKey, true);

	internal static IHttpClientBuilder AddCourierResilience(this IHttpClientBuilder builder)
	{
		builder.AddResilienceHandler(PipelineName, (pipeline, context) =>
		{
			var logger = context.ServiceProvider
				.GetRequiredService<ILoggerFactory>()
				.CreateLogger(typeof(CourierResilience));
			var syncState = context.ServiceProvider.GetRequiredService<SyncState>();

			pipeline
				.AddTimeout(new TimeoutStrategyOptions
				{
					Name = "Total",
					Timeout = TotalTimeout,
					OnTimeout = args =>
					{
						logger.LogWarning(
							"The API call exceeded the overall timeout of {TimeoutSeconds} s, retries included.",
							args.Timeout.TotalSeconds);
						return default;
					}
				})
				.AddRetry(CreateRetryOptions(logger))
				.AddCircuitBreaker(CreateCircuitBreakerOptions(logger, syncState))
				.AddTimeout(new TimeoutStrategyOptions
				{
					Name = "Attempt",
					Timeout = AttemptTimeout
				});
		});
		return builder;
	}

	internal static RetryStrategyOptions<HttpResponseMessage> CreateRetryOptions(ILogger logger) => new()
	{
		MaxRetryAttempts = MaxRetryAttempts,
		BackoffType = DelayBackoffType.Exponential,
		UseJitter = true,
		Delay = RetryBaseDelay,
		MaxDelay = MaxRetryDelay,
		ShouldHandle = args => ValueTask.FromResult(
			IsTransient(args.Outcome) && IsRetrySafe(args.Context.GetRequestMessage())),
		OnRetry = args =>
		{
			var request = args.Context.GetRequestMessage();
			logger.LogWarning(
				"Retry {Retry} of {MaxRetries} for {Method} {Path} in {DelayMilliseconds} ms after {Failure}.",
				args.AttemptNumber + 1,
				MaxRetryAttempts,
				request?.Method.Method,
				request?.RequestUri?.AbsolutePath,
				(long)args.RetryDelay.TotalMilliseconds,
				Describe(args.Outcome));
			return default;
		}
	};

	// Each state change is also published to SyncState, for the Sync Status
	// screen and the offline banner (D11).
	private static CircuitBreakerStrategyOptions<HttpResponseMessage> CreateCircuitBreakerOptions(
		ILogger logger,
		SyncState syncState) => new()
	{
		FailureRatio = FailureRatio,
		MinimumThroughput = MinimumThroughput,
		SamplingDuration = SamplingDuration,
		BreakDuration = BreakDuration,
		ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
		OnOpened = args =>
		{
			syncState.SetCircuitState(CircuitState.Open);
			logger.LogWarning(
				"Circuit breaker opened for {BreakSeconds} s after {Failure}: API calls fail at once and scans stay on the device.",
				args.BreakDuration.TotalSeconds,
				Describe(args.Outcome));
			return default;
		},
		OnHalfOpened = _ =>
		{
			syncState.SetCircuitState(CircuitState.HalfOpen);
			logger.LogInformation("Circuit breaker half-open: the next API call tests whether the server has recovered.");
			return default;
		},
		OnClosed = _ =>
		{
			syncState.SetCircuitState(CircuitState.Closed);
			logger.LogInformation("Circuit breaker closed: the API is answering again.");
			return default;
		}
	};

	// No response at all (network error, attempt timeout), a 408 or a 5xx.
	private static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
		outcome.Exception switch
		{
			HttpRequestException or TimeoutRejectedException => true,
			null => outcome.Result?.StatusCode is HttpStatusCode.RequestTimeout
				|| (int?)outcome.Result?.StatusCode >= 500,
			_ => false
		};

	private static bool IsRetrySafe(HttpRequestMessage? request) =>
		request is not null
		&& request.Options.TryGetValue(RetrySafeKey, out var retrySafe)
		&& retrySafe;

	// The failure without its message, which could quote the server.
	private static string Describe(Outcome<HttpResponseMessage> outcome) =>
		outcome.Exception is { } exception
			? exception.GetType().Name
			: $"HTTP {(int)outcome.Result!.StatusCode}";
}
