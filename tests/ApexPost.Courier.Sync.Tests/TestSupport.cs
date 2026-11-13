using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Domain;
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
		services.AddSingleton<INetworkStatus>(Connectivity);
		services.AddSingleton<IScanRepository>(Scans);
		services.AddCourierSync(configuration).ConfigurePrimaryHttpMessageHandler(() => Network);
		_services = services.BuildServiceProvider();
	}

	public TestClock Clock { get; } = new();

	// The device's network (INetworkStatus) and scan store (IScanRepository).
	public FakeNetwork Connectivity { get; } = new();

	public InMemoryScanRepository Scans { get; } = new();

	public StubHandler Network { get; }

	public InMemoryTokenStore Tokens { get; } = new();

	public RecordingLoggerProvider Logs { get; } = new();

	public ApexApiClient Client => _services.GetRequiredService<ApexApiClient>();

	public CourierSession Session => _services.GetRequiredService<CourierSession>();

	public SyncState State => _services.GetRequiredService<SyncState>();

	public T Get<T>()
		where T : notnull => _services.GetRequiredService<T>();

	public ApiShipmentUploader Uploader() =>
		new(Client, _services.GetRequiredService<ILogger<ApiShipmentUploader>>());

	public OutboxService Outbox(IScanRepository scans) =>
		new(scans, Uploader(), _services.GetRequiredService<ILogger<OutboxService>>());

	public void Dispose() => _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

	public bool ClearToken(string expectedValue)
	{
		if (Token?.Value != expectedValue)
			return false;

		ClearToken();
		return true;
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

// IScanRepository in memory, with the ordering rules of CouchbaseScanRepository
// (oldest first, then by ID), for tests of the outbox and the sync coordinator
// that do not need Couchbase Lite.
internal sealed class InMemoryScanRepository : IScanRepository
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, ShipmentScan> _scans = new(StringComparer.Ordinal);

	public void Create(ShipmentScan scan)
	{
		lock (_gate)
		{
			if (scan.SyncStatus != SyncStatus.Pending || !_scans.TryAdd(scan.Id, scan))
				throw new InvalidOperationException($"Scan {scan.Id} cannot be created.");
		}
	}

	public ShipmentScan? GetById(string id)
	{
		lock (_gate)
			return _scans.GetValueOrDefault(id);
	}

	public void Update(ShipmentScan scan)
	{
		lock (_gate)
		{
			if (!_scans.ContainsKey(scan.Id))
				throw new KeyNotFoundException(scan.Id);
			_scans[scan.Id] = scan;
		}
	}

	public bool Delete(string id)
	{
		lock (_gate)
			return _scans.Remove(id);
	}

	public IReadOnlyList<ShipmentScan> ListAll() => Where(_ => true);

	public IReadOnlyList<ShipmentScan> FindByTrackingNumber(string trackingNumber) =>
		Where(scan => scan.TrackingNumber == trackingNumber);

	public IReadOnlyList<ShipmentScan> ListPending() =>
		Where(scan => scan.SyncStatus == SyncStatus.Pending);

	public IReadOnlyList<ScanTypeCount> CountByScanType(string courierId) =>
		[.. Where(scan => scan.CourierId == courierId)
			.GroupBy(scan => scan.ScanType)
			.OrderBy(group => group.Key)
			.Select(group => new ScanTypeCount(group.Key, group.Count()))];

	public SyncStatus StatusOf(ShipmentScan scan) => GetById(scan.Id)!.SyncStatus;

	private List<ShipmentScan> Where(Func<ShipmentScan, bool> predicate)
	{
		lock (_gate)
		{
			return [.. _scans.Values
				.Where(predicate)
				.OrderBy(scan => scan.ScannedAtUtc)
				.ThenBy(scan => scan.Id, StringComparer.Ordinal)];
		}
	}
}

// Fictional scans for COURIER-DEMO-07, one minute apart, oldest first.
internal static class TestScans
{
	public static ShipmentScan For(string trackingNumber, int minute, ScanType scanType = ScanType.Pickup) =>
		ShipmentScan.Record(
			trackingNumber,
			scanType,
			TestValues.CourierId,
			"Pune hub",
			TestValues.Now.AddMinutes(minute - 120));

	public static List<ShipmentScan> Store(InMemoryScanRepository repository, params ShipmentScan[] scans)
	{
		foreach (var scan in scans)
			repository.Create(scan);
		return [.. scans];
	}
}

// INetworkStatus under the test's control. Change raises the event and sets
// IsAvailable to match; AvailabilityChecks counts the reads of IsAvailable.
internal sealed class FakeNetwork : INetworkStatus
{
	private int _checks;

	public int AvailabilityChecks => Volatile.Read(ref _checks);

	public bool IsAvailable
	{
		get
		{
			Interlocked.Increment(ref _checks);
			return field;
		}

		set;
	} = true;

	public event EventHandler<bool>? AvailabilityChanged;

	public void Change(bool available)
	{
		IsAvailable = available;
		AvailabilityChanged?.Invoke(this, available);
	}
}

// Unsigned JWT-shaped tokens with the claims the client reads (courierId,
// role, exp). Fictional; the API would refuse them, the client cannot tell.
internal static class TestJwt
{
	public static string Create(string? courierId, DateTimeOffset expiresAtUtc, params string[] roles)
	{
		var claims = new Dictionary<string, object?>
		{
			["sub"] = courierId ?? "admin",
			["exp"] = expiresAtUtc.ToUnixTimeSeconds()
		};
		if (courierId is not null)
			claims["courierId"] = courierId;
		claims["role"] = roles.Length == 1 ? roles[0] : roles;
		return $"{Part("""{"alg":"none","typ":"JWT"}""")}.{Part(System.Text.Json.JsonSerializer.Serialize(claims))}.c2lnbmF0dXJl";
	}

	public static string Courier(string courierId = TestValues.CourierId) =>
		Create(courierId, TestValues.Now.AddHours(1), "Courier");

	public static string Part(string json) =>
		System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
