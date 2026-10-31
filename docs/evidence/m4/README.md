# M4 T-SQL evidence

Every `.txt` file here is saved output from `scripts/run-sql-demos.ps1`, run on 2026-10-31 on SQL Server 17.0.1000.7. The script creates a disposable `ApexPost_Test_SqlDemo_*` database on E:, applies the idempotent schema script twice, loads `sql/demos/demo-fixture.sql`, and drops the database at the end. All data is fictional. `ApexPostDb` is not used.

To repeat it, run `./scripts/run-sql-demos.ps1`. Session IDs and timings change on each run, and either deadlock session can be the victim.

| File | Shows |
|---|---|
| `schema.txt` | Both migrations and the three procedures present after two runs of the idempotent script |
| `reports.txt` | Output of the five queries in `sql/reports` |
| `procedures.txt` | Results and `THROW` errors from each procedure, and the plan operators with `OPTION (RECOMPILE)` |
| `atomic-sale.txt` | A committed sale, then a failed sale that changes nothing |
| `blocking-session-*.txt` | The blocking demo |
| `deadlock-*.txt` | The deadlock demo, in both access orders |

## Blocking: what was observed

Session 1 updates `DM01` / `DEMO-SKU-001` from 40 to 39 inside an open transaction, waits 20 seconds and rolls back. Session 2 reads the same row at **READ COMMITTED**, with `READ_COMMITTED_SNAPSHOT` off, which is also the `ApexPostDb` setting.

| Observation | Evidence |
|---|---|
| The writer holds an exclusive key lock | `KEY X GRANT` |
| The reader waits for a shared lock on that key | `KEY S WAIT`, wait type `LCK_M_S`, with the writer as `BlockingSession` |
| The reader's isolation level | `READ COMMITTED` |
| The reader waited until the rollback | About 18 seconds (`ReaderWaitedMilliseconds`) |
| The reader returned only committed data | `CommittedQuantity` 40 |
| `READ UNCOMMITTED` is not blocked, but reads a value that never commits | `UncommittedQuantity` 39 |

**Why:** at READ COMMITTED with locking, a reader needs a shared lock, which is incompatible with the writer's exclusive lock. The wait lasts as long as the writer's transaction, which is why ApexPost transactions stay short and never wait for a user.

The two sessions ran through `sqlcmd`. The same files run in two SSMS windows, as the comments in them explain, but no SSMS screenshots were taken.

## Deadlock: what was observed

Both sessions update the same two stock rows, with a 3-second gap to force the overlap.

| Run | Session results | Stock from 40 each |
|---|---|---|
| **Opposite order** | One committed, the other got **error 1205** as the deadlock victim and rolled back | 39 and 39: only the survivor's change remains |
| **Same order** | Both committed, the second only waited | 38 and 38: both changes applied |

The design that avoids this is in `DESIGN.md` section 4.7.
