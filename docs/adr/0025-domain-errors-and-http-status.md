# ADR 0025: State conflicts stay 409, a deliberate deviation

- Status: Accepted, pending trainer confirmation
- Date: 2026-11-01

## Context

Part G of the brief lists "domain errors → 400". Week 2 returns 409 when a request is well formed but the current state refuses it, such as insufficient stock or a void of a voided sale. Tests pin that behaviour.

The two rules collide for state conflicts. Part F also requires the Week 2 business-rule tests to pass unchanged, and a new `DomainException` type would break them, because xUnit's `Assert.Throws<T>` matches the exact type.

## Decision

The existing exception types stay, and each maps by meaning:

| Exception | Meaning | Status |
|---|---|---|
| `ArgumentException` | The value is invalid | 400 |
| `KeyNotFoundException` | The record does not exist | 404 |
| `InvalidOperationException` caught by a form route | The current state refuses a valid request | 409 |
| `InvalidOperationException` reaching the handler | A configuration or data fault, such as a missing tax rate | 500, logged |
| `DuplicateRecordException`, `ConcurrencyConflictException` | A unique violation or a concurrency conflict | 409 |

So **this is a deviation from the brief's wording for state conflicts only.** Invalid values are 400, as the brief says.

Browser forms answer with their view and the same status. `ApplicationExceptionHandler` writes Problem Details for anything that reaches it.

## Alternatives considered

- **Return 400 for every domain error.** It matches the brief's wording, but changes Week 2 behaviour and its tests, and tells a client that a valid request was malformed.
- **Add a `DomainException` type.** It breaks the unchanged Domain tests.

## Consequences

- `CreateTransaction_InvalidParcelOrInsufficientStockIsNotPersisted` pins the 409 for a sale.
- The sale, void, refund, stock and administration routes catch their state conflicts. `InvalidOperationException` is also used for configuration and data faults, so `ApplicationExceptionHandler` deliberately has no general mapping for it: a 409 would present a fault as a conflict and stop it being logged. `ApplicationExceptionHandlerTests` pins this. `ConcurrencyConflictException` and `DuplicateRecordException` derive from it and stay 409.
- The trainer needs to confirm this reading of Part G. Changing it would affect the status codes and tests, not any business rule.
