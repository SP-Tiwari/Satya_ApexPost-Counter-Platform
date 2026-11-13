# ADR 0031: The courier domain model in its own project

- Status: Accepted
- Date: 2026-11-11
- Amends: [ADR 0027](0027-couchbase-lite-courier-offline-store.md) (project layout only)

## Context

Week 4 Part A requires that the courier's domain classes reference no Couchbase types. Until now they were in `src/ApexPost.Courier.Offline/Domain` (`ShipmentScan`, `ShipmentScanId`, `ScanType`, `ScanTypeCount`, `SyncStatus`). That is the same assembly that references `Couchbase.Lite`. No domain class used a Couchbase type, and `ArchitectureTests` checked this by reflection. But the compiler would accept a Couchbase type in a domain class, and the rule would only be caught when the tests ran.

The Week 4 review (96/100) named this as a remaining gap: "a separate project would let the compiler enforce the rule."

## Decision

The domain classes move to a new project, **`src/ApexPost.Courier.Domain`**, with the namespace `ApexPost.Courier.Domain`.

| Aspect | Decision |
|---|---|
| References | None. No package or project reference, only the framework. A `using Couchbase.Lite;` in a domain class is now a compile error |
| Dependency direction | `ApexPost.Courier.Offline` (the Couchbase persistence, the outbox and the change listener) references `ApexPost.Courier.Domain`. `ApexPost.Courier.Sync`, `ApexPost.Courier.Console` and `ApexPost.Courier.Harness` get it through `Offline`. Nothing points back at the domain from below |
| Code | The five files moved unchanged apart from their namespace (`git mv`, so their history follows). The only code change elsewhere is each `using ApexPost.Courier.Offline.Domain;`, which becomes `using ApexPost.Courier.Domain;` |
| Application layer | `IScanRepository`, `IShipmentUploader` and `OutboxService` stay in `Offline/Application`. They also reference no Couchbase type, as `ArchitectureTests.ApplicationTypes_DoNotReferenceCouchbase` checks. Moving them too would split the Part A library into three projects for a single gain the review did not ask for |
| Tests | `ArchitectureTests` now also checks that the domain is a separate assembly (`Domain_IsASeparateAssemblyFromThePersistenceLibrary`) and that it references only framework assemblies (`DomainAssembly_ReferencesOnlyTheFramework`). That catches a reference added back to the `.csproj`. `Library_DoesNotDependOnTheWeek3Projects` allows `ApexPost.Courier.Domain` as the library's only `ApexPost.*` reference |

```
ApexPost.Courier.Console ──► ApexPost.Courier.Sync ──► ApexPost.Courier.Offline ──► ApexPost.Courier.Domain
ApexPost.Courier.Harness ─────────────────────────────►        │  (Couchbase.Lite)         (no references)
                                                                ▼
                                                          Couchbase.Lite 4.1.2
```

## Alternatives considered

- **Keep the folder and rely on `ArchitectureTests`.** This was the Part A design. It works, but a violation is found only when the tests run, not when the code is compiled, and the reviewer marked it down.
- **A Roslyn analyzer or `BannedSymbols.txt` (Microsoft.CodeAnalysis.BannedApiAnalyzers) banning `Couchbase.*` in the `Domain` namespace.** This would also fail the build. But it adds a package and a configuration file to enforce what a project boundary enforces for free, and the ban is easy to weaken by editing a text file.
- **Put the courier domain in the Week 3 `ApexPost.Domain` project.** That project is the counter platform's domain (`TrackedShipment`, `ShipmentScanType`). Part A requires that the courier library does not depend on the Week 3 projects. The device's model (`SyncStatus`, the `scan::{Guid}` ID) also differs from the server's on purpose (ADR 0027).
- **Also split `Offline/Application` into its own project.** Rejected for now, as described above. It can be done later in the same way if the outbox gains another storage engine.

## Consequences

- The "no Couchbase in the domain" rule is enforced by the compiler; the tests guard the project file.
- The solution has one more project (`ApexPost.slnx`). Builds and tests are unchanged otherwise: all Part A, Part D and API tests passed after the move.
- The namespace changed from `ApexPost.Courier.Offline.Domain` to `ApexPost.Courier.Domain`. The Couchbase documents store no CLR type names, so existing local databases open unchanged.
- Evidence files recorded before the move (`docs/evidence/week4-part-a/...`) still show the old namespace; they are kept as recorded.
