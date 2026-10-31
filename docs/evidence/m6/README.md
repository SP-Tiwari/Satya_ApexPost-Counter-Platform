# M6 performance evidence

Saved output from `scripts/run-performance.ps1`, run on 2026-10-31 on a disposable `ApexPostDb_Perf` database, which was dropped afterwards. All data is fictional. `ApexPostDb` was not used. [PERFORMANCE.md](../../../PERFORMANCE.md) explains the results.

| File | Shows |
|---|---|
| `dataset.txt` | Row counts and load time |
| `measurements-before.csv`, `measurements-after.csv` | Rows, logical reads, median CPU and elapsed time, reads by table, and plan shape for each workload |
| `plans/*.sqlplan` | Actual execution plans, which open in SSMS. One before and after pair per workload, plus the studies |
| `write-cost-*.csv` | Three runs of the write workload as milliseconds, log bytes and log records. The first run in each file pays for page splits and for cold pages |
| `index-sizes-before.txt`, `index-sizes-after.txt` | Used MB per index. Sizes of untouched indexes also grew, because the write workload's rollbacks leave page splits |
| `index-build-seconds.csv` | Build time per candidate index |
| `index-definitions-migration.txt`, `index-definitions-measured.txt` | Index definitions from the migration and from the measured database. The two files are identical |
| `studies.txt` | SARGability, implicit conversion, estimates and fragmentation |
| `fragmentation-fresh.txt` | `CounterTransactions` fragmentation straight after the load |
| `query-store.txt` | Query Store runtime history per query and plan |
| `recompile-migration.txt` | Fresh installation, upgrade from the committed script, rollback, re-upgrade and idempotent script runs, showing the deployed `usp_DailyBranchSales` text at each step, and the procedure resources in the built assembly. From 2026-11-01. The harness did not produce this file |

To repeat the run, use `./scripts/run-performance.ps1`. The database files start at 12 GB on E:. Timings will vary, while logical reads should stay close.
