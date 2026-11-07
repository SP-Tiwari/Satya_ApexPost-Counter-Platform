# Week 4 Part A evidence

Saved output from real runs on 2026-11-02, using Couchbase Lite 4.1.2 on .NET 10 (Windows x64). All data is fictional. Every result below comes from those runs, but some files were tidied after capture:

- **Harness sessions and `single-owner.txt`:** the console output is unchanged apart from two things. LiteCore's one-line startup banner was filtered out, and a header comment was added at the top. The header in `single-owner.txt` was later reworded to describe the setup more clearly.
- **`query-plans-and-tests.txt`:** condensed. Blank lines, xUnit runner status lines and per-test durations were removed. The test names, outcomes and query plans are as printed.
- **`test-results.txt`:** a summary written from the real `dotnet test` output, not a raw log. The pass and fail lines are copied from that output.

The harness runs had their input piped from a script, so the transcript shows each answer after its prompt. The listener runs on a Couchbase Lite background thread, so its log lines sometimes appear in the middle of a prompt, for example `Tracking number: [info] CourierDatabase: Changed documents: …`. That interleaving is real, not a formatting fault.

| File | Shows |
|---|---|
| [harness-session-1.txt](harness-session-1.txt) | Every menu option against a new database: five scans added, query 3 after each add, query 1 (including an injection-style value that returns nothing), query 2, a simulated sync with one rejected scan, an invalid ID and an invalid option. Every change is logged by the listener with the document ID and the live pending count, from 1 up to 5 and back down to 0 |
| [harness-session-2.txt](harness-session-2.txt) | Restart against the same files: the rejected scan is still stored as `Failed`. It is then deleted by ID, and a second delete reports that it is gone |
| [single-owner.txt](single-owner.txt) | A second harness process is refused while another holds the database open |
| [query-plans-and-tests.txt](query-plans-and-tests.txt) | The query tests, with `IQuery.Explain()` output showing `USING INDEX idx_scans_trackingNumber` and `USING INDEX idx_scans_syncStatus` |
| [test-results.txt](test-results.txt) | The build and every test project, including the Week 3 suites |

## Where to find each requirement in the evidence

| Requirement | Evidence |
|---|---|
| Database `apexpost_courier`, opened and closed | Session 1: `Opened apexpost_courier at …\apexpost_courier.cblite2\`, then `Change listener removed.` and `Closed apexpost_courier.` at exit |
| `scan::{Guid}` IDs, saved locally as `Pending` | Session 1: `Saved scan::… locally as Pending.` |
| Query 1, by tracking number, ordered by `scannedAtUtc` | Session 1: `Scans for APX-DEMO-1001, oldest first: 3`, listed Pickup, InTransit, Delivered |
| Query 2, `syncStatus = 'Pending'` | Session 1: `Pending scans, oldest first: 5`, and `0` after the sync |
| Query 3, scans per scan type for one courier | Session 1: `Scans per type for courier COURIER-DEMO-07: Delivered 1, InTransit 1, Pickup 2` |
| Parameters only | Session 1: the value `' OR 1=1 --` returns 0 scans; `Parameters_TreatQuerySyntaxAsAPlainValue` in the query tests |
| Value indexes | `query-plans-and-tests.txt` |
| Outbox marks `Synced` and `Failed` | Session 1: `Sync finished: 5 attempted, 4 synced, 1 failed.`; session 2 shows the rejected scan still stored as `Failed` |
| Listener logs changed IDs and a live pending count | Session 1: the `[info] CourierDatabase: Changed documents: …. Pending scans: N.` lines, and the `pending scans (live): N` menu header |
| Listener removed on dispose | Both sessions end with `Change listener removed.` before `Closed apexpost_courier.` |
| No network needed | No replicator, endpoint or HTTP client exists in the courier projects, and the fake uploader makes no call. The runs were not repeated with the network physically disconnected |

## Screenshots

Real captures of the harness's console window on 2026-11-02, taken with one fictional database (`out/courier-shots`). Each run had its input piped from a script. The Windows taskbar covers the last few lines of each window.

| File | Shows |
|---|---|
| [01-query-by-tracking-number.png](screenshots/01-query-by-tracking-number.png) | Query 1: the three `APX-DEMO-1001` scans in `scannedAtUtc` order |
| [02-query-pending.png](screenshots/02-query-pending.png) | Query 2: the four `Pending` scans |
| [03-query-scans-per-type.png](screenshots/03-query-scans-per-type.png) | Add Scan, the listener line for the new ID with pending scans 5, and query 3: `Delivered 1, InTransit 1, Pickup 3` |
| [04-listener-log-during-sync.png](screenshots/04-listener-log-during-sync.png) | Simulate Sync with `APX-DEMO-2002` rejected, and the listener's `Changed documents: …. Pending scans: N.` lines |

## When the listener reports

Couchbase Lite delivers change notifications asynchronously on a background thread, and can deliver one after several more writes have happened. The listener counts pending scans when the notification arrives, so each count is correct for that moment, not for the moment of the change it reports.

Screenshot 04 shows this. The first notification during the sync reports 3, because two more scans had already been synced by the time it arrived. The menu header shows the count from the most recent notification, so with scripted input it briefly showed 1 while the final notifications were still arriving. They then arrived after `Goodbye.`, reporting 0, before the listener was removed. A person typing at the menu gives the notifications time to arrive before the next redraw.
