# ApexPost API notes (Week 4 Part B)

`src/ApexPost.Api` exposes tracked shipments and their courier scans as a REST API over the Week 3 SQL Server database. It reuses `ApexPostDbContext`, the repositories and the unit of work through `AddApexPostSqlPersistence`. Storage decisions are in [ADR 0028](docs/adr/0028-tracked-shipments-storage.md). All data is fictional.

## Endpoints

`{trackingNumber}` has the route constraint `regex(^APX-\d{8}$)`. A value that does not match finds no route, so it is a 404 like any other unknown tracking number.

| Verb | Route | Action | Purpose | Status codes |
|---|---|---|---|---|
| GET | `/api/shipments?page=&pageSize=&status=&city=` | `ShipmentsController.List` | Paged, filtered list, ordered by tracking number. `page` defaults to 1, `pageSize` to 20, and its maximum is 50 | 200, 400 |
| GET | `/api/shipments/{trackingNumber}` | `ShipmentsController.Get` | One shipment | 200, 404 |
| POST | `/api/shipments` | `ShipmentsController.Create` | Creates a shipment with status `Created`; the `Location` header comes from `CreatedAtAction` | 201, 400, 409, 415 |
| PUT | `/api/shipments/{trackingNumber}` | `ShipmentsController.Replace` | Replaces the editable details: recipient name, phone, address and city, and the weight | 200, 400, 404, 409, 415 |
| PATCH | `/api/shipments/{trackingNumber}/status` | `ShipmentsController.ChangeStatus` | Changes the status only | 200, 400, 404, 409, 415 |
| DELETE | `/api/shipments/{trackingNumber}` | `ShipmentsController.Delete` | Deletes a shipment, with its scans, only while its status is `Created` | 204, 404, 409 |
| POST | `/api/shipments/{trackingNumber}/scans` | `ShipmentScansController.Upload` | Bulk upload of offline scans from Part A, idempotent by scan ID | 200, 400, 404, 415 |
| GET | `/api/shipments/{trackingNumber}/scans` | `ShipmentScansController.History` | The shipment's scans, oldest first | 200, 404 |

Any route can also return 500 through the global exception handler, and a 409 when a change conflicts with another one made at the same time.

## Status codes

| Code | When | Implementation |
|---|---|---|
| 200 OK | A read, a replace, a status change or a scan upload succeeded | `Ok(...)` |
| 201 Created | A shipment was created. `Location` is the URL of `GET /api/shipments/{trackingNumber}` | `CreatedAtAction(nameof(Get), ...)` |
| 204 No Content | A shipment was deleted | `NoContent()` |
| 400 Bad Request | Validation failed, including malformed JSON | `[ApiController]` automatic validation: `ValidationProblemDetails` |
| 404 Not Found | No shipment has the tracking number, or the route does not match | `ShipmentProblems.ShipmentNotFound`; `UseStatusCodePages` for unmatched routes |
| 409 Conflict | A duplicate tracking number, a refused status transition, deleting a shipment that is not `Created`, or a concurrent change that outlasted the retries | `ShipmentProblems.ShipmentProblem`; `ApiExceptionHandler` for `DuplicateRecordException` and `ConcurrencyConflictException` |
| 415 Unsupported Media Type | A body that is not `application/json` | `[Consumes("application/json")]` |
| 500 Internal Server Error | Any other unhandled error | `ApiExceptionHandler` |

Every error is Problem Details (`application/problem+json`). `ApiExceptionHandler` (`src/ApexPost.Api/Errors/ApiExceptionHandler.cs`) is the `IExceptionHandler`. It adds the exception text and stack trace only in Development; elsewhere a 500 has only its status and a fixed title.

## Status transitions

`Created → PickedUp → InTransit → OutForDelivery → Delivered` or `Failed`. Each status moves only to the next one, and `Delivered` and `Failed` are final. A skipped, repeated or backward change is a 409. The rule is `ShipmentStatusTransitionRule` (`src/ApexPost.Domain/Rules/`). Uploaded scans never change the status; only the PATCH does.

