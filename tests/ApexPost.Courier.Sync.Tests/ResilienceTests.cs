using System.Net;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Sync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ApexPost.Courier.Sync.Tests;

// D7: retry, circuit breaker and timeouts of the typed client, through the
// pipeline AddCourierSync registers. TestClock makes retry delays elapse at once
// and fires a timeout only when a test advances the clock, so no outcome
// depends on real time; the real waits below only guard against a hang.
public sealed class ResilienceTests
{
	private const string TrackingNumber = "APX-00000011";
	private const string UploadPath = "/api/v2/shipments/APX-00000011/scans";
	private const string Location = "Nagpur hub 7";
	private const string UploadedOne = """{"accepted":1,"duplicatesSkipped":0,"rejected":0,"rejections":[]}""";

	private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

	private static readonly ScanUploadRequest Upload = new(
	[
		new ScanUploadItem(
			"scan::5d7f9b1d-3f5b-4d7f-9b1d-3f5b7d9f1b2d",
			TrackingNumber,
			ScanType.Delivered,
			TestValues.CourierId,
			Location,
			new DateTimeOffset(2026, 11, 7, 11, 10, 0, TimeSpan.Zero),
			"Left with the neighbour")
	]);

	[Fact]
	public void Settings_AreTheDocumentedValues()
	{
		Assert.Equal(3, CourierResilience.MaxRetryAttempts);
		Assert.Equal(TimeSpan.FromSeconds(1), CourierResilience.RetryBaseDelay);
		Assert.Equal(TimeSpan.FromSeconds(4), CourierResilience.MaxRetryDelay);
		Assert.Equal(TimeSpan.FromSeconds(10), CourierResilience.AttemptTimeout);
		Assert.Equal(TimeSpan.FromSeconds(30), CourierResilience.TotalTimeout);
		Assert.Equal(0.5, CourierResilience.FailureRatio);
		Assert.Equal(5, CourierResilience.MinimumThroughput);
		Assert.Equal(TimeSpan.FromSeconds(30), CourierResilience.SamplingDuration);
		Assert.Equal(TimeSpan.FromSeconds(30), CourierResilience.BreakDuration);

		// Four attempts and three maximum delays fit in the overall timeout's
		// budget only together with it: the overall timeout is the real cap.
		Assert.True(CourierResilience.SamplingDuration >= 2 * CourierResilience.AttemptTimeout);
		Assert.True(CourierResilience.TotalTimeout > CourierResilience.AttemptTimeout + CourierResilience.MaxRetryDelay);
	}

	[Theory]
	[InlineData(HttpStatusCode.RequestTimeout, ApiErrorKind.OtherStatus)]
	[InlineData(HttpStatusCode.InternalServerError, ApiErrorKind.ServerError)]
	[InlineData(HttpStatusCode.BadGateway, ApiErrorKind.ServerError)]
	[InlineData(HttpStatusCode.ServiceUnavailable, ApiErrorKind.ServerError)]
	[InlineData(HttpStatusCode.GatewayTimeout, ApiErrorKind.ServerError)]
	public async Task TransientStatus_IsRetriedThreeTimes_ThenReturned(HttpStatusCode status, ApiErrorKind kind)
	{
		using var host = new ClientHost(_ => Responses.Empty(status));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Equal(4, host.Network.Requests.Count);
		Assert.Equal(kind, result.Error!.Kind);
		Assert.Equal((int)status, result.Error.StatusCode);
		Assert.Equal(3, host.Clock.Delays.Count);
		Assert.All(host.Clock.Delays, delay => Assert.InRange(delay, TimeSpan.Zero, CourierResilience.MaxRetryDelay));
	}

	[Fact]
	public async Task NetworkFailure_IsRetriedThreeTimes_ThenThrown()
	{
		using var host = new ClientHost();
		host.Network.Throw = new HttpRequestException("No such host is known.");

		await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.UploadScansAsync(TrackingNumber, Upload));

