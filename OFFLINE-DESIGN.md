# Courier offline design (Week 4 Part A, synchronization in Part D)

Couriers store shipment scans in an embedded Couchbase Lite database, `apexpost_courier`, using its default collection (`src/ApexPost.Courier.Offline`, with the domain model in `src/ApexPost.Courier.Domain`; decisions in [ADR 0027](docs/adr/0027-couchbase-lite-courier-offline-store.md) and [ADR 0031](docs/adr/0031-courier-domain-project.md)). **Implemented** means built and verified, in Part A unless marked Part D. **Design** means a plan or reasoning that is not implemented. Section 4 describes the synchronization with the API added in Part D (D9).

## 1. Why scans are saved locally first

Couriers often have no signal at a pickup or delivery. If saving needed the network, the scan would be lost, or entered later with the wrong time.

- **Implemented:**
  - *Save without network:* `ShipmentScan.Record` creates each scan as `Pending`, with its own `scan::{Guid}` ID and a UTC time. `CouchbaseScanRepository.Create` refuses anything not `Pending`. The save is a local file write, so no network is needed.
  - *Status as outbox:* the `syncStatus` field is the outbox; there is no separate queue. `OutboxService` uploads the `Pending` scans oldest first through `IShipmentUploader`. Each scan becomes `Synced` or `Failed` only on the server's final answer. Since Part D the uploads go in batches of 20, and a failure without an answer leaves scans `Pending` ([section 4](#4-synchronization-with-the-api-part-d-d9)). In Part A an uploader exception counted as `Failed`. Cancelling leaves the rest `Pending`.
  - *No lost scans:* a scan is never lost to a network problem. It stays on the device as `Pending` or `Failed`.
  - *Repeated uploads (Part D):* uploading and marking `Synced` are two steps, so a crash between them resends the scan. The API accepts a repeated scan ID once and reports it as a duplicate, which the device counts as `Synced`. `CourierOfflineSyncTests` verifies this against the API.
  - *`Failed` is final:* a `Failed` scan was refused by the server for a reason that sending it again would not change, such as an unknown shipment or a scan ID already used for another shipment. It is not sent again and stays visible on the device. Anything that might succeed later stays `Pending` instead.

## 2. How conflicts would be handled

A conflict is two copies of one record changing independently. Part A has one writer. After a scan is saved, only the outbox changes it, and only its `syncStatus`. **Implemented:** locally, `Update` and `Delete` use `FailOnConflict`, so they refuse to overwrite a document that changed underneath them.

| | Last-write-wins | Server decides |
|---|---|---|
| Rule | The newest timestamp replaces the other version | The device proposes; the server applies the business rules and returns the final state |
| Strength | Simple, with no extra round trip | One source of truth; a rejection is reported back, not silently lost |
| Weakness | Clocks drift. An offline device's older `InTransit` can quietly overwrite a newer `Delivered` | A scan counts as `Synced` only after the server answers |
| Fits | Fields where the newest edit is best, such as notes | Shipment status |

**Design: the server decides.** Each scan is a separate event with its own ID, not an edit of a shared record. The server can accept each event once, by ID, and work out a shipment's status from their order. A refused scan becomes `Failed` on the device and stays visible to the courier. `ShipmentUploadResult` and the `Synced`/`Failed` statuses already carry this answer.

## 3. Why an embedded database must not be shared by several processes

Couchbase Lite is a library inside the app's process, not a server. Its open database object, listeners and query state belong to that process.

- **Design reasoning, not verified:** a second process writing the same files is not coordinated with this one. Its changes would not reach this process's listener or live pending count, and a crash in either could leave the other with stale or damaged data.
- **Design reasoning, from this code:** a second process would run its own outbox over the same `Pending` scans. It would upload them twice and race to set their status.
- **Implemented:**
  - *The lock:* `CourierDatabase` holds a lock file (`apexpost_courier.lock`) while it is open.
  - *Second process refused:* [single-owner.txt](docs/evidence/week4-part-a/single-owner.txt) records a second harness process being refused.
  - *Second instance refused:* `Open_RefusesASecondOwnerOfTheSameDatabase` checks that a second instance in the same process is refused too.
  - *The listener:* one at most; its token is removed before the database closes on dispose.
- **Design:** other software should go through the owning app, or the central API in later parts, never open the files directly.

## 4. Synchronization with the API (Part D, D9)

**Implemented** in Week 4 Part D. The courier client is a .NET console application instead of a .NET MAUI app ([ADR 0030](docs/adr/0030-courier-console-client.md)); the substitutions are named below.

### Flow

1. **Trigger:**
   - `SyncCoordinator` (`src/ApexPost.Courier.Sync`) listens to `INetworkStatus`. Its implementation, `SystemNetworkStatus`, uses `NetworkChange.NetworkAvailabilityChanged` and `NetworkInterface.GetIsNetworkAvailable()`, the console substitute for MAUI `Connectivity.ConnectivityChanged`.
   - When a network becomes available and stays available for 3 seconds (`ReconnectDelay`), it runs the outbox. A change during the wait restarts it, and losing the network cancels it.
   - `Start()` syncs once at application start when the network is available.
   - After a new scan is saved, the console calls `SyncIfOnline()`.
   - The console's **Sync Now** calls `SyncNowAsync()`. It is also how a courier retries after the circuit breaker has opened while the network stayed up.
   - **No run while sign-in is required:** after a 401 the token is gone. Every trigger then starts nothing, instead of sending requests that would only get another 401, until the courier logs in again. The scans stay `Pending`.
