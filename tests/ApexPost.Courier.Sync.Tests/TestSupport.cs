using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
// replaced by StubHandler and the token store by an in-memory one.
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
		services.AddCourierSync(configuration).ConfigurePrimaryHttpMessageHandler(() => Network);
		_services = services.BuildServiceProvider();
	}

	public StubHandler Network { get; }

	public InMemoryTokenStore Tokens { get; } = new();

	public RecordingLoggerProvider Logs { get; } = new();

	public ApexApiClient Client => _services.GetRequiredService<ApexApiClient>();

	public CourierSession Session => _services.GetRequiredService<CourierSession>();

	public void Dispose() => _services.Dispose();
}

// Records each request (with its body, read before the request is disposed)
// and answers with the supplied function.
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
	: HttpMessageHandler
{
	public List<RecordedRequest> Requests { get; } = [];

	public Exception? Throw { get; set; }

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		var body = request.Content is null
			? null
			: await request.Content.ReadAsStringAsync(cancellationToken);
		Requests.Add(new RecordedRequest(
			request.Method,
			request.RequestUri!,
			request.Headers.Authorization?.ToString(),
			request.Content?.Headers.ContentType?.MediaType,
			body));

		if (Throw is not null)
			throw Throw;

		var response = respond(request);
		response.RequestMessage = request;
		return response;
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

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
	public DateTimeOffset Now { get; set; } = now;

	public override DateTimeOffset GetUtcNow() => Now;
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
