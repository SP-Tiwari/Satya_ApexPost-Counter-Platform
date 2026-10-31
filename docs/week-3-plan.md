# Week 3 delivery plan: SQL Server persistence

Week 3 of 12. Source brief: `AssignmentWeek03.txt`. Checked against the code and this machine on 2026-10-30.

**Goal:** replace JSON persistence with SQL Server and EF Core 10, and prove the business logic did not change. Later weeks (APIs, security, mobile, DevOps) will build on this schema and these data-access boundaries, so the decisions below are made to last.

## 1. Starting point

| Item | Status |
|---|---|
| Tests | 549 passing: Domain 213, Application 110, Infrastructure 33, Web 193 |
| SQL Server | Default instance is running: SQL Server 2025 (17.0), Enterprise Evaluation. `ApexPostDb` does not exist yet |
| Access | Windows authentication only. TCP 1433 is closed, so connect with `Server=.` |
| Tools | LocalDB, SSMS 22, sqlcmd and `dotnet-ef` 10.0.14 are installed. Docker is not |
| Packages | Use EF Core 10.0.14 to match `dotnet-ef` |
| Disk | **C: has 6.4 GB free and SQL Server's default data path is on C:. E: has 149 GB free** |
| Persistence seam | `IApexPostRepository` is used by 35 files (173 matches, tests included), and it loads the whole data set for every operation |

### What the domain does not model yet

The brief's tables need these additions:

- `Roles` and `ProductCategories`: enums today.
- `ShippingZones`: a plain string on `ShippingRate` today.
- `Shipments`: `ShippingItem` today, needs a table and links.
- `Payments`: only a method name on `CounterSaleDetails` today.
- `StockAdjustments`: no history is kept today.
- `AuditEntries` and staff credentials: JSON files today.
- `Address`: not on `Branch` today. It is the brief's example of an owned type.

## 2. Decisions and conflicts to settle first

These are the places where the brief and the existing code collide. Each needs an ADR.

| # | Issue | Decision |
|---|---|---|
| 1 | "Same application interfaces" conflicts with Part F, which wants aggregate repositories and no load-everything calls | Retire `IApexPostRepository`. Add `IBranchRepository`, `IProductRepository`, `IStockRepository`, `ICounterTransactionRepository`, `IStaffRepository`, `IUnitOfWork` and query services. **Application service signatures stay the same**, so controllers barely change |
| 2 | "Week 2 tests pass unchanged", yet Application tests use the snapshot fake | `tests/ApexPost.Domain.Tests` must have **zero diff**: that is the proof. Application test assertions stay. The snapshot moves into the test doubles as an in-memory store behind the new interfaces, so only the setup changes |
| 3 | `decimal(18,2)` conflicts with unrounded maths: `Money` does not round and nothing in `src` calls `Math.Round`, so tax and per-kg shipping can produce more than 2 decimals, which SQL would silently round | **Not decided yet.** M1 starts by inspecting every `Money` and tax calculation and recording real examples, and only then chooses the rounding strategy. Likely direction: store the tax rate and a rounded tax amount per line. Any behaviour change gets its own ADR and tests, and must not alter a Domain test |
| 4 | "Domain errors → 400" conflicts with Week 2, where insufficient stock and bad state return 409 (tests pin this and the trainer praised it) | A new `DomainException` type would break the Domain tests, because xUnit `Assert.Throws<T>` matches the exact type. Keep the existing types. Map `ArgumentException` → 400, state conflicts → 409, `KeyNotFoundException` → 404. **Record this as a deliberate deviation** |
| 5 | The dashboard runs work in parallel, but a `DbContext` cannot be used concurrently | Do all DbContext reads one after another. Keep `Task.WhenAll` for the courier lookups and the cache, which run in parallel |
| 6 | `ReferenceDataCache` is a singleton that depends on the repository, so it cannot use a scoped DbContext | It loads through `IServiceScopeFactory`, in a short-lived scope |
| 7 | A rowversion on stock makes two clerks conflict even when stock is sufficient, causing false 409s | Retry the whole use case a few times on `DbUpdateConcurrencyException`. Each attempt clears the change tracker and **reloads current database state**, then re-runs every business rule; no value from a failed attempt is reused. Events are collected per attempt, and only the committed attempt's events are published, **exactly once, after commit**. If the stock is really gone, the retry gets the normal "insufficient stock" 409. The Week 2 result of exactly 10 sales out of 20 may not hold under a retry limit, so the test asserts: stock never negative, units sold match the stock decrease, and one event set per committed sale |
| 8 | Audit writes must not break the sale. Today, a failed audit write after a successful commit escapes the filter and the clerk gets a 500 for a sale that went through (`AuditActionFilter.cs:170` only catches when the action already failed) | Write audit entries in **a separate scope and commit**, so a rollback cannot erase the record of a failed attempt. Always catch and log audit failures. The ADR documents this transaction boundary. Tests cover the two failures separately: a failed audit write leaves the sale committed and the clerk's response unchanged; a failed sale rolls back fully and its audit entry is still written |
| 9 | `Money` has two parts, so a single-column value converter cannot map it | Use complex/owned columns for `Money` and value converters for `Sku`, `BranchCode` and `Weight`. State this in the ADR, because the brief lists Money as a converter |
| 10 | The basket's tax-rate dictionary and notifications do not map cleanly to tables | Use a separate persistence model and mapper for `CounterTransaction`, reusing `Reconstitute`, as well as `IdentityVerification` (which the brief requires). Other aggregates map directly, using shadow properties for rowversion and audit columns |
| 11 | The mask length depends on the document length, so `varchar(N)` needs a limit | Add a maximum identifier length to the domain, then use a matching column length and a CHECK that allows only `*` before the last 4 characters |
| 12 | The title and Lab Module 9 mention offline-first terminals, but no Part, deliverable or mark covers them | **Design only this week; the full offline workflow needs separate approval.** One ADR covers transaction IDs, idempotency and how a future Outbox would work. The schema must keep that path open: client-generated GUID transaction IDs stay as primary keys, and a repeated ID is rejected safely. Ask the trainer to confirm. Module 8 is also missing from the list |
| 13 | The brief's allowed packages exclude Testcontainers, and Docker is not installed | Test fixtures create a uniquely named database through migrations, then drop it |

