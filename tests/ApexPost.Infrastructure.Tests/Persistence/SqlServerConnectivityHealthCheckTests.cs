using ApexPost.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApexPost.Infrastructure.Tests.Persistence;

// No test here reaches a real database: port 1 on the loopback address has no
// listener, so these cover only the failure and cancellation paths.
public class SqlServerConnectivityHealthCheckTests
{
	private const string UnreachableConnectionString =
		"Server=tcp:127.0.0.1,1;Database=ApexPost_Unreachable;" +
		"Integrated Security=True;Encrypt=False;Connect Timeout=1;" +
		"Connect Retry Count=0;Pooling=False";

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Constructor_RejectsBlankConnectionString(string connectionString)
	{
		Assert.Throws<ArgumentException>(
			() => new SqlServerConnectivityHealthCheck(connectionString));
	}

	[Theory]
	[InlineData(HealthStatus.Unhealthy)]
	[InlineData(HealthStatus.Degraded)]
	public async Task CheckHealthAsync_WhenServerIsUnreachable_ReportsFailureStatusWithoutDetails(
		HealthStatus failureStatus)
	{
		var check = new SqlServerConnectivityHealthCheck(
			UnreachableConnectionString);

		var result = await check.CheckHealthAsync(CreateContext(check, failureStatus));

		Assert.Equal(failureStatus, result.Status);
		Assert.Null(result.Exception);
		Assert.DoesNotContain("127.0.0.1", result.Description);
		Assert.DoesNotContain("ApexPost_Unreachable", result.Description);
	}

	[Fact]
	public async Task CheckHealthAsync_WhenAlreadyCancelled_PropagatesCancellation()
	{
		var check = new SqlServerConnectivityHealthCheck(
			UnreachableConnectionString);
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			check.CheckHealthAsync(
				CreateContext(check, HealthStatus.Unhealthy),
				cancellation.Token));
	}

	private static HealthCheckContext CreateContext(
		IHealthCheck check,
		HealthStatus failureStatus) =>
		new()
		{
			Registration = new HealthCheckRegistration(
				"sql-server", check, failureStatus, tags: null)
		};
}
