# ADR 0027: Couchbase Lite courier offline store

- Status: Accepted
- Date: 2026-11-02

## Context

Week 4 Part A asks for a class library, `ApexPost.Courier.Offline`, and a console harness. Together they store courier shipment scans as JSON documents in Couchbase Lite, in a database named `apexpost_courier` using its default collection. Queries and change notifications must work without a network, and with no Couchbase Server or Sync Gateway. Domain classes must not reference Couchbase. The Week 3 SQL Server and EF Core code must not change, and Part D will replace the fake uploader with a real API call.

## Decision

| Concern | Decision |
|---|---|
| Package | `Couchbase.Lite` 4.1.2: the latest stable release (published 2026-09-14). Its `net8.0` assets run on .NET 10, checked on win-x64 before use. Community Edition; no replication is used. `Microsoft.Extensions.Logging.Abstractions` 10.0.14 matches the version the solution already uses |
| Transitive packages | Brought in by `Couchbase.Lite` and not referenced directly: `Couchbase.Lite.Support.NetDesktop` 4.1.2 (the native LiteCore libraries), `SerialQueue` 2.1.0, and `Microsoft.Extensions.DependencyInjection` 9.0.8. `Microsoft.Extensions.DependencyInjection.Abstractions` resolves to 10.0.14, raised by the logging package. None is pinned or upgraded here. `dotnet list package --vulnerable --include-transitive` reported none vulnerable on 2026-11-02 |
| Projects | One library, as the brief names. It is split by namespace: `Domain` (no Couchbase), `Application` (`IScanRepository`, `IShipmentUploader`, `OutboxService`, no Couchbase), `Persistence` (all Couchbase code) and `Uploading` (the fake). `ArchitectureTests` enforces the split. The harness is `ApexPost.Courier.Harness`. Neither project references the Week 3 projects |
| Lifecycle | `CourierDatabase : IDisposable` opens the database and creates both value indexes on every open; creating an existing index is a no-op. Dispose removes the listener token, closes the database and releases a single-owner lock file. Dispose is safe to call twice |
| Document model | `ShipmentScan` is a record whose init accessors validate every value, using the C# 14 `field` keyword, so `with` copies are checked too. `ShipmentScanDocument` maps the seven JSON fields explicitly. Enums are stored as their names, and `scannedAtUtc` as a fixed-width UTC string, so string order is time order |
| Repository | Synchronous, because Couchbase Lite's API is synchronous; async wrappers would add nothing. Get, update and delete are key-value operations on the document ID. Writes use `FailOnConflict` |
| Queries | SQL++ text lives in `ScanQueries`. Every value is a `$parameter`. Query plans show `idx_scans_trackingNumber` and `idx_scans_syncStatus` in use |
| Outbox | `syncStatus` in each document is the outbox; there is no second collection. `IShipmentUploader` is async and takes a cancellation token, ready for Part D's HTTP client |
| Listener | One collection change listener per database at most. It logs the changed IDs and the pending count, which the harness shows as a live count |

## Alternatives considered

- **A separate `ApexPost.Courier.Domain` project.** It would give compile-time separation, but the brief names a single library. The architecture test gives the same guarantee for the domain and application types.
- **Put `ShipmentScan` in `ApexPost.Domain`.** That would tie the courier device to the counter domain and touch Week 3 code.
- **Async repository methods wrapping the synchronous API.** They would look like I/O concurrency without providing any.
- **A separate outbox collection.** A scan and its outbox entry would then need to be written together. With the status field, one save does both.
- **Serialise with System.Text.Json into the document.** This is possible, but explicit mapping keeps the field names in one place and lets the domain validate stored values.
- **A composite index on `(trackingNumber, scannedAtUtc)`.** It would avoid the sort step shown in the plan. The brief asks for single-property value indexes, and one shipment has only a few scans, so the sort is cheap.

## Consequences

- The courier library can be built, tested and later changed without the SQL Server stack.
- The harness turns Couchbase Lite's own console logging down to errors, because that setting is process-wide.
- Uploading and marking a scan `Synced` are two separate steps, so Part D's API must accept a repeated scan ID. See [OFFLINE-DESIGN.md](../../OFFLINE-DESIGN.md).
- `Failed` scans are not retried automatically. That needs a retry rule, which belongs to Part D.
