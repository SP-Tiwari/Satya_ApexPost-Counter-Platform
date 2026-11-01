using ApexPost.Application.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Scoped, sharing one context with the repositories. See ADR 0020.
public sealed class SqlUnitOfWork(ApexPostDbContext database) : IUnitOfWork
{
	private const int MaximumAttempts = 5;
	private const int MaximumBackoffMilliseconds = 50;
	private const int UniqueIndexViolation = 2601;
	private const int UniqueConstraintViolation = 2627;

	public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
		database.SaveChangesAsync(cancellationToken);

	public async Task<TResult> ExecuteInTransactionAsync<TResult>(
		Func<CancellationToken, Task<TResult>> work,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(work);

		var strategy = database.Database.CreateExecutionStrategy();
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				return await strategy.ExecuteAsync(
					async token =>
					{
						// Every attempt, including a transient-failure retry,
						// starts empty, so the work reloads current state.
						database.ChangeTracker.Clear();
						await using var transaction = await database.Database
							.BeginTransactionAsync(token)
							.ConfigureAwait(false);
						var result = await work(token).ConfigureAwait(false);
						await transaction.CommitAsync(token).ConfigureAwait(false);
						return result;
					},
					cancellationToken).ConfigureAwait(false);
			}
			catch (DbUpdateConcurrencyException exception)
			{
				database.ChangeTracker.Clear();
				if (attempt == MaximumAttempts)
				{
					throw new ConcurrencyConflictException(
						"Another change was made at the same time. Please try again.",
						exception);
				}

				await Task.Delay(
					Random.Shared.Next(1, MaximumBackoffMilliseconds * attempt),
					cancellationToken).ConfigureAwait(false);
			}
			catch (DbUpdateException exception) when (IsUniqueViolation(exception))
			{
				database.ChangeTracker.Clear();
				throw new DuplicateRecordException(
					"This record already exists.", exception);
			}
			catch
			{
				// The transaction has rolled back, so nothing tracked from the
				// failed attempt may reach a later read in the same request.
				database.ChangeTracker.Clear();
				throw;
			}
		}
	}

	private static bool IsUniqueViolation(DbUpdateException exception) =>
		exception.InnerException is SqlException
		{
			Number: UniqueIndexViolation or UniqueConstraintViolation
		};
}