Other rules: no `EnsureCreated()`, and the app does not migrate itself at startup (migrations run from the command line or script). Financial rows use `Restrict` deletes. Stock rows are updated in SKU order, which M2 must verify, to avoid deadlocks.

### Standing rules for every milestone

1. **Domain rules are preserved.** Existing Domain business rules and test assertions are never weakened or removed to accommodate EF Core.
2. **Async only.** Every database operation is asynchronous where the API supports it, and every call passes a `CancellationToken` through. No `.Result`, `.Wait()` or `Thread.Sleep`.
3. **Tests never use the development database.** `ApexPostDb` is off limits to every test. Integration tests use a uniquely named disposable database. The test host blanks `ConnectionStrings:ApexPostDb` with `UseSetting`, because removing the user-secrets source happens too late for values `Program.cs` reads directly.
4. **One milestone at a time.** Each milestone ends with a report and waits for approval. Work does not move ahead of the approved milestone.
5. **Offline stays a design.** No offline workflow or Outbox is built this week without approval.
6. **Commits stay clean.** Unrelated files are preserved. `Assignment.txt` and `trainer_evaluation_remarks.txt` stay out of commits.

## 3. Milestones

Work in order. Each milestone is one or more small PRs into a `feature/week3-sql-persistence` branch. That branch merges to `master` only when M3 is green, so `master` always works.

### M0: Foundation

- Add EF Core 10.0.14 to Infrastructure, and Design to Web.
- Create `ApexPostDb` with its files on **E:**, through a repeatable setup script.
- Put the connection string in user-secrets. Windows auth means it contains no password, but the rule still applies.
- Add a Development-only `/health/database` endpoint to prove the app can reach the database. It connects only when requested, so nothing connects at startup or during other tests. There is no `DbContext` yet, and JSON persistence stays in use.

**Done when:** build has zero warnings, the 549 existing tests pass, and the app can open the database.

### M1: Schema and mapping (Parts A, E): 15 + part of 15 marks

- First inspect the `Money` and tax calculations and settle decision 3.
- Add the missing domain concepts from section 1, with private EF constructors only where needed.
- Write `ApexPostDbContext` and one `IEntityTypeConfiguration` per entity.
- Add keys, UNIQUE, CHECK and DEFAULT constraints, audit columns, and rowversion on `BranchStock` and `CounterTransactions`.
- Create `InitialCreate` and review it by hand. Generate the idempotent script into `sql/schema/`.