## Request DTOs and validation

The request DTOs are in `src/ApexPost.Api/Models/`. No request binds to an EF entity. Maximum lengths match the database columns, so an over-long value is a 400, not a database error.

| DTO | Field | Rules |
|---|---|---|
| `CreateShipmentRequest` | `trackingNumber` | Required; `^APX-[0-9]{8}$`. This is the brief's `^APX-\d{8}$`, written with `[0-9]` because .NET's `\d` also matches non-ASCII digits |
| `CreateShipmentRequest`, `UpdateShipmentRequest` | `recipientName` | Required; at most 100 |
| | `recipientPhone` | Required; at most 20; E.164 (`^\+[1-9][0-9]{7,14}$`), such as `+919876543210` |
| | `recipientAddress` | Required; at most 200 |
| | `recipientCity` | Required; at most 100 |
| | `weightKg` | Required; 0.1 to 70 |
| `UpdateStatusRequest` | `status` | Required; a defined `ShipmentStatus` |
| `ScanUploadRequest` | `scans` | Required; 1 to 100 scans |
| Each scan (`ScanUploadItem`) | `scanId`, `trackingNumber` | Required. A malformed ID or a different tracking number rejects that scan, not the request |
| | `scanType` | Required; a defined `ShipmentScanType`: `Pickup`, `InTransit`, `Delivered` or `Failed` |
| | `courierId` | Required; at most 50 |
| | `location` | Required; at most 200 |
| | `scannedAtUtc` | Required; **custom rule `[NotInFuture]`** |
| | `notes` | Optional; at most 500 |
| `ShipmentListQuery` | `page`, `pageSize`, `status`, `city` | `page` at least 1; `pageSize` 1 to 50; `status` a defined status; `city` at most 100 |

**Custom rule:** `NotInFutureAttribute` (`src/ApexPost.Api/Validation/`) is a custom validation attribute. A scan time later than the server's current UTC time, by any amount, is a 400. A time equal to it is accepted.

Enums are serialized as numbers in Part B, the default for `System.Text.Json`. Returning `status` as a string is one of the breaking changes that Part C introduces in v2.

## Idempotent scan upload

Each scan carries its Part A ID, `scan::{Guid}`, which is the primary key of `TrackedShipmentScans`. `TrackedShipmentApplicationService.UploadScansAsync` handles each scan in order:

| Scan | Counted as | Reason returned |
|---|---|---|
| Malformed scan ID | `rejected` | "The scan ID must have the format scan::{Guid}." |
| Tracking number differs from the route | `rejected` | "The scan is for …, not …." |
| ID already stored under another shipment | `rejected` | "The scan ID is already recorded for another shipment." |
| ID already recorded for this shipment, or repeated in the request | `duplicatesSkipped` | |
| Anything else | `accepted` | |

A scan is accepted even when the shipment is already `Delivered` or `Failed`, because a device can upload a scan recorded offline after the status changed. The response is `{ accepted, duplicatesSkipped, rejected, rejections: [{ scanId, reason }] }`. If another upload stores the same scan at the same moment, the database refuses the second copy, and the upload runs once more so that scan is counted as a duplicate.

## Verification

**Automated:** `tests/ApexPost.Api.IntegrationTests` runs every endpoint against a disposable `ApexPost_Test_*` SQL Server database built by the real migrations. User Secrets are removed, so a developer's database is never used. Every error assertion checks `application/problem+json`, the `status` and a `title`.

The full run on 2026-11-07 at 11:14 UTC passed 1,124 of 1,124 tests, 98 of them API integration tests, with 0 warnings ([test-results.txt](docs/evidence/week4-part-b/test-results.txt)).

