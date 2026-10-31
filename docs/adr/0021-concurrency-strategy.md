# ADR 0021: Concurrency strategy for stock and transactions

- Status: Accepted
- Date: 2026-10-31

## Context

Two clerks selling the last unit must not drive stock negative. `BranchStock` and `CounterTransactions` have rowversion columns, so a change made from stale data fails with `DbUpdateConcurrencyException`. Rowversion alone also rejects a sale when stock is plentiful but another clerk touched the same row, which would be a false conflict.

## Decision

**Optimistic concurrency with a bounded retry of the whole use case**, in `SqlUnitOfWork.ExecuteInTransactionAsync`:

1. Each attempt clears the change tracker, so the use case **reloads current state** and re-runs every business rule. No tracked value from a failed attempt survives.
2. A conflict waits a short random time and retries, up to 5 attempts.
3. If stock has really run out, the retried rule raises the normal "insufficient stock" error, which the controllers answer with 409.
4. A conflict on every attempt throws `ConcurrencyConflictException`. It derives from `InvalidOperationException`, which the controllers already answer with 409.

**Events are published exactly once.** A service collects events in a variable that each attempt replaces, and publishes after `ExecuteInTransactionAsync` returns, so only the committed attempt's events go out.

**Execution strategy.** The transaction runs inside `CreateExecutionStrategy().ExecuteAsync`, which `EnableRetryOnFailure` requires for explicit transactions. It retries transient failures, also from a cleared change tracker.

**Deadlocks.** `GetForProductsAsync` orders stock rows by SKU, so concurrent sales read them in one order. EF Core's write order is not asserted here; M4's deadlock note covers it. A deadlock victim is a transient failure, which the execution strategy retries.

**Identity verification keeps its result across retries.** It runs once before the transaction, because it comes from an external provider, not from the database state a conflict makes stale.

**A repeated transaction ID** is rejected by `ExistsAsync`. Two simultaneous submissions of one ID are stopped by the primary key, which `SqlUnitOfWork` turns into `DuplicateRecordException` and the controllers answer with 409.

## Alternatives considered

- **Pessimistic locks (`UPDLOCK`).** No false conflicts, but they need raw SQL in the read path, hold locks longer, and the brief asks for rowversion.
- **A conditional `UPDATE ... WHERE Quantity >= @n`.** Efficient, but it bypasses `BranchStock.RemoveStock` and moves a business rule into SQL.
- **Return 409 on the first conflict.** Simple, but clerks would see failures while stock is available.
- **Retry in each application service.** It would repeat the same loop in every use case, and Application cannot reference EF Core's exception.

## Consequences

A disposable-database probe on 2026-10-31 gave these results. `SqlPersistenceTests` now repeats each scenario.

| Scenario | Result |
|---|---|
| 20 concurrent one-unit sales of 10 units | 10 sold, 10 insufficient stock, 0 unresolved conflicts, stock 0, 10 completed events |
| A rival change forced during the first attempt | 2 attempts; the second read 7 where the first read 8 |
| A rival change forced on every attempt | 5 attempts, then `ConcurrencyConflictException` |

The first result depends on timing. A heavier load can exhaust the retries while stock remains, so the guarantee is that stock is never negative and units sold match the stock decrease, not that every available unit sells.
