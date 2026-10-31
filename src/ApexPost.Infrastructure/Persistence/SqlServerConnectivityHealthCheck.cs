using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApexPost.Infrastructure.Persistence;

// Asks EF Core whether the database can be reached, only when the check is
// requested. The result never carries exception text, which can name the
// server or the database.
public sealed class SqlServerConnectivityHealthCheck : IHealthCheck
{
	private readonly DbContextOptions<ApexPostDbContext> _options;

	public SqlServerConnectivityHealthCheck(string connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString))
			throw new ArgumentException(
				"A connection string is required.", nameof(connectionString));

		_options = new DbContextOptionsBuilder<ApexPostDbContext>()
			.UseSqlServer(connectionString)
			.Options;
	}

	public async Task<HealthCheckResult> CheckHealthAsync(
		HealthCheckContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		await using var database = new ApexPostDbContext(_options);
		var canConnect = await database.Database
			.CanConnectAsync(cancellationToken)
			.ConfigureAwait(false);

		return canConnect
			? HealthCheckResult.Healthy()
			: new HealthCheckResult(
				context.Registration.FailureStatus,
				"The database could not be reached.");
	}
}
