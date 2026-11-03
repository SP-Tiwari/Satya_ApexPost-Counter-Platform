using ApexPost.Application.Persistence;

namespace ApexPost.Testing;

// Test-only: loads and saves a whole snapshot, so a test can seed or inspect
// its data in one call. The application uses the aggregate repositories and
// never this type. Application.Tests implements it in memory, and
// Web.IntegrationTests over the disposable SQL Server database.
internal interface IApexPostRepository
{
	Task<ApexPostSnapshot> LoadAsync(
		CancellationToken cancellationToken = default);

	Task SaveAsync(
		ApexPostSnapshot snapshot,
		CancellationToken cancellationToken = default);

	Task UpdateAsync(
		Func<ApexPostSnapshot, ApexPostSnapshot> update,
		CancellationToken cancellationToken = default);
}
