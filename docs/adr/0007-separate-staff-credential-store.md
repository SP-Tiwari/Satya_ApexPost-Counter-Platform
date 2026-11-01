# ADR 0007: Separate staff credential store

- Status: Superseded: credentials are now in the StaffCredentials table (ADR 0018)
- Date: 2026-10-25 (updated 2026-10-29: demo provisioning, see ADR 0010)

## Context

Staff profile and branch information belongs to the business snapshot, while password-verification material is highly sensitive authentication data. Combining the credential data with ordinary staff records would unnecessarily broaden where credential material is handled and backed up.

## Decision

Keep staff credentials in a separately configured JSON document accessed through `IStaffCredentialRepository`, outside `ApexPostSnapshot`. The authentication service verifies stored password hashes and then resolves the staff profile from the business repository. Only active persisted staff may sign in; role and branch claims are derived from that record. Cookie validation rechecks the current persisted staff status, role, and branch.

Passwords use PBKDF2-SHA256 with at least 600,000 iterations and a random 16-byte salt, compared in constant time. A dummy hash runs for unknown staff IDs. The application never generates production credentials; operators provision the file out of band and protect it with filesystem permissions. The only write path is `IStaffCredentialProvisioning`, used for Development demo logins (ADR 0010), and it adds missing credentials but never replaces existing ones.

## Alternatives considered

- **Password hash on the `Staff` entity.** Simpler, but every snapshot backup, export or log of staff data would then carry hashes.
- **ASP.NET Core Identity.** Not allowed this week, and it brings its own store.
- **bcrypt or Argon2.** These need third-party packages outside the allowed libraries; PBKDF2 is in the framework.

## Consequences

Business snapshot backups do not contain password hashes. Authentication depends on both the separate credential file and the current staff data; missing or invalid credential data fails closed. Provisioning and protecting the credential file remain operational responsibilities. The dummy check uses the same 600,000 iterations as every credential the application creates, so an unknown staff ID takes as long to reject as a wrong password; only a credential provisioned out of band with a higher count would still take measurably longer.

Relevant implementation: `src/ApexPost.Application/Security/IStaffCredentialRepository.cs`, `src/ApexPost.Application/Services/StaffAuthenticationService.cs`, `src/ApexPost.Application/Security/StaffPasswordHashVerifier.cs`, `src/ApexPost.Infrastructure/Persistence/Sql/SqlSupportStores.cs`, `src/ApexPost.Web/Authentication/StaffCookieAuthenticationEvents.cs`, and `src/ApexPost.Web/Program.cs`.
