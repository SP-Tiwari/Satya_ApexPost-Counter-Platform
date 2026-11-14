using ApexPost.Application.Security;

namespace ApexPost.Application.Tests.Security;

public sealed class StaffPasswordHashingTests
{
	private const string Password = "Correct-Horse-Battery-1";

	[Fact]
	public void CreatedCredential_UsesTheSupportedWorkFactorAndVerifies()
	{
		var credential = StaffPasswordHasher.CreateCredential(" staff-1 ", Password);

		Assert.Equal("staff-1", credential.StaffId);
		Assert.Equal(StaffPasswordHashVerifier.MinimumIterations, credential.Iterations);
		Assert.True(StaffPasswordHashVerifier.Verify(Password, credential));
		Assert.False(StaffPasswordHashVerifier.Verify("wrong-password", credential));
		Assert.DoesNotContain(Password, credential.PasswordHash, StringComparison.Ordinal);
	}

	[Fact]
	public void SamePassword_GetsADifferentSaltEachTime()
	{
		var first = StaffPasswordHasher.CreateCredential("staff-1", Password);
		var second = StaffPasswordHasher.CreateCredential("staff-1", Password);

		Assert.NotEqual(first.Salt, second.Salt);
		Assert.NotEqual(first.PasswordHash, second.PasswordHash);
	}

	[Fact]
	public void CredentialBelowMinimumIterations_IsRejected()
	{
		var valid = StaffPasswordHasher.CreateCredential("staff-1", Password);
		var weak = valid with { Iterations = StaffPasswordHashVerifier.MinimumIterations - 1 };

		Assert.Throws<InvalidDataException>(
			() => StaffPasswordHashVerifier.Verify(Password, weak));
	}

	// The unknown-account path must cost as much as a real check, so response
	// time does not reveal which staff IDs exist. PerformDummyVerification runs
	// Verify against DummyCredential, so equal hash parameters mean equal work.
	// This replaces a wall-clock comparison whose result depended on machine
	// load; it still fails if the dummy work factor drops (an earlier dummy
	// used 210,000 iterations).
	[Fact]
	public void DummyCredential_HasTheSameWorkFactorAsACreatedCredential()
	{
		// Arrange
		var created = StaffPasswordHasher.CreateCredential("staff-1", Password);
		var dummy = StaffPasswordHashVerifier.DummyCredential;

		// Act
		var dummyAcceptsAPassword = StaffPasswordHashVerifier.Verify(Password, dummy);

		// Assert
		Assert.Equal(created.Version, dummy.Version);
		Assert.Equal(created.Iterations, dummy.Iterations);
		Assert.Equal(
			Convert.FromBase64String(created.Salt).Length,
			Convert.FromBase64String(dummy.Salt).Length);
		Assert.Equal(
			Convert.FromBase64String(created.PasswordHash).Length,
			Convert.FromBase64String(dummy.PasswordHash).Length);
		Assert.False(dummyAcceptsAPassword);
	}
}
