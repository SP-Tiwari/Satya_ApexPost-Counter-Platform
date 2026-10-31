namespace ApexPost.Application.Persistence;

public interface IStaffRepository
{
	// Matches the staff ID exactly, including its case.
	Task<Staff?> FindByIdAsync(
		string staffId,
		CancellationToken cancellationToken = default);

	Task<bool> EmailExistsAsync(
		string email,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<Staff>> GetStaffAsync(
		CancellationToken cancellationToken = default);

	void Add(Staff staff);
}
