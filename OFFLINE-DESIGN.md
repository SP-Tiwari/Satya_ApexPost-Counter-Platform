# Courier offline design (Week 4 Part A)

Couriers store shipment scans in an embedded Couchbase Lite database, `apexpost_courier`, using its default collection (`src/ApexPost.Courier.Offline`; decisions in [ADR 0027](docs/adr/0027-couchbase-lite-courier-offline-store.md)). **Implemented** means built and verified in Part A. **Design** means a plan for later parts.

## 1. Why scans are saved locally first

Couriers often have no signal at a pickup or delivery. If saving needed the network, the scan would be lost, or entered later with the wrong time.

- **Implemented:**
  - *Save without network:* `ShipmentScan.Record` creates each scan as `Pending`, with its own `scan::{Guid}` ID and a UTC time. `CouchbaseScanRepository.Create` refuses anything not `Pending`. The save is a local file write, so no network is needed.
  - *Status as outbox:* the `syncStatus` field is the outbox; there is no separate queue. `OutboxService` uploads the `Pending` scans oldest first through `IShipmentUploader`, and marks each one `Synced` or `Failed`. An uploader exception counts as `Failed`; cancelling leaves the rest `Pending`.
  - *No lost scans:* a scan is never lost to a network problem. It stays on the device as `Pending` or `Failed`.
- **Design (Part D):**
  - *Repeated uploads:* uploading and marking `Synced` are two steps, so a crash between them resends the scan. The API must accept a repeated scan ID once.
  - *Retries:* `Failed` scans are not retried yet; the retry rule belongs to Part D.

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