| Requirement | Verified by |
|---|---|
| 201 and a `Location` that returns the shipment | `ShipmentEndpointsTests.Create_Returns201_WithALocationThatReturnsTheShipment` |
| 204 | `Delete_CreatedShipment_Returns204_AndRemovesItWithItsScans` |
| 400 for each validation rule and maximum length | `Create_InvalidField_Returns400ValidationProblem_AndStoresNothing` (18 cases), `Upload_InvalidScanField_Returns400` (10 cases), `List_InvalidQuery_Returns400ValidationProblem` |
| Weight limits 0.1 and 70 kg | `Create_WeightAtEitherLimit_IsAccepted`; 0.09 and 70.001 in the 400 cases |
| 404 | `Get_UnknownTrackingNumber_Returns404ProblemDetails`, `Get_ValueOutsideTheRouteConstraint_Returns404ProblemDetails`, and one for each other endpoint that takes a tracking number |
| 409: duplicate, transition, delete after `Created`, concurrency | `Create_DuplicateTrackingNumber_Returns409_AndKeepsTheFirst`, `ChangeStatus_RefusedTransition_Returns409_AndKeepsTheStatus` (7 cases), `Delete_AfterCreated_Returns409_AndKeepsTheShipment`, `ErrorHandlingTests.ConcurrencyConflict_Returns409ProblemDetails` |
| 415 | `Create_WrongContentType_Returns415ProblemDetails`, and the same for PUT, PATCH and the scan upload |
| 500 without a stack trace outside Development | `UnhandledError_OutsideDevelopment_Returns500ProblemDetails_WithoutTheStackTrace` (Production, Staging) |
| Status lifecycle | `ChangeStatus_EachStepOfTheLifecycle_Returns200WithTheNewStatus`; all 36 pairs in `ShipmentStatusTransitionRuleTests` (Domain) |
| Paging and filters | `ShipmentListTests` |
| Idempotent upload and its three counts | `Upload_TheSameScansAgain_SkipsThemAsDuplicates_AndStoresOneCopy`, `Upload_AnIdRepeatedInTheRequestOrInAnotherCase_IsADuplicate` |
| Per-scan rejections | `Upload_MalformedScanId_IsRejectedWithAReason_AndTheRestAccepted` (5 cases), `Upload_ScanForAnotherTrackingNumber_IsRejected`, `Upload_ScanIdStoredUnderAnotherShipment_IsRejected` |
| Scans never change the status; historical scans accepted | `Upload_NeverChangesTheShipmentStatus`, `Upload_HistoricalScanAfterAFinalStatus_IsAccepted` (`Delivered`, `Failed`) |
| Custom rule `[NotInFuture]`, strict | With the server clock fixed: `Upload_ScanAtExactlyTheServerTime_IsAccepted`; `Upload_ScanAfterTheServerTime_Returns400_AndStoresNothing` (1 tick, 1 ms, 5 minutes); `Upload_FutureTimeInAnotherOffset_IsComparedInUtc` |

**Live HTTP:** [ApexPost.Api.http](src/ApexPost.Api/ApexPost.Api.http) has a success and at least one failure request for each endpoint. On 2026-11-07 all 24 requests were sent, in file order, to the API running on its `https` profile against an isolated `ApexPost_Test_*` database built by the migrations. Every response had its expected status. The requests were sent by a script that reads the `.http` file, not from the Visual Studio editor.

| Evidence | File |
|---|---|
| Every request and response | [http-run.txt](docs/evidence/week4-part-b/http-run.txt) |
| 201 with the `Location` header | [01-create-201-location.png](docs/evidence/week4-part-b/screenshots/01-create-201-location.png) |
| 400 ValidationProblemDetails | [02-validation-400.png](docs/evidence/week4-part-b/screenshots/02-validation-400.png) |
| 409 Problem Details | [03-conflict-409.png](docs/evidence/week4-part-b/screenshots/03-conflict-409.png) |

How the run was done and its full results are in [docs/evidence/week4-part-b](docs/evidence/week4-part-b/README.md).