2. **Single flight:** at most one run at a time. A request during a run, from the network event or on demand, joins that run.
3. **Batches:** `OutboxService.SyncPendingAsync` reads the `Pending` scans oldest first and passes them to the uploader in batches of at most 20 (`OutboxService.BatchSize`). The Part A change listener sees every status change, so the live pending count stays current.
4. **Upload:**
   - `ApiShipmentUploader` replaces Part A's `FakeShipmentUploader`. The fake is still used by the Part A harness.
   - It groups each batch by tracking number, in the order each first appears, because `POST /api/v2/shipments/{trackingNumber}/scans` takes one shipment per request.
   - It sends one group at a time through `ApexApiClient`, with the D7 retry, circuit breaker and timeouts.
5. **Result per scan:** the uploader returns one `ShipmentUploadResult` per scan, `Synced`, `Rejected` or `RetryLater`, and the outbox applies it. A scan without a result counts as `RetryLater`.
6. **Stop on a temporary failure:**
   - After a `RetryLater`, the uploader sends no further group, and the outbox sends no further batch.
   - The rest of the run stays `Pending` for the next run. Answers already received are kept.
7. **State:** `SyncState` records the end of each run (time, counts, failure reasons) and the circuit breaker's state, for the Sync Status screen (D11).

### Status rules

| API or client outcome | Upload result | Local status |
|---|---|---|
| Accepted by the server | `Synced` | `Synced` |
| A duplicate: the scan ID is already stored for this shipment | `Synced` | `Synced` |
| Listed in the response's `rejections` (for example, a scan ID already stored for another shipment) | `Rejected`, with the server's reason | `Failed` |
| 400, 403, 404 or another final 4xx for the request | `Rejected`, with the server's message | `Failed` (every scan of that request) |
| 401 | `RetryLater`; the auth handler has already cleared the token (D10) | `Pending` |
| 408 or 429 | `RetryLater` | `Pending` |
| 5xx, still failing after the retries | `RetryLater` | `Pending` |
| No network, a timeout, or the circuit open | `RetryLater` | `Pending` |
| An unreadable or inconsistent answer (counts that do not add up, a rejection naming no scan of the request) | `RetryLater` | `Pending` |
| Not sent, because an earlier group or batch of the run got no final answer | `RetryLater` | `Pending` |

- **429:** "Too many requests" is not final, so it is treated like 408, although it is a 4xx.
- **While the circuit is open:** scans are still saved locally. Saving never needs the network. Each sync run then fails at once and leaves them `Pending`.
- **An unexpected failure** while one shipment is being sent leaves that shipment's scans, and those not yet sent, `Pending`. Answers already received for earlier shipments in the batch are kept.
- **Known limitation: a device clock ahead of the server's.**
  - The API refuses a scan time later than its own current time with a 400 for the whole request (`NotInFuture`, Part B). Under the approved rule "a final 4xx → `Failed`", every scan of that shipment in the batch then becomes `Failed`, and `Failed` scans are not resent.
  - The server would accept the same scans once its clock had passed their times, so a device whose clock runs ahead can lose valid scans to `Failed`. They are kept and visible on the device, but not uploaded.
  - The rule is unchanged; treating that particular 400 as temporary would be a business-rule change for the project owner to decide. Couriers' devices should keep their clocks synchronised automatically.

### Verification

| Behaviour | Tests |
|---|---|
| Batches of 20, 20 and 5, oldest first; a result per scan; a temporary failure stops the run; a missing result stays `Pending`; cancellation | `OutboxServiceTests` (`tests/ApexPost.Courier.Offline.Tests`) |
| Grouping by tracking number, field mapping, every row of the status table, earlier answers kept when a later shipment gets no answer | `ApiShipmentUploaderTests` (`tests/ApexPost.Courier.Sync.Tests`) |
| 45 scans over two shipments, 401 through the outbox, saving while the circuit is open, circuit state in `SyncState` | `OfflineSyncTests` |
| Sync at start and after a new scan when online, debounce, single flight, sync on reconnect, no run while sign-in is required, shutdown | `SyncCoordinatorTests` |
| End to end: Couchbase Lite store, `SyncCoordinator` from `AddCourierSync`, the real API. Accepted, duplicate, rejected and unknown-shipment scans; a resent accepted scan stores nothing new | `CourierOfflineSyncTests` (`tests/ApexPost.Api.IntegrationTests`) |

**Changed Part A rule:** in Part A an uploader exception marked the scan `Failed`. That would wrongly retire a scan the server never saw, so the exception now leaves it `Pending` and stops the run. The test is now `SyncPending_WhenTheUploaderThrows_LeavesTheScansPendingAndStops`.

**In the console client (D11):**
- `src/ApexPost.Courier.Console/Program.cs` registers the device's Couchbase Lite store as `IScanRepository`, starts the Part A change listener and calls `SyncCoordinator.Start()`.
- `CourierConsole` shows the live pending count, the Sync Status (`SyncState.Snapshot()`) and the offline banner, and offers Sync Now.
- Tests: `CourierConsoleTests` (`tests/ApexPost.Courier.Console.Tests`).
