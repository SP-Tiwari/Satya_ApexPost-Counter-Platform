# Architecture and request flow

The layer, domain and pipeline diagrams, the middleware-versus-filter table and the threat model are in [DESIGN.md](../DESIGN.md). This page follows a single counter sale through the system.

## A counter sale, end to end

```mermaid
sequenceDiagram
    autonumber
    actor Clerk
    participant MW as Middleware pipeline
    participant F as MVC filters
    participant C as TransactionsController
    participant S as MixedBasketApplicationService
    participant V as IIdentityVerificationService
    participant U as IUnitOfWork
    participant R as Repositories
    participant P as DomainEventPublisher
    Clerk->>MW: POST /Transactions/Create (cookie, antiforgery token)
    MW->>MW: correlation ID, headers, routing, sign-in cookie, role check
    MW->>F: matched [Audited] action
    F->>F: ValidationActionFilter (ModelState)
    F->>C: Create(model)
    C->>S: CreateAndProcessAsync(staffId, CounterBasketRequest)
    S->>V: verify identity for money orders (with timeout), before the transaction starts
    S->>U: ExecuteInTransactionAsync(work)
    U->>R: work reads staff, branch, products, rates and stock
    R-->>U: current state
    U->>R: work applies rules, deducts stock, stages the basket and payment
    U->>U: SaveChangesAsync once, then commit
    U-->>S: committed
    S->>P: publish TransactionCompleted, StockLow, MoneyOrderIssued, ...
    P-->>P: SimulatedNotificationSender logs notifications
    S-->>C: transaction ID
    C-->>F: RedirectToAction(Details)
    F->>F: AuditActionFilter writes record with status 302
    F-->>Clerk: 302 to /Transactions/{id}
```

1. **Before MVC:** middleware establishes the correlation ID, security headers and identity. `[RequireApiKey]` and `[RequireAdminNetwork]` checks also happen here, though they don't apply to this endpoint.
2. **Validation:** filters run around the action. Invalid input is redisplayed before the action body runs.
3. **Slow external calls:** identity verification runs before the transaction starts, so a slow provider never holds up other sales.
4. **One atomic use case:** every rule check and state change for the sale happens inside one `IUnitOfWork.ExecuteInTransactionAsync` call, with one save. If anything fails, nothing is saved and no events are published. Today this runs on JSON through `SnapshotStore`, which holds a gate across the use case. From M3 it runs on SQL Server through `SqlUnitOfWork` ([ADR 0020](adr/0020-repository-and-unit-of-work-boundaries.md)).
5. **After commit:** events are published only after the save. The audit record is written after the redirect result has executed, so it records the real 302.

## Data and security boundaries

- **Branch authority:** branch scope comes from the persisted staff record behind the authenticated `StaffId`. Branch codes in forms or cookie claims are never trusted for authority.
- **Unattributed legacy sales:** baskets with no branch attribution are not assigned to a branch and do not appear on branch pages.
- **Secrets:** API keys, passwords and credential hashes are not stored in the business snapshot, sample files or audit records. Credentials have their own file, and secrets come from user-secrets or the environment.
- **Audit sample:** `data/AUDIT-SAMPLE.json` holds real `audit-events.jsonl` lines from a local run on seeded demo data, exported unchanged as a JSON array. Runtime audit data is append-only JSON Lines and should be protected as operational data.
- **Single-process locking:** the repository lock coordinates one process only. Separate processes sharing a storage folder are not coordinated.
