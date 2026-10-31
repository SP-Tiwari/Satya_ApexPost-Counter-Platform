using System.Data;
using ApexPost.Application.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Calls the procedures through SqlQuery with typed parameters, so no value is
// ever part of the SQL text and each one matches its varchar parameter.
public sealed class SqlStoredProcedures(ApexPostDbContext database) : IStoredProcedures
{
	// The THROW numbers used in sql/procedures.
	private const int InvalidParameter = 51000;
	private const int NotFound = 51001;
	private const int NotAuthorized = 51002;
	private const int StateConflict = 51003;

	public async Task<DailyBranchSalesResult?> GetDailyBranchSalesAsync(
		string branchCode,
		DateOnly date,
		CancellationToken cancellationToken = default)
	{
		var branch = VarChar("BranchCode", 10, branchCode);
		var businessDate = new SqlParameter("Date", SqlDbType.Date) { Value = date };

		var rows = await RunAsync(token => database.Database
			.SqlQuery<DailyBranchSalesResult>(
				$"EXEC dbo.usp_DailyBranchSales @BranchCode = {branch}, @Date = {businessDate}")
			.ToListAsync(token), cancellationToken).ConfigureAwait(false);
		return rows.SingleOrDefault();
	}

	public async Task<IReadOnlyList<LowStockResult>> GetLowStockAsync(
		string? branchCode,
		int threshold,
		CancellationToken cancellationToken = default)
	{
		var branch = VarChar("BranchCode", 10, branchCode);
		var limit = new SqlParameter("Threshold", SqlDbType.Int) { Value = threshold };

		return await RunAsync(token => database.Database
			.SqlQuery<LowStockResult>(
				$"EXEC dbo.usp_LowStockReport @BranchCode = {branch}, @Threshold = {limit}")
			.ToListAsync(token), cancellationToken).ConfigureAwait(false);
	}

	public async Task<StockAdjustmentResult> AdjustStockAsync(
		string branchCode,
		string sku,
		int delta,
		string reason,
		string changedByStaffId,
		CancellationToken cancellationToken = default)
	{
		var branch = VarChar("BranchCode", 10, branchCode);
		var product = VarChar("Sku", 30, sku);
		var change = new SqlParameter("Delta", SqlDbType.Int) { Value = delta };
		var why = new SqlParameter("Reason", SqlDbType.NVarChar, 501)
		{
			Value = (object?)reason ?? DBNull.Value
		};
		var changedBy = VarChar("ChangedBy", 50, changedByStaffId);

		var rows = await RunAsync(token => database.Database
			.SqlQuery<StockAdjustmentResult>(
				$"EXEC dbo.usp_AdjustStock @BranchCode = {branch}, @Sku = {product}, @Delta = {change}, @Reason = {why}, @ChangedBy = {changedBy}")
			.ToListAsync(token), cancellationToken).ConfigureAwait(false);
		return rows.Single();
	}

	private static SqlParameter VarChar(string name, int size, string? value) =>
		new(name, SqlDbType.VarChar, size) { Value = (object?)value ?? DBNull.Value };

	private static async Task<List<T>> RunAsync<T>(
		Func<CancellationToken, Task<List<T>>> query,
		CancellationToken cancellationToken)
	{
		try
		{
			return await query(cancellationToken).ConfigureAwait(false);
		}
		catch (SqlException exception) when (exception.Number is
			InvalidParameter or NotFound or NotAuthorized or StateConflict)
		{
			// The same exception types the services throw, so the existing
			// status mappings apply.
			throw exception.Number switch
			{
				InvalidParameter => new ArgumentException(exception.Message, exception),
				NotFound => new KeyNotFoundException(exception.Message, exception),
				NotAuthorized => new UnauthorizedAccessException(exception.Message, exception),
				_ => new InvalidOperationException(exception.Message, exception)
			};
		}
	}
}
