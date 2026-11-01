namespace ApexPost.Application.Tests.TestDoubles;

// Counts commits around a real unit of work. ConflictsToSimulate makes that
// many commits fail, so the whole use case runs again as a retry would.
internal sealed class SpyUnitOfWork(IUnitOfWork inner) : IUnitOfWork
{
	public int Commits { get; private set; }
	public int Attempts { get; private set; }
	public int ConflictsToSimulate { get; init; }

	public Task SaveChangesAsync(CancellationToken cancellationToken = default)
	{
		if (Attempts <= ConflictsToSimulate)
			throw new SimulatedConflictException();

		Commits++;
		return inner.SaveChangesAsync(cancellationToken);
	}

	public async Task<TResult> ExecuteInTransactionAsync<TResult>(
		Func<CancellationToken, Task<TResult>> work,
		CancellationToken cancellationToken = default)
	{
		while (true)
		{
			Attempts++;
			try
			{
				return await inner.ExecuteInTransactionAsync(work, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (SimulatedConflictException)
			{
			}
		}
	}

	private sealed class SimulatedConflictException : Exception;
}
