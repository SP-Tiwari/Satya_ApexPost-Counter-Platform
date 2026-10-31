# ADR 0020: Repository and Unit of Work boundaries

- Status: Accepted, except the `Refund` ledger entries, which are pending trainer confirmation
- Date: 2026-10-31

## Context

`IApexPostRepository` loads and saves the whole data set for every operation, which cannot work on SQL Server. Part F of the brief asks for specific repositories, a Unit of Work and one commit per use case. M2 had to move the application services onto these while the Web application stays on JSON until M3, and without changing any test.

## Decision

**Interfaces live in Application, implementations in Infrastructure.**

| Interface | SQL Server implementation | Holds |
|---|---|---|
| `IBranchRepository` | `SqlBranchRepository` | Branches and their counters |
| `IProductRepository` | `SqlProductRepository` | Products and shipping rates, which together price a sale |
| `IStockRepository` | `SqlStockRepository` | Branch stock and adjustment history |
| `ICounterTransactionRepository` | `SqlCounterTransactionRepository` | Whole transactions, payments, money orders |
| `IStaffRepository` | `SqlStaffRepository` | Staff |
| `IUnitOfWork` | `SqlUnitOfWork` | The commit |

**Rules**
- Repositories stage changes and never call `SaveChanges`.
- No repository returns `IQueryable`.
- Each use case commits once, through `IUnitOfWork.ExecuteInTransactionAsync`.
- All SQL types are scoped and share one `DbContext` per request (`AddApexPostSqlPersistence`).
- `IProductRepository.RevisePricesAsync` is the one exception to "stage only". `ExecuteUpdateAsync` applies at once, so it runs inside `ExecuteInTransactionAsync`.

**`SnapshotStore` is a temporary bridge.** It implements the same six interfaces over `IApexPostRepository`. Services take the new interfaces in their main constructor, and keep a second constructor taking `IApexPostRepository` that creates a `SnapshotStore`. This keeps the Web application on JSON, and existing tests run unchanged against the refactored services. A gate per snapshot repository is held from a use case's first read to its commit, which keeps read-change-save atomic as `UpdateAsync` did. The bridge and these constructors are removed in M3.

**Migrated in M2:** `TransactionApplicationService`, `MixedBasketApplicationService`, `StockAdjustmentApplicationService`, `AdministrationApplicationService`, `StaffAuthenticationService`, `MoneyOrderApplicationService`, `ProductCatalogueApplicationService`.

**Not migrated:** the report services, `BranchDashboardApplicationService`, `ParcelStatusApplicationService` and `ReferenceDataCache`. They are reads, and they still use `IApexPostRepository` until M3.

**Where direct `DbContext` use is acceptable:** only in Infrastructure. M5's query services may project directly from the `DbContext`, behind Application interfaces.

**Recording money movements (`Payments`):** this section separates what the brief requires from what the code does.

| | Status |
|---|---|
| **Required by the brief** | A sale saves its payment with the transaction and lines (`AssignmentWeek03.txt`, line 104) |
| **Implemented, pending trainer confirmation** | A refund writes a `Refund` entry. Voiding a **paid** sale also writes a `Refund` entry |
| **Implemented, pending trainer confirmation** | Both entries use the sale's reloaded total and the sale's method |

The two `Refund` entries are **inferred, not approved**. No source requires them: the brief mentions voids and refunds without any ledger entry, the trainer's remarks ask only for stock to be returned, and ADR 0011 covers stock and money orders only. They were added so that the ledger would not show money taken and never returned. They are not final business rules until the trainer confirms them, and no ledger behaviour should be built on them before then.

**Known defect in these entries:** a `Refund` entry can differ from its `Payment` entry by a small amount. See [ADR 0017](0017-money-precision-and-rounding.md). The fix is held until the trainer confirms the rule.

Voiding an open basket writes nothing. A basket without sale details, or with a zero total, writes no entry. `SnapshotStore` keeps no payments or stock adjustment history, because the JSON snapshot has no place for them.

**Identity verification:** `SqlCounterTransactionRepository` loads `IdentityVerificationRecord` rows in one query per read, and restores them with `MoneyOrder.RestoreIdentityVerification`, which never replaces a recorded value.

**Audit columns:** `AuditColumnsInterceptor` stamps `ModifiedAtUtc` on updates, and `CreatedBy` from `ICurrentStaffAccessor` on inserts. When no staff member is known, the database default applies. `ExecuteUpdateAsync` bypasses interceptors, so `RevisePricesAsync` sets `ModifiedAtUtc` itself.

**Input limits:** `InputLimits` and `[MaximumLength]` check text lengths and the largest number each decimal column can hold (money, weight, dimensions, a sale's total and a revised price), so an oversized value is rejected with a clear message instead of failing in SQL Server. These are storage limits, not business rules.

**After a failure:** `SqlUnitOfWork` clears the change tracker when a use case fails, so nothing from the failed attempt reaches a later read in the same request. A successful commit keeps its tracked entities.

## Alternatives considered

- **Keep services on `IApexPostRepository` until M3.** No bridge is needed, but nothing would exercise the refactored services until the cutover, and M3 would combine the riskiest refactor with the database change.
- **Change the tests to use new fakes now.** Cleaner, but tests were out of scope for M2.
- **Make `IApexPostRepository.UpdateAsync` asynchronous.** Services could then keep their shape, but its callback is synchronous, and changing the interface would break the test fake.
- **A generic `IRepository<T>`.** It would need `IQueryable` or expression parameters to stay useful, which the brief rules out.
- **Add a `Void` entry type to `Payments`.** It repeats what the sale's status already says.

## Consequences

- Services are ready for SQL Server, and M3 changes the composition rather than the use cases.
- Until M3, some reads load the JSON file more than once per page. A catalogue page also reads sequentially instead of in parallel, because a `DbContext` cannot run two operations at once.
- `SnapshotStore` reports duplicate records as `InvalidDataException`. Some lookups previously treated a duplicate staff ID as unauthorized. Both refuse the request, and SQL keys prevent duplicates.
- Reports, the dashboard, parcel status and `ReferenceDataCache` still read JSON. M3 gives them minimum SQL-compatible reads, and M5 replaces those with optimized query services. See the plan.
