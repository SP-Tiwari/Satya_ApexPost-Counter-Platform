namespace ApexPost.Application.Persistence;

// One commit per use case. Repositories only stage changes; this commits them.
public interface IUnitOfWork
{
	Task SaveChangesAsync(CancellationToken cancellationToken = default);

	// Runs a whole use case. The work may run more than once after a stock or
	// transaction conflict, so it must read current state itself on every
	// attempt and keep its results only in what it returns or overwrites. A
	// conflict that outlasts the retries throws ConcurrencyConflictException.
	Task<TResult> ExecuteInTransactionAsync<TResult>(
		Func<CancellationToken, Task<TResult>> work,
		CancellationToken cancellationToken = default);
}

public static class UnitOfWorkExtensions
{
	public static Task ExecuteInTransactionAsync(
		this IUnitOfWork unitOfWork,
		Func<CancellationToken, Task> work,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(unitOfWork);
		ArgumentNullException.ThrowIfNull(work);

		return unitOfWork.ExecuteInTransactionAsync<bool>(
			async token =>
			{
				await work(token).ConfigureAwait(false);
				return true;
			},
			cancellationToken);
	}
}
