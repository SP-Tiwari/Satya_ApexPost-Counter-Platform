# Week 3 test results

Build, test and Postman results for the Week 3 solution, recorded from the actual output. All data is fictional.

## Latest full run

| Item | Value |
|---|---|
| Build started | 2026-11-01 15:30:35 UTC |
| Tests started | 2026-11-01 15:30:45 UTC |
| Tests finished | 2026-11-01 15:32:03 UTC |
| Code | Branch `feature/week3-score-improvements` at `1f0863a`, plus the uncommitted Milestones 1–5 changes. No file in `src` or `tests` changed after this build |
| .NET SDK | 10.0.402 |
| Target framework | `net10.0` |
| Operating system | Windows 10.0.22000.2538 |
| SQL Server | 17.0.1000.7, Enterprise Evaluation Edition (64-bit), default local instance, Windows authentication |
| `APEXPOST_TEST_SQL_SERVER`, `APEXPOST_TEST_DATA_DIRECTORY`, `APEXPOST_TEST_OTHER_MACHINE` | Not set, so the defaults applied |

```powershell
dotnet build ApexPost.slnx --no-incremental
dotnet test ApexPost.slnx --no-build
```

**Build succeeded, with 0 warnings and 0 errors.** `TreatWarningsAsErrors` is on.

| Project | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Domain | 224 | 0 | 0 | 224 |
| Application | 140 | 0 | 0 | 140 |
| Infrastructure | 240 | 0 | 0 | 240 |
| Web integration | 300 | 0 | 0 | 300 |
| **Total** | **904** | **0** | **0** | **904** |

### Change from the earlier full run

42 tests were added and 30 were removed, a net rise of 12 from 892.

| Project | Earlier run | Latest run | Change | Reason |
|---:|---:|---:|---:|---|
| Domain | 224 | 224 | 0 | |
| Application | 120 | 140 | +20 | Refund and command mapping tests |
| Infrastructure | 261 | 240 | −21 | Refund and seed tests added; 30 removed with the legacy JSON stores |
| Web integration | 287 | 300 | +13 | Over-posting, route tampering and demo report tests |

### Database safety

- SQL tests create their own uniquely named `ApexPost_Test_*` databases and drop them when the run ends.
- Connections are built only for `master` or an `ApexPost_Test_*` database on a local instance, always with Windows authentication. `SqlTestSettingsTests` shows that `ApexPostDb` is refused.
- Before migrating, the fixture reads `sys.master_files` from `master` and stops unless the database exists with exactly its two requested files. This stops EF Core creating a missing database in the instance's default location.
- During the run, a read-only query on `master` sampled the server 15 times:

| Check | Result |
|---|---|
| Database files seen during the run | 38 file rows, all `ApexPost_Test_*`, all in `E:\SqlData\ApexPost\Tests` |
| Sessions in `ApexPostDb` | 0 in all 15 samples |
| Databases after the run | Only `ApexPostDb` |
| `ApexPostDb` afterwards | 0 sessions, 0 index usage rows, 49,152 file pages, read from `master` only |
| Test files left behind | 0 in the test folder, 0 in the SQL Server data folder on C: |

## Latest Postman CLI run

Run on 2026-11-01 at 15:42 UTC with Postman CLI 1.75.0, after the full run and on the same code.

```powershell
./docs/evidence/m7/run-postman.ps1
```

**65 of 65 requests ran with 0 failed, 188 of 188 assertions passed, and no request returned a 5xx status.** The run used a disposable `ApexPost_Postman_*` database in `E:\SqlData\ApexPost\Tests`, which it dropped afterwards. During the run, 10 read-only samples on `master` found 0 sessions in `ApexPostDb`.

| Behaviour | Verified by | Result |
|---|---|---|
| A posted transaction ID cannot redirect a void | `Posted transaction ID cannot redirect a void`, then `Sale named only in the form stays Paid` | 409 on the addressed sale, and the sale named in the form stayed Paid. The audit row names the addressed sale |
| A refund equals the stored payment | `Refund a seeded shipment sale`, and the runner's database query | Paid 17.50, refunded 17.50. The voided sale with a rounded total: paid 26.46, refunded 26.46 |
| Command-backed endpoints | Folder `8 Command endpoints and seeded reports`, `Adjust stock through its command`, and the existing sale, void and error requests | All 302 or the expected error, with audit rows |
| Shipping volume report | `Shipping volume shows the seeded shipments` | All three service levels shown |
| Branch auditor report | `Branch auditor shows the seeded money orders` | All five branches shown |
| Masked identity values | `Money orders show masked documents only`, and the runner's database query | Masked values shown, no unmasked number on the page, 15 of 15 stored rows masked |

The collection, runner, guide and saved output are in [docs/evidence/m7](m7/README.md).

## Limitations

- **One machine.** These runs used the default settings on the development machine. The SQL test overrides for another machine are covered by tests that use no database.
- **Environment reading.** Tests pass a stand-in for the environment, so that they cannot affect SQL tests running at the same time. The direct call to `Environment.GetEnvironmentVariable` is confirmed by inspection and by the runs using the defaults.
- **Windows and a local instance.** Remote servers are refused. The test process creates the data folder itself, and the SQL Server service account needs access to it. Windows authentication is required.
- **Machine identity.** Code cannot identify the development machine. If `E:\SqlData\ApexPost` is missing there, a different folder is still refused unless someone also sets `APEXPOST_TEST_OTHER_MACHINE` to `true`.
- **Postman.** Only the CLI was used. Nobody opened the collection in the Postman desktop app.
- **Sampling.** The samples were taken every few seconds, so they show what was present at those moments.
- **Open items.** The trainer decisions and known gaps are in [DESIGN.md](../../DESIGN.md#14-known-gaps). None has been approved by these runs.

## Earlier full run (historical)

This run came before the Week 3 score-improvement changes. It is kept as a record and is not the latest result.

| Item | Value |
|---|---|
| Tests started | 2026-11-01 13:28:07 UTC |
| Tests finished | 2026-11-01 13:29:26 UTC |
| Build | 0 warnings, 0 errors |
| Result | 892 passed, 0 failed, 0 skipped: Domain 224, Application 120, Infrastructure 261, Web integration 287 |
| Baseline before it | 787 passed, 0 failed, 0 skipped |
