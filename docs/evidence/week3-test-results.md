# Week 3 test results

A full build and test run of the Week 3 solution, recorded from the actual output. All data is fictional.

| Item | Value |
|---|---|
| Build started | 2026-11-01 13:27:46 UTC |
| Tests started | 2026-11-01 13:28:07 UTC |
| Tests finished | 2026-11-01 13:29:26 UTC |
| Code | Commit `538c1fb` plus uncommitted test-infrastructure changes. `src` matched `538c1fb`. Documentation was updated after the run |
| .NET SDK | 10.0.402 |
| Target framework | `net10.0` |
| Operating system | Windows 10.0.22000.2538 |
| SQL Server | 17.0.1000.7, Enterprise Evaluation Edition (64-bit), default local instance, Windows authentication |
| `APEXPOST_TEST_SQL_SERVER`, `APEXPOST_TEST_DATA_DIRECTORY`, `APEXPOST_TEST_OTHER_MACHINE` | Not set, so the defaults applied |

## Build

```powershell
dotnet build ApexPost.slnx --no-incremental
```

**Build succeeded, with 0 warnings and 0 errors.** `TreatWarningsAsErrors` is on.

## Tests

```powershell
dotnet test ApexPost.slnx --no-build
```

| Project | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Domain | 224 | 0 | 0 | 224 |
| Application | 120 | 0 | 0 | 120 |
| Infrastructure | 261 | 0 | 0 | 261 |
| Web integration | 287 | 0 | 0 | 287 |
| **Total** | **892** | **0** | **0** | **892** |

## Comparison with the previous baseline

The previous baseline was 787 passed, 0 failed and 0 skipped. This run adds 105 tests and removes none.

| Project | Baseline | This run | Added | New tests |
|---|---:|---:|---:|---|
| Domain | 224 | 224 | 0 | |
| Application | 120 | 120 | 0 | |
| Infrastructure | 159 | 261 | 102 | `SqlTestSettingsTests` (97, no database), `DisposableSqlDatabaseTests` (5) |
| Web integration | 284 | 287 | 3 | `ApplicationExceptionHandlerTests` |
| **Total** | **787** | **892** | **105** | |

## Database safety

- SQL tests create their own uniquely named `ApexPost_Test_*` databases and drop them when the run ends.
- Connections are built only for `master` or an `ApexPost_Test_*` database on a local instance, always with Windows authentication. `SqlTestSettingsTests` shows that `ApexPostDb` is refused.
- Before migrating, the fixture reads `sys.master_files` from `master` and stops unless the database exists with exactly its two requested files. This stops EF Core creating a missing database in the instance's default location.
- `DisposableSqlDatabaseTests` ran the real statements in this run. A path too long for `QUOTENAME` raised an error and created nothing, and a database that does not exist stopped before migrating.
- On the development machine, the only accepted data folder is `E:\SqlData\ApexPost\Tests`.
- During the run, a read-only query on `master` sampled the server 15 times:

| Check | Result |
|---|---|
| Database files seen during the run | 36 file rows, all `ApexPost_Test_*`, all in `E:\SqlData\ApexPost\Tests` |
| Sessions in `ApexPostDb` | 0 in all 15 samples |
| Databases after the run | Only `ApexPostDb` |
| `ApexPostDb` afterwards | 0 sessions, 0 index usage rows, 49,152 file pages, read from `master` only |
| Test files left behind | 0 in the test folder, 0 in the SQL Server data folder on C: |

## Limitations

- **One machine.** This run used the default settings on the development machine. The overrides for another machine are covered by tests that use no database. A full run on another machine or instance has not been done.
- **Environment reading.** Tests pass a stand-in for the environment, so that they cannot affect SQL tests running at the same time. The direct call to `Environment.GetEnvironmentVariable` is confirmed by inspection and by this run using the defaults.
- **Windows and a local instance.** Remote servers are refused. The test process creates the data folder itself, and the SQL Server service account needs access to it. Windows authentication is required.
- **Machine identity.** Code cannot identify the development machine. If `E:\SqlData\ApexPost` is missing there, a different folder is still refused unless someone also sets `APEXPOST_TEST_OTHER_MACHINE` to `true`.
- **Postman is not part of this run.** The collection was not run.
- **Sampling.** The samples were taken about every 5 seconds, so they show what was present at those moments.
- **Open items.** The trainer decisions and known gaps are in [DESIGN.md](../../DESIGN.md#14-known-gaps). None has been approved by this run.
