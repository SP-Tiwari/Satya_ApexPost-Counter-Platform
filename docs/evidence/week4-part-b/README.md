# Week 4 Part B evidence

Evidence for the shipments API in `src/ApexPost.Api`. The endpoints and rules are described in [API-NOTES.md](../../../API-NOTES.md). All data is fictional.

| File | Shows |
|---|---|
| [http-run.txt](http-run.txt) | All 24 requests in [ApexPost.Api.http](../../../src/ApexPost.Api/ApexPost.Api.http), sent to the running API on 2026-11-07, with each request as sent and the real response: status, `Location`, content type and body |
| [screenshots/01-create-201-location.png](screenshots/01-create-201-location.png) | Request 1: `201 Created`, the `Location` header and the created shipment |
| [screenshots/02-validation-400.png](screenshots/02-validation-400.png) | Request 3: `400`, `application/problem+json`, and the `errors` for the tracking number, phone, weight and missing city |
| [screenshots/03-conflict-409.png](screenshots/03-conflict-409.png) | Request 2: `409`, `application/problem+json`, and the `detail` "Shipment APX-00000001 already exists." |
| [test-results.txt](test-results.txt) | The clean build and the full test run on 2026-11-07: 1,124 of 1,124 passed, including the 98 API integration tests, with 0 warnings |

## How the live run was done

1. **Database:** an isolated database, `ApexPost_Test_<guid>`, was created with its files in `E:\SqlData\ApexPost\Tests`, as the test fixture creates them. `dotnet ef database update --connection …` then applied the five migrations, including `AddTrackedShipments` (22 tables). No developer database, User Secrets or LocalDB setting was used or changed. The connection string existed only as a process environment variable.
2. **API:** `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`, trusted development certificate).
3. **Requests:** a script outside the repository read `ApexPost.Api.http`, substituted its `@` variables and sent every request in file order, exactly as written, with `System.Net.Http.HttpClient`. It printed the request and the response; `http-run.txt` is that output. The requests were not sent from the Visual Studio `.http` editor, which could not be operated from the session that ran them.
4. **Screenshots:** after the full run, the two shipment tables were emptied. Requests 1, 3 and 2 were then each sent live in a console window by the same script, and each window was captured. They are genuine captures of those live calls, not of saved output, so their trace IDs and times differ from `http-run.txt`. The "Activate Windows" text is the machine's own watermark.
5. **Cleanup:** the API was stopped and the database was dropped. A read-only query on `master` afterwards listed only `ApexPostDb`.

## Results of the live run

All 24 responses had the status their request is labelled with in `ApexPost.Api.http`:

| Requests | Endpoint | Results |
|---|---|---|
| 1–5 | `POST /api/shipments` | 201 with `Location: https://localhost:7280/api/shipments/APX-00000001`; 409 duplicate; 400 with four field errors; 415; 201 |
| 6–8 | `GET /api/shipments` | 200 with both shipments; 200 filtered by `status=Created&city=Pune` (one item); 400 for `pageSize=51` |
| 9–10 | `GET /api/shipments/{trackingNumber}` | 200; 404 |
| 11–12 | `PUT /api/shipments/{trackingNumber}` | 200; 400 for weight 0.05 |
| 13–15 | `PATCH /api/shipments/{trackingNumber}/status` | 200 to `PickedUp`; 409 "A shipment cannot move from PickedUp to Delivered."; 400 for status 42 |
| 16, 23–24 | `DELETE /api/shipments/{trackingNumber}` | 409 after `PickedUp`; 204 for the `Created` shipment; 404 once deleted |
| 17–20 | `POST /api/shipments/{trackingNumber}/scans` | 200 accepted 2; 200 with `duplicatesSkipped` 1 and `rejected` 2 (malformed ID, other tracking number), each with its reason; 400 for a 2099 scan time; 404 |
| 21–22 | `GET /api/shipments/{trackingNumber}/scans` | 200, two scans oldest first; 404 |

Every error response was `application/problem+json`. The 500 path cannot be triggered from the `.http` file; `ErrorHandlingTests` covers it.

## What the automated tests cover

The same behaviour is verified on every run by `tests/ApexPost.Api.IntegrationTests`, against a disposable `ApexPost_Test_*` SQL Server database built by the real migrations:

| Test class | Tests | Covers |
|---|---:|---|
| `ShipmentEndpointsTests` | 36 | POST (201 and Location, 409 duplicate, 400 per field and per maximum length, both weight limits, 415, malformed JSON), GET (200, 404, route constraint), PUT (200, 400, 404, 415), DELETE (204 and cascade to scans, 409 after `Created`, 404) |
| `ShipmentStatusTests` | 14 | The two full lifecycles, seven refused transitions (409, status unchanged), missing, undefined and string status (400), 404, 415 |
| `ShipmentListTests` | 9 | Defaults (page 1, 20 items), page 2, `pageSize=50`, 400 for `pageSize` 0 and 51, `page=0` and an unknown status, filters by status, city and both |
| `ScanUploadTests` | 35 | Accepted scans and the history order, re-upload and in-request duplicates, five malformed scan IDs, tracking-number mismatch, scan ID under another shipment, no status change, historical scans after `Delivered` and `Failed`, the strict future-time boundary with a fixed server clock, 400 per invalid field and batch size, 404, 415 |
| `ErrorHandlingTests` | 4 | A 500 in Production and Staging with no exception text or stack trace, the detail in Development, and a concurrency conflict as 409 |

Every error assertion also checks `application/problem+json`, the `status` and a `title` (`Api.AssertProblemAsync`).

The Swagger UI screenshots belong to Part C.
