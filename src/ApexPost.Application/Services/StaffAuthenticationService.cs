using ApexPost.Application.Security;

namespace ApexPost.Application.Services;

public sealed class StaffAuthenticationService
{
	private readonly IStaffRepository _staff;
	private readonly IStaffCredentialRepository _credentials;

	public StaffAuthenticationService(
		IStaffRepository staff,
		IStaffCredentialRepository credentials)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(credentials);
		_staff = staff;
		_credentials = credentials;
	}

	// JSON persistence, until the SQL Server cutover in M3.
	public StaffAuthenticationService(
		IApexPostRepository repository,
		IStaffCredentialRepository credentials)
		: this(new SnapshotStore(repository), credentials)
	{
	}

	public async Task<Staff?> AuthenticateAsync(
		string staffId,
		string password,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(staffId) ||
			string.IsNullOrEmpty(password))
		{
			StaffPasswordHashVerifier.PerformDummyVerification(password ?? string.Empty);
			return null;
		}

		var credential = await _credentials.FindAsync(
			staffId.Trim(), cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			StaffPasswordHashVerifier.PerformDummyVerification(password);
			return null;
		}

		if (!StaffPasswordHashVerifier.Verify(password, credential))
			return null;

		var staff = await _staff.FindByIdAsync(
			credential.StaffId, cancellationToken).ConfigureAwait(false);
		return staff is { IsActive: true } ? staff : null;
	}

	public async Task<Staff?> FindActiveStaffAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			return null;

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		return staff is { IsActive: true } ? staff : null;
	}
}
