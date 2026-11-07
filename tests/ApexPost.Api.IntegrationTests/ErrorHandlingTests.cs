using System.Net;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApexPost.Api.IntegrationTests;

// The global exception handler, ApiExceptionHandler. The repository is
// replaced by one that throws, so an error reaches the handler.
public sealed class ErrorHandlingTests
{
	private const string Failure = "Simulated storage failure for the error-handling tests.";

	[Theory]
	[InlineData("Production")]
	[InlineData("Staging")]
	public async Task UnhandledError_OutsideDevelopment_Returns500ProblemDetails_WithoutTheStackTrace(string environment)
	{
		using var factory = FactoryThrowing(new InvalidOperationException(Failure), environment);

		var response = await factory.CreateClient().GetAsync(Api.Shipment("APX-60000001"));

		var problem = await response.AssertProblemAsync(HttpStatusCode.InternalServerError);
		Assert.Equal("An unexpected error occurred.", problem.GetProperty("title").GetString());
		var body = problem.GetRawText();
		Assert.DoesNotContain(Failure, body, StringComparison.Ordinal);
		Assert.DoesNotContain("at ApexPost", body, StringComparison.Ordinal);
		Assert.DoesNotContain(nameof(ThrowingShipmentRepository), body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnhandledError_InDevelopment_IncludesTheExceptionDetail()
	{
		using var factory = FactoryThrowing(new InvalidOperationException(Failure), "Development");

		var response = await factory.CreateClient().GetAsync(Api.Shipment("APX-60000001"));

		var problem = await response.AssertProblemAsync(HttpStatusCode.InternalServerError);
		Assert.Contains(Failure, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task ConcurrencyConflict_Returns409ProblemDetails()
	{
		using var factory = FactoryThrowing(
			new ConcurrencyConflictException(
				"Another change was made at the same time.", new InvalidOperationException(Failure)),
			"Production");

		var response = await factory.CreateClient().GetAsync(Api.Shipment("APX-60000001"));

		var problem = await response.AssertProblemAsync(HttpStatusCode.Conflict);
		Assert.Equal(
			"The shipment was changed by someone else. Please try again.",
			problem.GetProperty("title").GetString());
	}

	private static ApiFactory FactoryThrowing(Exception exception, string environment) =>
		new(environment, services => services.Replace(
			ServiceDescriptor.Scoped<ITrackedShipmentRepository>(_ => new ThrowingShipmentRepository(exception))));

	private sealed class ThrowingShipmentRepository(Exception exception) : ITrackedShipmentRepository
	{
		public Task<TrackedShipment?> FindAsync(string trackingNumber, CancellationToken cancellationToken = default) =>
			throw exception;

		public Task<TrackedShipment?> FindForReadAsync(string trackingNumber, CancellationToken cancellationToken = default) =>
			throw exception;

		public Task<TrackedShipmentPage> GetPageAsync(int page, int pageSize, ShipmentStatus? status, string? city, CancellationToken cancellationToken = default) =>
			throw exception;

		public Task<IReadOnlyList<string>> FindStoredScanIdsAsync(IReadOnlyCollection<string> scanIds, CancellationToken cancellationToken = default) =>
			throw exception;

		public void Add(TrackedShipment shipment) => throw exception;

		public void Remove(TrackedShipment shipment) => throw exception;
	}
}
