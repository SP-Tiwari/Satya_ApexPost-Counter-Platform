# ADR 0004: Atomic transaction processing

- Status: Accepted
- Date: 2026-10-25 (updated 2026-10-29: multi-line baskets, money orders, events)

## Context

Processing a sale can create several goods lines, a shipping line, money-order and fee lines, deduct stock, set payment status and add shipping and money-order records. Saving these separately could leave inconsistent data if a later check fails. Repeated form submissions must not deduct stock twice, and concurrent sales must not oversell.

## Decision

Coordinate the whole sale inside one `IApexPostRepository.UpdateAsync` callback. The repository holds its lock while it loads the current snapshot, applies the callback and saves the result. Within the callback:

1. `SaleProcessingService` checks every line's stock before deducting any.
2. It applies payment, branch and sale details.
3. It returns the updated snapshot with the transaction, shipping and money-order records together.
4. A transaction ID already in the snapshot is rejected.

Slow external work, such as identity verification, runs before the lock is taken. Domain events are collected inside the callback and published only after it commits (ADR 0012).

The application and domain layers enforce the rules. The MVC controller handles binding, authorization, antiforgery and Post/Redirect/Get.

## Alternatives considered

- **Save each change as it happens.** This is simpler, but one failed check midway leaves stock deducted with no sale.
- **Optimistic concurrency (version check and retry).** It scales across processes, but it needs a version field and retry logic. A single in-process lock is enough for one web process on a JSON file.
- **Call the identity provider inside the lock.** That keeps everything in one place, but a slow provider would block every other sale.

## Consequences

A failed callback saves nothing, and duplicate transaction IDs cannot be processed twice. Twenty concurrent one-unit sales against 10 in stock gave exactly 10 successes (confirmed by the trainer's run). This is in-process coordination only; it is not a multi-process transaction guarantee.

Relevant implementation: `src/ApexPost.Application/Services/MixedBasketApplicationService.cs`, `src/ApexPost.Application/Services/TransactionApplicationService.cs`, `src/ApexPost.Domain/Services/SaleProcessingService.cs`, `src/ApexPost.Infrastructure/Persistence/Sql/SqlUnitOfWork.cs`, and `src/ApexPost.Web/Controllers/TransactionsController.cs`.
