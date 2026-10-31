# ADR 0024: Index choices from execution-plan evidence

- Status: Accepted
- Date: 2026-10-31

## Context

Part C requires indexes designed from measured workloads, with the order of key columns and any `INCLUDE` columns justified. The baseline on 2 million sales showed full clustered scans for daily sales, shipment search and all-branch low stock. Every number below comes from [PERFORMANCE.md](../../PERFORMANCE.md).

## Decision

`AddPerformanceIndexes` adds or replaces five indexes. It applies after `InitialCreate` and `AddStoredProcedures`, and the idempotent script includes it. All five were reviewed and kept after M6.

| Index | Key | Include | Why this order and these includes |
|---|---|---|---|
| `IX_CounterTransactions_BranchCode_OccurredAtUtc` | `BranchCode`, `OccurredAtUtc` | `Status`, `Currency` | Equality before range: the seek reaches one branch, then stays in date order. Reversing them would read every branch's rows for the date. `Status` is tested and `Currency` returned, but neither narrows the seek much, so they are not key columns |
| `IX_TransactionLines_TransactionId` | `TransactionId` | `LineType`, `Quantity`, `UnitPrice`, `TaxCategory`, `ShipmentId` | Every column a day's lines are read for, which removes the key lookups |
| `IX_Shipments_ZoneCode_ServiceLevel` | `ZoneCode`, `ServiceLevel` | `ShipmentType`, `WeightKg` | Both keys are equality tests. `ZoneCode` stays first so the index also serves its foreign key. The includes cover the search |
| `IX_MoneyOrders_ProcessingBranchCode` | `ProcessingBranchCode` | `Status`, `Amount`, `Currency`, `Fee`, `Sender`, `Recipient` | With only `Status` included, the branch list stayed at 9,597 reads, because a lookup per order costs more than a scan. Covering the list took it to 2,704 |
| `IX_BranchStock_Quantity` | `Quantity` | None | A range on `Quantity` across all branches. `BranchCode` and `Sku` are the clustered key, so they are already in the index |

The first and third indexes replace `IX_CounterTransactions_BranchCode` and `IX_Shipments_ZoneCode`, whose columns they start with, so no foreign key loses its index.

**`IncludeProperties` limit.** It cannot name a complex property, so `UnitPrice`, `Amount`, `Currency` and `Fee` are in the reviewed migration, and the model lists only the scalar includes. The harness saves index definitions from the migration and from the measured database, and they are identical.

**`usp_DailyBranchSales` gains `OPTION (RECOMPILE)`.** Its dates are local variables, so the optimizer guessed 24,612 rows where 71 were actual, and chose joins that took 1.3 seconds. With the hint it takes 1 ms. The results do not change, and each call pays one compile, which is acceptable for a report that is not on a screen. The separate `AddDailyBranchSalesRecompile` migration deploys it, as ADR 0023 requires, and its `Down` restores the earlier text.

## Alternatives considered

- **`(OccurredAtUtc, BranchCode)` on `CounterTransactions`.** Every report filters to one branch, so leading with the date reads more.
- **A filtered index on `Status = 'Paid'`.** 90% of rows are paid, so it would save little, and `BranchCode` still needs an index for its foreign key.
- **An index for low stock at one branch.** Not needed: `PK_BranchStock` already seeks in 22 reads.
- **A composite `(BranchCode, Quantity)` on `BranchStock`.** It would serve only the case that needs no help, and not the all-branch report.
- **An index for the shipping and auditor reports.** They aggregate whole tables, so an index can only narrow the scan.

## Consequences

- The indexes add 284 MB and 10.8% more log per write. Write time did not change measurably.
- **`IX_BranchStock_Quantity` is the weakest choice.** Reads fall from 8,562 to 133, but elapsed time does not improve, its only user is the all-branch mode of `usp_LowStockReport`, which no screen uses, and every sale pays to maintain it (3.8% of log). It stays because Part D requires that mode. It is the first to remove if write load matters more.
- **`IX_Shipments_ZoneCode_ServiceLevel` slows the shipping volume report.** CPU rises from 140 to 249 ms, because the plan leaves batch mode. Shipment search falls from 13,771 reads to 61, which justifies it.
- The covering money-order index stores customer names twice. They are fictional here and are not identity documents, and its includes hold no document data.
- The clustered GUID key fragments under random inserts, as measured in `PERFORMANCE.md`. It stays for the offline design, and production would need index maintenance.
- A database that already has the earlier procedure text receives the hint from `AddDailyBranchSalesRecompile`. [recompile-migration.txt](../evidence/m6/recompile-migration.txt) records the upgrade, rollback and repeat runs. The migration deploys the same text that was measured, so the measurements stand.
