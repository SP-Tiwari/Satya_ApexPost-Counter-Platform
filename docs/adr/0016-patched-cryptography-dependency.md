# ADR 0016: Pin the patched cryptography dependency

- Status: Accepted, pending trainer approval
- Date: 2026-10-31

## Context

The Week 3 brief allows `Microsoft.EntityFrameworkCore.*` and requires trainer approval and an ADR for packages outside its list.

`Microsoft.EntityFrameworkCore.SqlServer` 10.0.14 brings in `Microsoft.Data.SqlClient` 6.1.7, which brings in `System.Security.Cryptography.Pkcs` 9.0.11. NuGet's audit reports two advisories against that version: GHSA-846f-f764-76mx (high) and GHSA-2fmv-rv2c-59x7 (moderate). `TreatWarningsAsErrors` turns the audit warnings into restore errors, so the solution cannot build with the unpinned package.

## Decision

Add a direct reference in `ApexPost.Infrastructure` to `System.Security.Cryptography.Pkcs` 10.0.14, the same version as the rest of the .NET 10 packages. It adds no new library: the dependency already existed, and the pin only raises its version. No code calls it directly.

## Alternatives considered

- **Suppress the audit warnings.** The vulnerable code would still ship, and future advisories could be missed.
- **Upgrade `Microsoft.Data.SqlClient` to 7.x.** It is a new major version that EF Core 10.0.14 does not request, and it is a larger change than a dependency patch.
- **Turn off `TreatWarningsAsErrors`.** It would break the zero-warning requirement.

## Consequences

- `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages across all 8 projects.
- The pin must be reviewed when EF Core is next updated, and removed once `Microsoft.Data.SqlClient` depends on a patched version itself.
- The trainer needs to approve this reference, because the package is not on the brief's allowed list.
