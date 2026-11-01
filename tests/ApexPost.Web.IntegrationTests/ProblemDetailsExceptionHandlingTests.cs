using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace ApexPost.Web.IntegrationTests;

public sealed class ProblemDetailsExceptionHandlingTests : IAsyncLifetime
{
	private const string TestPath = "/__test/unhandled-error";
	public const string ExceptionSecret =
		"internal exception detail must not reach the response";
	private string _rootDirectory = null!;
	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_rootDirectory = Path.Combine(
			Path.GetTempPath(), $"ApexPost-ProblemDetails-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_rootDirectory);
		_factory = new ApexPostWebApplicationFactory()
			.WithWebHostBuilder(builder =>
			{
				builder.UseEnvironment(Environments.Production);
				builder.UseSetting(
					"Storage:Directory",
					Path.Combine(_rootDirectory, "domain-data"));
				builder.UseSetting(
					"Authentication:CredentialFile",
					Path.Combine(_rootDirectory, "credentials.json"));
				builder.ConfigureTestServices(services =>
					services.AddControllersWithViews()
						.AddApplicationPart(
							typeof(ProblemDetailsExceptionController).Assembly));
			});
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		if (Directory.Exists(_rootDirectory))
			Directory.Delete(_rootDirectory, recursive: true);
	}

	[Fact]
	public async Task UnexpectedException_ReturnsSafeProblemDetailsWithSecurityHeaders()
	{
		const string correlationId = "problem-test-correlation";
		var client = _factory.CreateClient();
		client.DefaultRequestHeaders.Add(
			"X-Correlation-ID", correlationId);

		var response = await client.GetAsync(TestPath);

		Assert.Equal(
			System.Net.HttpStatusCode.InternalServerError,
			response.StatusCode);
		Assert.StartsWith(
			"application/problem+json",
			response.Content.Headers.ContentType?.ToString());
		var body = await response.Content.ReadAsStringAsync();
		using var problem = JsonDocument.Parse(body);
		var root = problem.RootElement;
		Assert.Equal(
			correlationId,
			root.GetProperty("correlationId").GetString());
		Assert.True(
			response.Headers.TryGetValues(
				"X-Correlation-ID", out var responseCorrelationIds),
			$"Correlation header missing from Problem Details response: {body}");
		Assert.Equal(correlationId, Assert.Single(responseCorrelationIds!));
		Assert.Equal(
			"nosniff",
			Assert.Single(response.Headers.GetValues(
				"X-Content-Type-Options")));
		Assert.Equal(
			"DENY",
			Assert.Single(response.Headers.GetValues("X-Frame-Options")));
		Assert.Equal(
			"no-referrer",
			Assert.Single(response.Headers.GetValues("Referrer-Policy")));
		Assert.Equal(
			"default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'",
			Assert.Single(response.Headers.GetValues(
				"Content-Security-Policy")));

		Assert.Equal(500, root.GetProperty("status").GetInt32());
		Assert.Equal(
			"An unexpected error occurred.",
			root.GetProperty("title").GetString());
		Assert.DoesNotContain(ExceptionSecret, body, StringComparison.Ordinal);
		Assert.DoesNotContain("System.InvalidOperationException", body, StringComparison.Ordinal);
		Assert.DoesNotContain(_rootDirectory, body, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("concurrency-conflict", 409, "The record was changed by someone else.")]
	[InlineData("duplicate-record", 409, "The record already exists.")]
	[InlineData("not-found", 404, "The record was not found.")]
	[InlineData("invalid-argument", 400, "The request is not valid.")]
	public async Task KnownException_MapsToItsProblemDetails(
		string path, int status, string title)
	{
		var client = _factory.CreateClient();

		var response = await client.GetAsync($"/__test/{path}");

		Assert.Equal(status, (int)response.StatusCode);
		Assert.StartsWith(
			"application/problem+json",
			response.Content.Headers.ContentType?.ToString());
		var body = await response.Content.ReadAsStringAsync();
		using var problem = JsonDocument.Parse(body);
		Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
		Assert.Equal(title, problem.RootElement.GetProperty("title").GetString());
		Assert.DoesNotContain(ExceptionSecret, body, StringComparison.Ordinal);
	}
}

[ApiController]
[Route("__test")]
public sealed class ProblemDetailsExceptionController : ControllerBase
{
	[HttpGet("unhandled-error")]
	public IActionResult ThrowUnexpectedException() =>
		throw new InvalidOperationException(
			ProblemDetailsExceptionHandlingTests.ExceptionSecret);

	[HttpGet("concurrency-conflict")]
	public IActionResult ThrowConcurrencyConflict() =>
		throw new ApexPost.Application.Persistence.ConcurrencyConflictException(
			ProblemDetailsExceptionHandlingTests.ExceptionSecret,
			new InvalidOperationException());

	[HttpGet("duplicate-record")]
	public IActionResult ThrowDuplicateRecord() =>
		throw new ApexPost.Application.Persistence.DuplicateRecordException(
			ProblemDetailsExceptionHandlingTests.ExceptionSecret,
			new InvalidOperationException());

	[HttpGet("not-found")]
	public IActionResult ThrowNotFound() =>
		throw new KeyNotFoundException(
			ProblemDetailsExceptionHandlingTests.ExceptionSecret);

	[HttpGet("invalid-argument")]
	public IActionResult ThrowInvalidArgument() =>
		throw new ArgumentException(
			ProblemDetailsExceptionHandlingTests.ExceptionSecret);
}