		Assert.Equal(4, host.Network.Requests.Count);
	}

	[Fact]
	public async Task TransientFailures_ThenSuccess_ReturnsTheSuccess()
	{
		var attempt = 0;
		using var host = new ClientHost(_ => ++attempt <= 2
			? Responses.Empty(HttpStatusCode.ServiceUnavailable)
			: Responses.Json(HttpStatusCode.OK, UploadedOne));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.True(result.Succeeded);
		Assert.Equal(3, host.Network.Requests.Count);
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData(HttpStatusCode.Conflict)]
	[InlineData(HttpStatusCode.UnsupportedMediaType)]
	[InlineData(HttpStatusCode.UnprocessableEntity)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	public async Task ClientError_IsNeverRetried(HttpStatusCode status)
	{
		using var host = new ClientHost(_ => Responses.Empty(status));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Single(host.Network.Requests);
		Assert.Equal((int)status, result.Error!.StatusCode);
		Assert.Empty(host.Clock.Delays);
	}

	[Fact]
	public async Task TokenRequest_IsNotRetried_OnAServerError()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));

		var result = await host.Client.RequestCourierTokenAsync(TestValues.CourierId);

		Assert.Single(host.Network.Requests);
		Assert.Equal(ApiErrorKind.ServerError, result.Error!.Kind);
	}

	[Fact]
	public async Task TokenRequest_IsNotRetried_OnANetworkFailure()
	{
		using var host = new ClientHost();
		host.Network.Throw = new HttpRequestException("Connection refused.");

		await Assert.ThrowsAsync<HttpRequestException>(
			() => host.Client.RequestCourierTokenAsync(TestValues.CourierId));

		Assert.Single(host.Network.Requests);
	}

	[Fact]
	public async Task TokenRequest_IsNotRetried_AfterAnAttemptTimeout()
	{
		using var host = new ClientHost();
		host.Network.Hang = _ => true;

		var call = host.Client.RequestCourierTokenAsync(TestValues.CourierId);
		await StartedAsync(host);
		host.Clock.Advance(CourierResilience.AttemptTimeout);

		var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(() => call);
		Assert.Equal(CourierResilience.AttemptTimeout, exception.Timeout);
		Assert.Single(host.Network.Requests);
	}

	[Fact]
	public async Task RetriedUpload_SendsTheSameBodyAndToken_OnEveryAttempt()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));
		host.Tokens.Token = TestValues.ValidToken();

		await host.Client.UploadScansAsync(TrackingNumber, Upload);

		var attempts = host.Network.Requests;
		Assert.Equal(4, attempts.Count);
		Assert.All(attempts, attempt =>
		{
			Assert.Equal(attempts[0].Body, attempt.Body);
			Assert.Equal($"Bearer {TestValues.Token}", attempt.Authorization);
			Assert.Equal(UploadPath, attempt.Uri.AbsolutePath);
		});
		Assert.Contains(Location, attempts[0].Body);
	}

	[Fact]
	public async Task AttemptTimeout_IsRetried_AndTheNextAttemptSucceeds()
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, UploadedOne));
		host.Network.Hang = attempt => attempt == 1;

		var call = host.Client.UploadScansAsync(TrackingNumber, Upload);
		await StartedAsync(host);
		host.Clock.Advance(CourierResilience.AttemptTimeout);
		var result = await call;

		Assert.True(result.Succeeded);
		Assert.Equal(2, host.Network.Requests.Count);
	}

	[Fact]
	public async Task OverallTimeout_EndsTheCall_IncludingItsRetries()
	{
		using var host = new ClientHost();
		host.Network.Hang = _ => true;

		var call = host.Client.UploadScansAsync(TrackingNumber, Upload);

		// Attempts 1 and 2 time out after 10 s each; attempt 3 starts before
		// 30 s, because each delay is at most 4 s, and the overall timeout
		// ends it.
		for (var attempt = 1; attempt <= 3; attempt++)
		{
			await StartedAsync(host);
			host.Clock.Advance(CourierResilience.AttemptTimeout);
		}

		var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(() => call);
		Assert.Equal(CourierResilience.TotalTimeout, exception.Timeout);
		Assert.Equal(3, host.Network.Requests.Count);
		Assert.Contains(host.Logs.Messages, m => m.Contains("overall timeout of 30 s", StringComparison.Ordinal));
	}

	[Fact]
	public async Task CallerCancellation_DuringAnAttempt_IsThrown_AndNotRetried()
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, UploadedOne));
		host.Network.Hang = _ => true;
		using var cancellation = new CancellationTokenSource();

		var call = host.Client.UploadScansAsync(TrackingNumber, Upload, cancellation.Token);
		await StartedAsync(host);
		await cancellation.CancelAsync();

		// OperationCanceledException (TaskCanceledException included), not a
		// TimeoutRejectedException, and no second attempt or retry delay.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
		Assert.Single(host.Network.Requests);
		Assert.Empty(host.Clock.Delays);
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("Retry 1 of 3", StringComparison.Ordinal));
	}

	[Fact]
	public async Task CallerCancellations_DoNotCountAsCircuitBreakerFailures()
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, UploadedOne));
		host.Network.Hang = attempt => attempt <= CourierResilience.MinimumThroughput;

		// As many cancelled calls as the minimum throughput: were they failures,
		// the failure ratio would be 1.0 and the circuit would open.
		for (var call = 0; call < CourierResilience.MinimumThroughput; call++)
		{
			using var cancellation = new CancellationTokenSource();
			var upload = host.Client.UploadScansAsync(TrackingNumber, Upload, cancellation.Token);
			await StartedAsync(host);
			await cancellation.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
		}

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.True(result.Succeeded);
		Assert.Equal(CourierResilience.MinimumThroughput + 1, host.Network.Requests.Count);
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("Circuit breaker opened", StringComparison.Ordinal));
	}

	[Fact]
	public void Backoff_IsExponential_WithJitter()
	{
		var steady = RetryDelays(randomValue: 0.5);
		var low = RetryDelays(randomValue: 0.2);
		var high = RetryDelays(randomValue: 0.8);

		// With the same random value, each delay is longer than the last.
		Assert.Equal(3, steady.Count);
		Assert.True(steady[0] < steady[1] && steady[1] < steady[2], string.Join(", ", steady));

		// The random value (the jitter) changes the delays.
		Assert.NotEqual(low, high);

		Assert.All(
			steady.Concat(low).Concat(high),
			delay => Assert.InRange(delay, TimeSpan.Zero, CourierResilience.MaxRetryDelay));
	}

	[Fact]
	public async Task Retries_AreLogged_WithoutTokenOrBody()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));
		host.Tokens.Token = TestValues.ValidToken();

		await host.Client.UploadScansAsync(TrackingNumber, Upload);

		for (var retry = 1; retry <= 3; retry++)
		{
			Assert.Contains(
				host.Logs.Messages,
				m => m.Contains($"Retry {retry} of 3 for POST {UploadPath} in ", StringComparison.Ordinal)
					&& m.Contains("after HTTP 503", StringComparison.Ordinal));
		}

		AssertNoSecretsLogged(host);
	}

	[Fact]
	public async Task RepeatedFailures_OpenTheCircuit_AndCallsThenFailFast()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));
		host.Tokens.Token = TestValues.ValidToken();

		// One failed call (four attempts) is below the minimum throughput of 5.
		await host.Client.UploadScansAsync(TrackingNumber, Upload);
		Assert.Equal(4, host.Network.Requests.Count);
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("Circuit breaker opened", StringComparison.Ordinal));

		// The fifth failure opens it; the retry then meets the open circuit.
		await Assert.ThrowsAsync<BrokenCircuitException>(() => host.Client.UploadScansAsync(TrackingNumber, Upload));
		Assert.Equal(5, host.Network.Requests.Count);
		Assert.Contains(
			host.Logs.Messages,
			m => m.Contains("Circuit breaker opened for 30 s after HTTP 503", StringComparison.Ordinal));

		// While it is open, nothing reaches the network.
		await Assert.ThrowsAsync<BrokenCircuitException>(() => host.Client.UploadScansAsync(TrackingNumber, Upload));
		await Assert.ThrowsAsync<BrokenCircuitException>(() => host.Client.RequestCourierTokenAsync(TestValues.CourierId));
		Assert.Equal(5, host.Network.Requests.Count);

		AssertNoSecretsLogged(host);
	}

	[Fact]
	public async Task AfterTheBreak_TheCircuitIsHalfOpen_AndASuccessClosesIt()
	{
		var serverUp = false;
		using var host = new ClientHost(_ => serverUp
			? Responses.Json(HttpStatusCode.OK, UploadedOne)
			: Responses.Empty(HttpStatusCode.ServiceUnavailable));
		await OpenTheCircuitAsync(host);

		serverUp = true;
		host.Clock.Advance(CourierResilience.BreakDuration);
		var probe = await host.Client.UploadScansAsync(TrackingNumber, Upload);
		var next = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.True(probe.Succeeded);
		Assert.True(next.Succeeded);
		Assert.Equal(7, host.Network.Requests.Count);
		var messages = host.Logs.Messages.ToList();
		var halfOpen = messages.FindIndex(m => m.Contains("Circuit breaker half-open", StringComparison.Ordinal));
		var closed = messages.FindIndex(m => m.Contains("Circuit breaker closed", StringComparison.Ordinal));
		Assert.True(halfOpen >= 0 && closed > halfOpen);
	}

	[Fact]
	public async Task AFailedHalfOpenProbe_OpensTheCircuitAgain()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));
		await OpenTheCircuitAsync(host);

		host.Clock.Advance(CourierResilience.BreakDuration);
		await Assert.ThrowsAsync<BrokenCircuitException>(() => host.Client.UploadScansAsync(TrackingNumber, Upload));
		Assert.Equal(6, host.Network.Requests.Count);

		await Assert.ThrowsAsync<BrokenCircuitException>(() => host.Client.UploadScansAsync(TrackingNumber, Upload));
		Assert.Equal(6, host.Network.Requests.Count);
		Assert.Equal(2, host.Logs.Messages.Count(m => m.Contains("Circuit breaker opened", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task ClientErrors_DoNotOpenTheCircuit()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.NotFound));

		for (var call = 0; call < 10; call++)
			Assert.Equal(ApiErrorKind.NotFound, (await host.Client.UploadScansAsync(TrackingNumber, Upload)).Error!.Kind);

		Assert.Equal(10, host.Network.Requests.Count);
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("Circuit breaker opened", StringComparison.Ordinal));
	}

	// Two failed calls: four attempts, then the fifth failure opens the circuit.
	private static async Task OpenTheCircuitAsync(ClientHost host)
	{
		await host.Client.UploadScansAsync(TrackingNumber, Upload);
		await Assert.ThrowsAsync<BrokenCircuitException>(() => host.Client.UploadScansAsync(TrackingNumber, Upload));
		Assert.Equal(5, host.Network.Requests.Count);
	}

	private static async Task StartedAsync(ClientHost host) =>
		Assert.True(await host.Network.AttemptStarted.WaitAsync(HangGuard), "The attempt never started.");

	private static void AssertNoSecretsLogged(ClientHost host)
	{
		Assert.NotEmpty(host.Logs.Messages);
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(TestValues.Token, StringComparison.Ordinal));
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(Location, StringComparison.Ordinal));
		Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("Left with the neighbour", StringComparison.Ordinal));
	}

	// The production retry options with a fixed random value, so the delays
	// they produce are known; TestClock lets each delay elapse at once.
	private static List<TimeSpan> RetryDelays(double randomValue)
	{
		var delays = new List<TimeSpan>();
		var options = CourierResilience.CreateRetryOptions(NullLogger.Instance);
		options.Randomizer = () => randomValue;
		var onRetry = options.OnRetry!;
		options.OnRetry = args =>
		{
			delays.Add(args.RetryDelay);
			return onRetry(args);
		};

		var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = new TestClock() }
			.AddRetry(options)
			.Build();
		using var request = new HttpRequestMessage(HttpMethod.Post, TestValues.BaseUrl);
		CourierResilience.MarkRetrySafe(request);
		var context = ResilienceContextPool.Shared.Get();
		context.SetRequestMessage(request);
		try
		{
			Assert.True(
				pipeline
					.ExecuteAsync(_ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), context)
					.AsTask()
					.Wait(HangGuard),
				"The retry pipeline did not finish.");
		}
		finally
		{
			ResilienceContextPool.Shared.Return(context);
		}

		return delays;
	}
}
