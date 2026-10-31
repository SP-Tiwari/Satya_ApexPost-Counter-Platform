# ADR 0022: How the audit store interacts with the unit of work

- Status: Accepted
- Date: 2026-10-31

## Context

`AuditActionFilter` records each `[Audited]` action after its result has run. The brief asks for audit entries in the `AuditEntries` table, says audit writes must not break the business transaction, and asks whether they share the unit of work.

Week 2 had a gap: a failed audit write after a successful commit escaped the filter, so a clerk could see a 500 for a sale that had gone through.

## Decision

**Audit entries are stored separately from the unit of work.** `SqlAuditStore` opens a new scope for each write, with its own `ApexPostDbContext`, and commits on its own. It never touches the request's context, so nothing tracked by a failed use case can reach it.

**A failed audit write is logged and changes nothing.** `AuditActionFilter` catches every exception from `AppendAsync`, logs it, and leaves the response as it was.

| Failure | Result |
|---|---|
| The business action fails | The unit of work rolls back. The audit entry still commits, recording the rejection |
| The audit write fails | The business change stays committed. The response is unchanged, and the failure is logged |

The audit write uses `CancellationToken.None`, so a client disconnecting cannot drop the record of an action that already ran.

## Alternatives considered

- **Share the request's unit of work.** A rollback would erase the record of the failed attempt, which is the entry an auditor most needs. The audit filter also runs after the commit, so it would need a second commit anyway.
- **Keep the audit file alongside SQL Server.** No shared failure, but the brief requires `AuditEntries`, and auditors could not query it with the business data.
- **Fail the request when audit fails.** It would guarantee an entry per action, but a clerk would see an error for a completed sale, and the brief says audit must not break the business transaction.
- **An outbox for audit entries.** It could guarantee delivery after a failure, but that is new infrastructure. Offline and outbox work stays design-only this week.

## Consequences

- An action can succeed without an audit entry if the audit write fails. That shows up in the logs, not the table.
- Each audited request uses a second short-lived `DbContext`.
- `CounterScreensIntegrationTests` covers the two failure paths separately: `AuditWriteFailure_LeavesTheSaleCommittedAndTheResponseUnchanged` and `BusinessFailure_RollsBackButKeepsItsAuditRecord`.
