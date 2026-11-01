# ADR 0026: Offline counter terminals are a design only

- Status: Proposed. **Nothing in this ADR is implemented**
- Date: 2026-11-01

## Context

The Week 3 title and Lab Module 9 mention offline-first counter terminals, and the trainer's topics list collection change listeners, the outbox pattern, idempotency and conflict resolution. No Part, deliverable or mark in `AssignmentWeek03.txt` covers them, so the plan keeps this work to a design unless the assignment or trainer requires more.

## Decision

Build no offline workflow this week. Keep the schema able to support one. If it is approved later, the design would be:

| Concern | Design |
|---|---|
| Local state | The terminal keeps its reference data and unsent sales in a local store |
| Collection change listeners | The terminal listens to its pending-sales collection to update the screen and start a sync. A listener is unsubscribed when its screen closes, so it cannot leak or fire twice |
| Outbox | A sale and its outbox row are written in one local transaction, so a sale cannot be lost between saving and sending. A worker sends rows in order, and marks a row sent only after the server answers |
| Idempotency | The terminal creates the transaction's GUID, and every retry sends the same one |
| Conflict resolution | The server stays authoritative for stock, prices and sales. A sale the server refuses is kept for staff to review, never dropped or silently changed |

## What already supports this

- `CounterTransactions` uses a GUID primary key that does not depend on the database ([ADR 0018](0018-schema-design-and-delete-behaviours.md)).
- A repeated transaction ID is rejected safely, including two simultaneous submissions ([ADR 0021](0021-concurrency-strategy.md)).
- Rowversion and retries already handle two clerks changing the same stock.

## Open questions

These are business rules, so they need the trainer's answer and are not decided here:

- May a terminal sell while offline when it cannot confirm stock?
- Which price and tax rate apply to an offline sale: those at the time of sale, or those when it reaches the server?
- What should happen to a paid offline sale when the stock has gone?
- Can identity verification for a money order run offline at all?

## Alternatives considered

- **Build the outbox this week.** No deliverable requires it, and the plan forbids it without approval.
- **Ignore offline.** The title names it, so the schema decision and the reasoning are worth recording.

## Consequences

- The random GUID key stays, along with its measured fragmentation ([ADR 0024](0024-index-choices.md)).
- An evaluator looking for offline code will find none. This ADR is the whole of the work.
