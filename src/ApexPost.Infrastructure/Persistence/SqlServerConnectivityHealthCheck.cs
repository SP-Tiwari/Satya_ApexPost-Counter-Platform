using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApexPost.Infrastructure.Persistence;

// Opens a connection and runs a trivial query, only when the check is
// requested. The result never carries the exception text, which can name the
// server or the database.
public sealed class SqlServerConnectivityHealthCheck : IHealthCheck
{
	private readonly string _connectionString;

	public SqlServerConnectivityHealthCheck(string connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString))
			throw new ArgumentException(
				"A connection string is required.", nameof(connectionString));

		_connectionString = connectionString;
	}

	public async Task<HealthCheckResult> CheckHealthAsync(
		HealthCheckContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		try
		{
			await using var connection = new SqlConnection(_connectionString);
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

			await using var command = connection.CreateCommand();
			command.CommandText = "SELECT 1;";
			await command.ExecuteScalarAsync(cancellationToken)
				.ConfigureAwait(false);

			return HealthCheckResult.Healthy();
		}
		catch (SqlException)
		{
			return new HealthCheckResult(
				context.Registration.FailureStatus,
				"The database could not be reached.");
		}
	}
}
