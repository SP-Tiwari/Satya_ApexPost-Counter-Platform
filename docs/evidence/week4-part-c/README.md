# Week 4 Part C evidence

Evidence for the OpenAPI documents, Swagger UI and API versioning of `src/ApexPost.Api`. The metadata is described in [API-NOTES.md](../../../API-NOTES.md#openapi-metadata-week-4-part-c-c3), the versioning in [API-NOTES.md](../../../API-NOTES.md#api-versioning-week-4-part-c-c4) the version-specific documents in [API-NOTES.md](../../../API-NOTES.md#version-specific-openapi-documents-week-4-part-c-c5), and the client SDK in [README.md](../../../README.md#client-sdk-week-4-part-c-c6). All data is fictional.

| File | Shows |
|---|---|
| [tryitout-run.txt](tryitout-run.txt) | C3 "Try it out": 25 executions covering all 8 operations, each with the request Swagger UI sent (its own curl line) and the response it displayed: status, headers and body |
| [http-run.txt](http-run.txt) | C4: all 42 requests in [ApexPost.Api.http](../../../src/ApexPost.Api/ApexPost.Api.http), 24 for version 1 and 18 for version 2, sent to the running API, with each request and the real response: status, `Location`, content type, `api-supported-versions`, `api-deprecated-versions` and body |
| [screenshots/c3-00-swagger-ui-operations.png](screenshots/c3-00-swagger-ui-operations.png) | Swagger UI at `/swagger`: the title, description and contact from the document transformer, the `Shipments` and `Scans` tags, each operation's summary, and the schemas |
| [screenshots/c3-01-create-201-location.png](screenshots/c3-01-create-201-location.png) | `CreateShipment` after Try it out and Execute: the XML remarks and body description, `201` with its `Location` header, and the documented 201, 400, 409, 415 and 500 responses with their descriptions and media types. *Historical:* taken before versioning (C4), so its URL is `/api/shipments/...`. The current version is `final-01` below |
| [swagger-ui-versions-run.txt](swagger-ui-versions-run.txt) | C5: what Swagger UI showed for each entry of its version dropdown: the entries, the selected one, the title, the description, the operations (with `[deprecated]` where Swagger UI marks them), the `GetShipment` 200 example, and the documents it fetched |
| [screenshots/c5-00-swagger-ui-v2.png](screenshots/c5-00-swagger-ui-v2.png) | Swagger UI opened on "v2" from `/openapi/v2.json`: the eight `/api/v2/` operations and the `GetShipment` 200 example with the nested `recipient`, the string `status` and `latestScan` |
| [screenshots/c5-01-swagger-ui-v1-deprecated.png](screenshots/c5-01-swagger-ui-v1-deprecated.png) | "v1 (deprecated)" selected, from `/openapi/v1.json`: the deprecation sentence, the eight `/api/v1/` operations struck through as deprecated, "Warning: Deprecated", and the flat Part B `GetShipment` example with a numeric `status` |
| [consumer-run.txt](consumer-run.txt) | C6: two runs of `ApexPost.Client.Consumer` against the running API, a direct v2 read of the first run's shipment, and a run with the API stopped |
| [../../../contracts/](../../../contracts/) | C7: the published `v1.json` and `v2.json`, described below |
| [screenshots/final-01-create-201-location-v2.png](screenshots/final-01-create-201-location-v2.png) | Final verification, Swagger UI on "v2": `CreateShipment` executed, `201` with `location: https://localhost:7280/api/v2/shipments/APX-00000201`, the v2 body (nested `recipient`, `"status": "Created"`, `latestScan: null`) and both version headers |
| [screenshots/final-02-validation-400-v2.png](screenshots/final-02-validation-400-v2.png) | Final verification: `CreateShipment` with an invalid body, `400` `ValidationProblemDetails` with an error for each of `TrackingNumber`, `RecipientPhone`, `WeightKg` and the missing `RecipientCity`, as `application/problem+json` |
| [screenshots/final-03-conflict-409-v2.png](screenshots/final-03-conflict-409-v2.png) | Final verification: the same `CreateShipment` body again, `409` Problem Details "Shipment APX-00000201 already exists." |
| [swagger-ui-final-run.txt](swagger-ui-final-run.txt) | Final verification: for each of the three executions, the body Swagger UI sent, its curl line, the request URL, and the status, headers and body it displayed |

## How the Try it out run was done (2026-11-07)

1. **Database:** an isolated database, `ApexPost_Test_<guid>`, was created on `.` with its files in `E:\SqlData\ApexPost\Tests`, as the test fixture creates them. `dotnet ef database update --connection …` applied the five migrations (22 tables). No developer database, User Secrets or LocalDB setting was used or changed; the connection string existed only as a process environment variable.
2. **API:** `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`, trusted development certificate).
3. **Swagger UI:** a script outside the repository drove Microsoft Edge (headless, through `puppeteer-core`) on `https://localhost:7280/swagger/index.html`. For each operation it expanded the block, clicked **Try it out**, filled the parameters or the request body, clicked **Execute** and read the result from the Swagger UI page itself. So every request was built and sent by Swagger UI from the OpenAPI document.
4. **Cleanup:** the API was stopped and the database was dropped.