**Done when:** the migration applies to an empty database, the Domain tests have zero diff, and the ER diagram and normalization note (three anomalies) are in `DESIGN.md`.

### M2: Repository, Unit of Work and command services (Part F): 20 marks

- Add the interfaces to Application and the EF implementations to Infrastructure.
- Repositories only stage changes, never return `IQueryable`, and never call `SaveChanges`.
- Move services one at a time, simplest first; `MixedBasketApplicationService` and `TransactionApplicationService` go last.
- A sale commits in one `SaveChangesAsync`. Events publish only after commit, and never again on a retry.
- Use an execution strategy around explicit transactions.
- Add `ExecuteUpdateAsync` for price revision, and input model → command → entity mapping.

**Done when:** `IApexPostRepository` is gone, Application tests pass with the fakes and a spy Unit of Work (commit once, never on failure), and over-posting tests pass.

### M3: Cut over the platform (Part G, seed)

- Store audit entries and credentials in SQL.
- Build the seeding service: 5 branches, 20 staff, 30 products, 10 zones/rates, **200+ transactions** (the current seed has 40).
- Add the `DbUpdateConcurrencyException` and unique-violation mappings to Problem Details. The current handler always titles errors "An unexpected error occurred", which has to change.
- Move all Web tests to a disposable database and update `run-demo.ps1`.

**Done when:** every screen works on SQL Server, the 193 Web tests pass against it, and no JSON repository remains in `Program.cs`. Merge to `master`.

### M4: T-SQL (Parts B, D): 10 + 10 marks

- Five reporting queries in `/sql`, and the atomic sale script (`XACT_ABORT`, `TRY…CATCH`, `XACT_STATE()`, `THROW`).
- `usp_DailyBranchSales`, `usp_LowStockReport` and `usp_AdjustStock`, deployed through a migration and called only through parameterized APIs.
- The blocking demonstration, the deadlock note and the least-privilege role note.

**Done when:** procedures are tested, and the ADR justifies each one without relying on "faster".

### M5: Query services (Part E rules, Part G)

- Reports and the dashboard use `AsNoTracking` DTO projections or stored procedures, with `AsSplitQuery` where a query would multiply rows.
- Use Development command logging to prove there are no N+1 queries.

**Done when:** reports return expected totals through real SQL, and `ToQueryString()` output is saved for 3+ queries.

### M6: Performance evidence (Part C): 15 marks

- Build a separate `ApexPostDb_Perf` database **on E:** with a repeatable set-based data generator, sized for millions of rows.
- For at least 4 workloads, save `.sqlplan` files plus `STATISTICS IO/TIME` results before and after each index, along with write and storage costs.
- Fix one non-SARGable predicate, and investigate one estimated-vs-actual row mismatch.
- Add the chosen indexes in the **required follow-up migration, `AddPerformanceIndexes`**: reviewed by hand, applied after `InitialCreate`, and included in the regenerated idempotent script. M4's stored-procedure migration is a further follow-up, but it does not replace this one.

**Done when:** every number in `PERFORMANCE.md` comes from a saved run, any scans that remain are explained, and `AddPerformanceIndexes` is applied and in the script.

### M7: Remaining tests (Part H)

Rollback of a failed sale, a real two-connection concurrency conflict returning 409, repository translation, stored procedures, void requiring a Branch Manager, and Problem Details.

**Done when:** all suites pass, and they never touch the development database.

### M8: Documentation and submission: 15 marks

- ADRs from 0016 up. The brief needs 7; we expect about 10, covering section 2. ADR 0001 (JSON) is marked superseded.
- Update `README.md`, `DESIGN.md` (diagrams, ER, SOLID mapping) and `docs/architecture.md`.
- Final constraint check: no warnings, no `.Result`, no concatenated SQL, no secrets, identity documents masked everywhere.

## 4. Deliverable checklist

Each item from `AssignmentWeek03.txt` is checked off against the repository at M8. An item is ticked only when it exists and has been verified.

