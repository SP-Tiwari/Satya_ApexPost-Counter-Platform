# ADR 0023: Which operations are stored procedures, and why

- Status: Accepted
- Date: 2026-10-31

## Context

Part D requires `usp_DailyBranchSales`, `usp_LowStockReport` and `usp_AdjustStock`, and asks why each is a procedure and not a LINQ query. "Stored procedures are faster" is not an accepted reason, and this ADR does not rely on speed. Performance is measured in M6.

## Decision

| Procedure | Why a procedure and not LINQ |
|---|---|
| `usp_DailyBranchSales` | The report is one definition that SSMS users, auditors and the application can all run, with validation and errors in one place. It also fixes the aggregation in T-SQL: the tax expression needs a `LEFT JOIN` per line, and a rounded `decimal(18,2)` result, which ADR 0017 keeps out of the Domain |
| `usp_LowStockReport` | The optional branch filter needs a plan chosen for the value passed. Inside a procedure, one `OPTION (RECOMPILE)` hint on one statement gives that, and the hint stays with the query it applies to. A database role can run this report with `EXECUTE` alone |
| `usp_AdjustStock` | It is a data-integrity operation for callers that have no domain model, such as a head-office stock correction run from SSMS. The balance check and the change are one `UPDATE`, and that change and its history row are one transaction. A caller with only `EXECUTE` cannot change stock without writing history |

### `@BranchCode = NULL`: why `OPTION (RECOMPILE)`

`WHERE (@BranchCode IS NULL OR s.BranchCode = @BranchCode)` has two very different shapes, one branch or all branches. A cached plan built for one would be reused for the other.

| Option | Result | Decision |
|---|---|---|
| `OPTION (RECOMPILE)` | The optimizer sees the actual value and removes the unused branch of the `OR`. Costs a compile on each run | **Chosen.** This is an occasional report, not a high-frequency path, so a compile per run is an acceptable cost for one readable statement |
| Dynamic SQL with `sp_executesql` | Two cached plans, no compile per run | More code, and harder to read and grant permissions for, for a saving this report does not need |
| Two procedures, or `IF` with two queries | Stable plans | The query is written twice |
| `OPTIMIZE FOR` | One fixed plan | Still wrong for the other shape |

Observed on the disposable database: `@BranchCode = 'DM01'` used a **Clustered Index Seek**, and `@BranchCode = NULL` a **Clustered Index Scan** (`docs/evidence/m4/procedures.txt`). That table has only 3 rows, so this shows the plan shape, not a performance gain.

### Calling them

`SqlStoredProcedures` uses `Database.SqlQuery` with typed `SqlParameter` values, so no value is part of the SQL text. A test passes `x';DROP--` as a branch code and it is treated as a value. `THROW` numbers map to the exceptions the services already throw:

| `THROW` | Meaning | Exception |
|---|---|---|
| 51000 | Invalid parameter | `ArgumentException` |
| 51001 | Not found | `KeyNotFoundException` |
| 51002 | Not authorized | `UnauthorizedAccessException` |
| 51003 | State conflict, such as insufficient stock | `InvalidOperationException` |

### Deployment

`sql/procedures/*.sql` holds the current text of each procedure. `sql/procedures/history/<migration>/` holds the text that migration deployed, and it must not change. Only these snapshots are embedded in Infrastructure, so a later change to a procedure cannot change an existing migration. The `AddStoredProcedures` migration wraps each snapshot in `EXEC(N'...')`, because the idempotent script runs migrations inside an `IF` block, where `CREATE OR ALTER` is a syntax error.

A later change to a procedure needs a new migration, so existing databases receive it. `AddDailyBranchSalesRecompile` is the first ([ADR 0024](0024-index-choices.md)). Such a migration:

- adds a snapshot of the new text in its own history folder, with its own `EmbeddedResource` line;
- applies that snapshot with `ALTER PROCEDURE`, which fails if the procedure is missing, through `StoredProcedureScripts.ReadSnapshotAsAlter`;
- runs it as dynamic SQL, because `ALTER PROCEDURE` must start a batch and the idempotent script runs each migration inside an `IF` block;
- restores the earlier text in `Down` from the earlier migration's snapshot.

**Why `sp_executesql`.** Dynamic SQL is the technical requirement, and `EXEC(N'...')` would meet it equally well. `sp_executesql` passes no parameters here and gives no technical benefit. It was chosen so that `EXEC(N'--` in the script still means a statement that creates a procedure, which `IdempotentScript_CreatesEachProcedureThroughExec` counts. That test now also counts the `ALTER` and checks that it belongs to `AddDailyBranchSalesRecompile`.

`Procedures_MatchTheRepositoryFilesAfterAllMigrations` fails if `sql/procedures` changes without such a migration, because it compares the database after all migrations with the current files.

`ProcedureMigrationTests` checks the rest: the hash of each snapshot, that only snapshots are embedded, that each migration deploys its own snapshot, the stop at `AddStoredProcedures`, and upgrade, rollback and re-upgrade with a data row.

`AddDailyBranchSalesRecompile` is the only source of `OPTION (RECOMPILE)` in `usp_DailyBranchSales`. [recompile-migration.txt](../evidence/m6/recompile-migration.txt) records fresh installation, upgrade from the committed script, rollback and re-upgrade. A procedure deployed by the idempotent script differs from its file only in leading spaces, because EF Core indents the script block, including inside the `EXEC` string.

### Least privilege

`sql/security/least-privilege-role.sql` creates the `apexpost_app` database role. The application login needs:

| Permission | On | Why |
|---|---|---|
| `SELECT, INSERT, UPDATE` | Tables the application changes | Normal operation |
| `SELECT, INSERT` | `AuditEntries`, `Payments`, `StockAdjustments`, `TransactionLines`, `TransactionTaxRates` | History is added to, never changed |
| `SELECT` | `Roles`, `ProductCategories` | Fixed by migrations |
| `EXECUTE` | The three procedures | |
| None | `DELETE`, `ALTER`, `CREATE`, `__EFMigrationsHistory` | Nothing in the application deletes rows. Migrations run under a separate deployment identity |

A test runs a real sale and `usp_AdjustStock` as a user holding only this role, and confirms it is refused `DELETE`, `UPDATE` on history tables and `DROP TABLE`. The role is not applied to `ApexPostDb` by any migration, because the login depends on the environment.

## Alternatives considered

- **LINQ for all three.** Reports can be LINQ, and M5 uses it for three of them, but Part D requires these procedures, and LINQ gives no `EXECUTE`-only boundary for non-application callers.
- **Route the stock screen through `usp_AdjustStock`.** Not done. `StockAdjustmentApplicationService` raises the low-stock event from the domain, which a procedure cannot do, and M4 must keep existing behaviour.

## Consequences

- **The adjustment rules exist twice:** in the domain and in `usp_AdjustStock`. Tests cover each rule in the procedure, but a change to the rule must be made in both places.
- **Screen use since M5.** `usp_LowStockReport` backs the low-stock report and dashboard. `usp_DailyBranchSales` is not on a screen, because it rounds and those pages show unrounded amounts. The stock screen stays on the domain path, so `usp_AdjustStock` is not on one either.
- **`usp_AdjustStock` raises no low-stock event.**
- **A retry risk for `usp_AdjustStock`:** the call goes through the context's execution strategy. If a connection failed after the commit and before the reply, a transient-failure retry could apply it twice. It has no idempotency key because its parameters are fixed by the brief. A caller should check `StockAdjustments` after a connection failure.
- An EF Core read taken before `usp_AdjustStock` becomes stale, because the rowversion changes, so a later `SaveChanges` gets the normal conflict. A test covers this.