## Results

All 25 executions behaved as expected:

| Steps | Operation (`operationId`) | Results shown by Swagger UI |
|---|---|---|
| 1–4 | `CreateShipment` | 201 with `location: https://localhost:7280/api/shipments/APX-00000101`; 409 duplicate; 400 with field errors; 201 |
| 5–8 | `ListShipments` | 200; 200 filtered by `Status=0&City=Pune`; `PageSize=51` blocked by Swagger UI (see below); 400 for `Status=9` |
| 9–10 | `GetShipment` | 200; 404 |
| 11–13 | `ReplaceShipment` | 200; 400 for weight 0.05; 404 |
| 14–16 | `ChangeShipmentStatus` | 200 to `PickedUp`; 409 for `PickedUp` to `Delivered`; 400 for status 42 |
| 17–20 | `UploadShipmentScans` | 200 with `accepted` 2; 200 with `duplicatesSkipped` 2 on re-upload; 400 for a 2099 scan time; 404 |
| 21–22 | `GetShipmentScans` | 200 with both scans oldest first; 404 |
| 23–25 | `DeleteShipment` | 409 after `PickedUp`; 204 for the `Created` shipment; 404 once deleted |

Every error response was `application/problem+json`.

**What Try it out cannot send:**
- **`PageSize=51`:** the document declares `maximum: 50`, so Swagger UI marks the field invalid and sends nothing (step 7). The server's own 400 for `pageSize=51` is covered by `ShipmentListTests` and by request 8 in [ApexPost.Api.http](../../../src/ApexPost.Api/ApexPost.Api.http).
- **415:** Swagger UI offers only the JSON media types the operation declares, so it cannot send another content type. 415 is covered by the integration tests and the `.http` file.
- **500 and the concurrency 409:** neither can be caused from Swagger UI. `ErrorHandlingTests` covers both.

## C3 Try it out after versioning (C4)

The run above was made before versioning, when the routes had no version segment. After C4, the same script repeated all 25 executions from Swagger UI. Swagger UI then sent them to `/api/v1/...`, taken from the document's paths, and all 25 again behaved as expected. The files above are from the first run.

## C4 live HTTP run (2026-11-07)

1. **Database:** a new isolated `ApexPost_Test_<guid>` database on `.`, with files in `E:\SqlData\ApexPost\Tests`, and the five migrations applied with `dotnet ef database update --connection …`. No developer database or User Secrets setting was used or changed.
2. **API:** `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`).
3. **Requests:** a script outside the repository read `ApexPost.Api.http`, substituted its `@` variables and sent the 42 requests in file order, exactly as written. `http-run.txt` is its output.
4. **Cleanup:** the API was stopped and the database was dropped.