| Brief | Required | Milestone |
|---|---|---|
| A | Tables: Branches, Counters, Staff, Roles, Products, ProductCategories, BranchStock, StockAdjustments, ShippingZones, ShippingRates, Shipments, MoneyOrders, IdentityVerifications, CounterTransactions, TransactionLines, Payments, AuditEntries | M1 |
| A | PK/FK with justified delete behaviour; UNIQUE on BranchCode, Sku, staff email; CHECK on quantities, amounts, statuses, weight; DEFAULTs; audit columns; rowversion on BranchStock and CounterTransactions | M1 |
| A | Data types (`decimal(18,2)`, `date`/`datetime2`, `varchar` codes, `nvarchar` names); masked documents enforced in domain and column length | M1 |
| A | ER diagram, normalization note with three anomalies, idempotent script | M1 |
| B | Five reporting queries in `/sql`; atomic T-SQL sale; blocking demo with isolation level; deadlock note | M4 |
| C | 4+ workloads with plans, IO/CPU, index justification, re-measurement, write and storage cost | M6 |
| C | One SARGability fix; one estimate mismatch; `PERFORMANCE.md` with plans, tables, conclusions | M6 |
| D | `usp_DailyBranchSales`, `usp_LowStockReport`, `usp_AdjustStock`; parameterized calls; RECOMPILE note; least-privilege note; ADR | M4 |
| E | `ApexPostDbContext`, Fluent configuration per entity, converters, owned types | M1 |
| E | `InitialCreate`, **follow-up migration**, idempotent script, no `EnsureCreated()` | M1, M6 |
| E | Seed: 5 branches, 20 staff, 30 products, 10 zones/rates, 200+ transactions | M3 |
| E | `AsNoTracking` DTO reads, `Include` only for aggregates, `AsSplitQuery`, N+1 proof by logging | M5 |
| E | One `SaveChangesAsync` per sale, `ExecuteUpdateAsync` price revision, concurrency handling, explicit transactions with execution strategy, `ToQueryString()` samples | M2, M5 |
| F | Dependency direction; aggregate repositories; no `SaveChanges` or `IQueryable` in repositories; `IUnitOfWork`; one commit per use case; all scoped | M2 |
| F | Rich entities, shadow properties, separate persistence entity and mapper with ADR | M1, M2 |
| F | Request and response DTOs, explicit input → command → entity mapping, over-posting proof | M2 |
| F | Query/command separation, stating that no separate databases are required | M5 |
| F | Week 2 business-rule tests unchanged | M2, re-checked each milestone |
| G | Dashboard, catalogue, stock, transactions, voids, refunds, money orders and reports on SQL | M3, M5 |
| G | `AuditEntries` through `IAuditStore`, justified boundary; Problem Details mappings; connection string in user-secrets | M0, M3 |
| H | Unit tests with spy Unit of Work; SQL integration tests; WebApplicationFactory tests on a test database | M2, M3, M7 |
| ADRs | The 7 required topics, plus section 2 decisions | M1–M8 |
| Docs | `README.md`, `DESIGN.md` (architecture, layers, ER, ADRs, SOLID), `PERFORMANCE.md`, `/sql`, seed | M8 |
| Constraints | .NET 10, C# 14, nullable, zero warnings, allowed packages only, async with `CancellationToken`, no concatenated SQL, scoped lifetimes, no secrets, masked identity documents, thin `Program.cs` | Every milestone |

## 5. Things only you can do

- Take the SSMS screenshots if the trainer wants them: the blocking demo with two sessions, and the execution plans. The plans can also be saved as `.sqlplan` files by script.
- Ask the trainer about offline scope (decision 12) and the 409 deviation (decision 4).
- Approve pushes and pull requests.
- Decide about `Assignment.txt` and `trainer_evaluation_remarks.txt`: both show as deleted in the working tree, and `AssignmentWeek03.txt` is untracked. Keep these out of the feature commits.

## 6. Risks

| Risk | Response |
|---|---|
| The service refactor (M2) is the largest and riskiest step | Move one service at a time, with tests passing after each |
| Filling C: with database files | Put both databases on E: explicitly |
| SQL-backed Web tests are slower and share state | Run them in a single xUnit collection, and reset data between tests |
| Enterprise Evaluation expires (normally after 180 days) and behaves differently from LocalDB Express | Record the edition in `PERFORMANCE.md`, and avoid features the later weeks might not have |
| Short on time | Keep the order M0–M3 first. M6 is worth 15 marks but needs M3, so do not leave it to the end |
