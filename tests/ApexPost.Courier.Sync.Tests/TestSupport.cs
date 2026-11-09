using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ApexPost.Courier.Sync.Tests;

// A fictional token-shaped value; never a real credential.
internal static class TestValues
{
	public const string BaseUrl = "https://api.apexpost.test/";
	public const string Token = "test-access-token-7f3c9a";
	public const string CourierId = "COURIER-DEMO-07";

	public static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

	public static AccessToken ValidToken(string value = Token) => new(value, Now.AddHours(1));
}

// The client's HTTP pipeline, as AddCourierSync registers it, with the network
// replaced by StubHandler, the token store by an in-memory one and the clock
// by TestClock, which also drives the resilience pipeline.
internal sealed class ClientHost : IDisposable
{
	private readonly ServiceProvider _services;

	public ClientHost(
		Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
		string baseUrl = TestValues.BaseUrl)
	{
		Network = new StubHandler(respond ?? (_ => Responses.Json(HttpStatusCode.OK, "{}")));
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[CourierSyncRegistration.BaseUrlKey] = baseUrl
			})
			.Build();

		var services = new ServiceCollection();
		services.AddLogging(logging => logging
			.SetMinimumLevel(LogLevel.Trace)
			.AddProvider(Logs));
		services.AddSingleton<ITokenStore>(Tokens);
		services.AddSingleton<TimeProvider>(Clock);
		services.AddCourierSync(configuration).ConfigurePrimaryHttpMessageHandler(() => Network);
		_services = services.BuildServiceProvider();
	}

	public TestClock Clock { get; } = new();

	public StubHandler Network { get; }

	public InMemoryTokenStore Tokens { get; } = new();

	public RecordingLoggerProvider Logs { get; } = new();

	public ApexApiClient Client => _services.GetRequiredService<ApexApiClient>();

	public CourierSession Session => _services.GetRequiredService<CourierSession>();

	public void Dispose() => _services.Dispose();
}

// Records each attempt (with its body, read before the request is disposed)
// and answers with the supplied function. Throw fails every attempt with that
// exception, as a lost connection would. An attempt for which Hang returns true
// (given its 1-based number) waits until it is cancelled, as an unresponsive
// server would. AttemptStarted is released as each attempt arrives.
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
	: HttpMessageHandler
{
	private readonly ConcurrentQueue<RecordedRequest> _requests = new();

	public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

	public Exception? Throw { get; set; }

	public Func<int, bool>? Hang { get; set; }

	public SemaphoreSlim AttemptStarted { get; } = new(0);

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		var body = request.Content is null
			? null
			: await request.Content.ReadAsStringAsync(cancellationToken);
		_requests.Enqueue(new RecordedRequest(
			request.Method,
			request.RequestUri!,
			request.Headers.Authorization?.ToString(),
			request.Content?.Headers.ContentType?.MediaType,
			body));
		var attempt = _requests.Count;
		AttemptStarted.Release();

		if (Hang?.Invoke(attempt) == true)
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

		if (Throw is not null)
			throw Throw;

		var response = respond(request);
		response.RequestMessage = request;
		return response;
	}
}

// Fake time for the resilience pipeline. A one-shot timer of at most
// CourierResilience.MaxRetryDelay is a retry delay: it elapses at once, by
// moving the clock forward by its due time, and its length is recorded. Longer
// timers (the attempt and overall timeouts) fire only when a test calls
// Advance. So retry delays never slow a test down, and a timeout fires exactly
// when the test decides; no outcome depends on the real clock.
internal sealed class TestClock() : FakeTimeProvider(TestValues.Now)
{
	private readonly ConcurrentQueue<TimeSpan> _delays = new();

	public IReadOnlyList<TimeSpan> Delays => [.. _delays];

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		var timer = base.CreateTimer(callback, state, dueTime, period);
		if (dueTime > TimeSpan.Zero
			&& dueTime <= CourierResilience.MaxRetryDelay
			&& period == Timeout.InfiniteTimeSpan)
		{
			_delays.Enqueue(dueTime);
			ThreadPool.QueueUserWorkItem(_ => Advance(dueTime));
		}

		return timer;
	}
}

internal sealed record RecordedRequest(
	HttpMethod Method,
	Uri Uri,
	string? Authorization,
	string? ContentType,
	string? Body);

internal static class Responses
{
	public static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
		new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

	public static HttpResponseMessage Problem(HttpStatusCode status, string json) =>
		Json(status, json, "application/problem+json");

	public static HttpResponseMessage Empty(HttpStatusCode status) => new(status);
}

internal sealed class InMemoryTokenStore : ITokenStore
{
	public AccessToken? Token { get; set; }

	public int ClearCount { get; private set; }

	public AccessToken? GetToken() => Token;

	public void SaveToken(AccessToken token) => Token = token;

	public void ClearToken()
	{
		Token = null;
		ClearCount++;
	}
}

// Captures every log message of every category, including IHttpClientFactory's
// request and header logs at Trace level.
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
	private readonly ConcurrentQueue<string> _messages = new();

	public IReadOnlyCollection<string> Messages => _messages;

	public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _messages);

	public void Dispose()
	{
	}

	private sealed class RecordingLogger(string category, ConcurrentQueue<string> messages) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
	}
}