**Results:**
- **Status:** all 42 responses had the status in their request's title.
- **Location:** request 25 returned `Location: https://localhost:7280/api/v2/shipments/APX-00000011`.
- **Shared data:** request 30 read the version 1 shipment `APX-00000001` through version 2. It came back with a nested `recipient`, `"status": "PickedUp"` and the `latestScan` from request 17.
- **Version headers:** every response except request 4, the 415, carried `api-supported-versions: 2.0` and `api-deprecated-versions: 1.0`. The 415 is answered by routing before a versioned action is selected (see [API-NOTES.md](../../../API-NOTES.md#api-versioning-week-4-part-c-c4)).

## C5 Swagger UI version dropdown (2026-11-07)

1. **API:** `dotnet run --project src/ApexPost.Api --launch-profile http` (Development, `http://localhost:5280`). Only the documents and Swagger UI were used, and they never open the database. The connection string was a process environment variable naming a database that is never opened. No developer database or User Secrets setting was used or changed.
2. **Swagger UI:** a script outside the repository drove Microsoft Edge (headless, through `puppeteer-core`) on `http://localhost:5280/swagger/index.html`. For each version it read the dropdown and the rendered page, and it expanded `GetShipment` to show its 200 example. It switched versions with the dropdown, as a user would. `swagger-ui-versions-run.txt` is its output.
3. **Cleanup:** the API was stopped.

**Results:**
- **Dropdown:** the entries were "v2" and "v1 (deprecated)", in that order, and Swagger UI opened on "v2".
- **v2:** title "ApexPost Logistics API v2", the link `/openapi/v2.json`, and the eight operations, all under `/api/v2/`. None was deprecated. The `GetShipment` example had `recipient { name, phone, address, city }`, `"status": "Created"` and `latestScan`.
- **v1 (deprecated):** choosing it fetched `/openapi/v1.json`. Title "ApexPost Logistics API v1", with the description ending "Version 1.0 is deprecated; use version 2.0." The eight operations were all under `/api/v1/` and all marked deprecated. The `GetShipment` example had the flat Part B fields and `"status": 0`.
- **Documents fetched:** `200 /openapi/v2.json`, then `200 /openapi/v1.json`.
- **Same checks automated:** the dropdown entries and their order are also checked by `SwaggerUi_InDevelopment_ListsBothVersionDocuments_V2First`, and the contents of each document by the other `OpenApiDocumentationTests`.

## C6 Client SDK (2026-11-07)

1. **Generation:** the API ran on its `http` profile in Development. `dotnet tool restore` installed NSwag 14.7.1 from [dotnet-tools.json](../../../dotnet-tools.json). `dotnet nswag run src/ApexPost.Client/nswag.json` then wrote `src/ApexPost.Client/Generated/ApexPostApiClient.cs` from `http://localhost:5280/openapi/v2.json`.
2. **Reproducibility:** the same command was run a second time against the same document. The file's SHA-256 was the same before and after (`18e69a720c489bb3…`), so the committed file is exactly the generator's output.
3. **Database:** a new isolated `ApexPost_Test_*` database on `.`, with files in `E:\SqlData\ApexPost\Tests`. `dotnet ef database update --connection …` applied the five migrations (22 tables). No developer database or User Secrets setting was used or changed, and the existing `ApexPost_Test_*` databases were not touched.
4. **Consumer:** with the API on its `https` profile against that database, `dotnet run --project src/ApexPost.Client.Consumer` ran twice with its default `appsettings.json` (`https://localhost:7280/`). `consumer-run.txt` is the output.
5. **Cleanup:** the API was stopped, the consumer was run once more to show the unreachable-API message, and that database was dropped.

**Results:**
- **Both runs:** each created a shipment with a new tracking number, fetched it (`status Created`, no latest scan), uploaded two scans (`accepted 2, duplicates skipped 0, rejected 0`) and handled `APX-00000000` as `404 Shipment not found.` through `ApiException<ProblemDetails>`. Both exited with code 0.
- **Direct read:** reading the first run's shipment through `/api/v2` returned its nested `recipient`, `"status": "Created"` and the InTransit scan the consumer uploaded as `latestScan`.
- **API stopped:** the consumer printed `The API could not be reached at https://localhost:7280/: …` and exited with code 1.

## C7 contracts (2026-11-07)

1. **Publishing:** the API ran on its `http` profile in Development (`http://localhost:5280`). Its connection string was a process environment variable naming a database that is never opened, because the documents do not use it. The two documents were saved with `Invoke-WebRequest http://localhost:5280/openapi/v1.json -UseBasicParsing -OutFile contracts/v1.json`, and the same for `v2.json`.
2. **Byte check:** each saved file was compared with a fresh download from the same API (`cmp`), and both were identical. SHA-256: `v1.json` `236d49f7d2493ea9…` (38,216 bytes), `v2.json` `28632766ef6d17be…` (39,440 bytes). Both are LF-only JSON, and `.gitattributes` keeps them so.
3. **Client from the file:** `src/ApexPost.Client/nswag.json` was changed to read `../../contracts/v2.json`. With the API stopped (a request to it failed to connect), `dotnet nswag run src/ApexPost.Client/nswag.json` wrote `Generated/ApexPostApiClient.cs` with the same SHA-256 as before (`18e69a720c489bb3…`). So the client generated from the published contract is the client of C6.
4. **The test detects drift:** a copy of `v2.json` in the test output folder, never the repository file, was edited to change one operation summary. `PublishedContract_MatchesTheDocumentTheApiGenerates(v2)` then failed with "contracts/v2.json differs from /openapi/v2.json in: paths". The copy was restored and the test passed again.
5. **Cleanup:** the API was stopped. No database was created, changed or dropped.

## Final Part C verification (2026-11-07)

This is the final verification of Part C, made after C7. The earlier sections record each milestone's own runs.

**Build and tests:** at 16:44 UTC, `dotnet build ApexPost.slnx --no-incremental` finished with 0 warnings and 0 errors. `dotnet test ApexPost.slnx --no-build` then passed 1,233 of 1,233 tests, with 0 failed and 0 skipped.

| Project | Passed |
|---|---|
| ApexPost.Domain.Tests | 279 |
| ApexPost.Application.Tests | 140 |
| ApexPost.Courier.Offline.Tests | 77 |
| ApexPost.Api.IntegrationTests | 195 |
| ApexPost.Infrastructure.Tests | 239 |
| ApexPost.Web.IntegrationTests | 303 |
| **Total** | **1,233** |

The last change to any code or test file came before that build. Since then only documentation and evidence have changed, so the run verifies the submitted code.

**Reproducibility, checked after the run.** Nothing was written in the repository:
- **Client:** NSwag ran with a copy of `src/ApexPost.Client/nswag.json` whose only changes were absolute paths for the input (`contracts/v2.json`) and an output outside the repository. The generated file was byte-identical to `src/ApexPost.Client/Generated/ApexPostApiClient.cs`.
- **Contracts:** the API ran on its `http` profile with a connection string to a database that is never opened. The documents it served at `/openapi/v1.json` and `/openapi/v2.json` were byte-identical to `contracts/v1.json` and `contracts/v2.json`.
- **Live spot checks:** the Swagger UI dropdown listed "v2", then "v1 (deprecated)". `/openapi/v3.json` and `/api/shipments` returned 404. A v2 400 response carried `api-supported-versions: 2.0` and `api-deprecated-versions: 1.0`.

### Swagger UI screenshots on version 2: 201, 400 and 409 (2026-11-07, 16:58 UTC)

These replace, as current evidence, the 201, 400 and 409 screenshots taken before versioning ([week4-part-b](../week4-part-b/README.md) and `c3-01` above, both under `/api/shipments/...`). The earlier screenshots are kept as historical evidence. No application code, test, contract or client changed for this run.

1. **Existing databases recorded first.** Before anything was created, the name, ID, creation date, state and file size of every `ApexPost*` database on `.` was recorded: `ApexPostDb` and four `ApexPost_Test_*` databases.
2. **One temporary database.** A new database named `ApexPost_Test_5f43915f0ed1402085e970b35dd8576d` was created, with its files in `E:\SqlData\ApexPost\Tests`. Its name was checked against `ApexPost_Test_` plus 32 hexadecimal characters, and checked not to exist, before it was created. `dotnet ef database update --connection …` then applied the five migrations (22 tables).
3. **API.** `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`). The connection string existed only as an environment variable of that process. No User Secrets or settings file was used or changed.
4. **Swagger UI.** A script outside the repository drove Microsoft Edge (headless, through `puppeteer-core`) on `https://localhost:7280/swagger/index.html`, which opened on "v2". On `POST /api/v2/shipments` (`CreateShipment`) it clicked **Try it out**, typed each body and clicked **Execute** three times. Each screenshot spans the operation from its title to the response Swagger UI displayed.

| Execution | Body | Result shown by Swagger UI | Screenshot |
|---|---|---|---|
| 1 | Valid, `APX-00000201` | `201`; `location: https://localhost:7280/api/v2/shipments/APX-00000201`; v2 body | [final-01](screenshots/final-01-create-201-location-v2.png) |
| 2 | The same body again | `409`; "Shipment APX-00000201 already exists." | [final-03](screenshots/final-03-conflict-409-v2.png) |
| 3 | Tracking number `APX-1`, phone `12345`, weight 0.05, no city | `400` `ValidationProblemDetails` with the four field errors | [final-02](screenshots/final-02-validation-400-v2.png) |

All three responses also carried `api-supported-versions: 2.0` and `api-deprecated-versions: 1.0`. The errors were `application/problem+json`. The full output is [swagger-ui-final-run.txt](swagger-ui-final-run.txt).

5. **Cleanup.** The API was stopped, and only `ApexPost_Test_5f43915f0ed1402085e970b35dd8576d` was dropped, by its exact name; none of its files remain. The same database details were then recorded again. They were identical to step 1 for `ApexPostDb` and the four existing `ApexPost_Test_*` databases, none of which was opened, changed or dropped.
