namespace ApexPost.Application.Persistence;

public interface IBranchRepository
{
	// Includes the branch's counter terminals.
	Task<Branch?> FindByCodeAsync(
		BranchCode code,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<Branch>> GetBranchesAsync(
		CancellationToken cancellationToken = default);

	void Add(Branch branch);
}
