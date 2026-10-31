# ADR 0001: Versioned JSON snapshot persistence

- Status: Superseded by ADR 0018 and ADR 0020 (SQL Server persistence)
- Date: 2026-10-25 (updated 2026-10-29: schema history)

## Context

The assignment forbids a database or ORM this week but requires persistence behind abstractions that next week's database can replace. Domain invariants must stay independent of the storage format, and schema changes must not break existing data files.

## Decision

Use `IApexPostRepository` in Application and implement it with Infrastructure's versioned `apexpost.json` snapshot.

- **Loading:** entities are reconstructed through domain constructors and explicit state transitions, using persistence DTOs rather than deserializing straight into entities.
- **File I/O:** reads and writes are asynchronous. Replacements are written to a temporary file in the same folder and moved into place.
- **Updates:** `UpdateAsync(Func<snapshot, snapshot>)` holds the repository's `SemaphoreSlim` across load, change and save.
- **Schema history:** the current schema is 7.

  | Version | Change |
  |---|---|
  | 5 | `OccurredAtUtc` |
  | 6 | Money-order IDs and status |
  | 7 | Sale details |

  Older files are migrated on load without inventing business data. A file without a version that contains newer fields is rejected.

## Alternatives considered

- **One JSON file per collection (branches.json, stock.json, …).** This is easier to inspect, but a sale touches stock, baskets, shipping items and money orders, which would need multi-file atomicity that the file system cannot give.
- **SQLite or an embedded database.** It would give real transactions, but it is out of scope for this week ("no database, no ORM") and is next week's step.
- **Deserializing directly into entities.** Less code, but it would bypass constructor validation and let invalid states load.

## Consequences

The JSON implementation can be replaced behind the abstraction. Migrations stay explicit, and legacy data is never given invented business attributes. The whole document is rewritten on every change, which is fine at demo scale but not beyond. The lock coordinates only callers sharing one repository instance; it gives no cross-process concurrency control.
