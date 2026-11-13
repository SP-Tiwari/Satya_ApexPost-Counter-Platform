# ApexPost API notes (Week 4 Parts B, C and D)

`src/ApexPost.Api` exposes tracked shipments and their courier scans as a REST API over the Week 3 SQL Server database. It reuses `ApexPostDbContext`, the repositories and the unit of work through `AddApexPostSqlPersistence`. Storage decisions are in [ADR 0028](docs/adr/0028-tracked-shipments-storage.md). All data is fictional.

## Endpoints

Every route starts with the API version (Part C, C4; see [API versioning](#api-versioning-week-4-part-c-c4)). The tables below show version 1, which keeps the Part B contract; version 2 has the same routes under `/api/v2`.

`{trackingNumber}` has the route constraint `regex(^APX-\d{8}$)`. A value that does not match finds no route, so it is a 404 like any other unknown tracking number.

| Verb | Route | Action | Purpose | Status codes |
|---|---|---|---|---|
| GET | `/api/v1/shipments?page=&pageSize=&status=&city=` | `ShipmentsController.List` | Paged, filtered list, ordered by tracking number. `page` defaults to 1, `pageSize` to 20, and its maximum is 50. Any signed-in user (Part D; Admin only until the Week 4 review) | 200, 400, 401 |
| GET | `/api/v1/shipments/{trackingNumber}` | `ShipmentsController.Get` | One shipment. Any signed-in user (Part D) | 200, 401, 404 |
| POST | `/api/v1/shipments` | `ShipmentsController.Create` | Creates a shipment with status `Created`; the `Location` header comes from `CreatedAtAction`. Admin (Part D) | 201, 400, 401, 403, 409, 415 |
| PUT | `/api/v1/shipments/{trackingNumber}` | `ShipmentsController.Replace` | Replaces the editable details: recipient name, phone, address and city, and the weight. Admin (Part D) | 200, 400, 401, 403, 404, 409, 415 |
| PATCH | `/api/v1/shipments/{trackingNumber}/status` | `ShipmentsController.ChangeStatus` | Changes the status only. Courier (Part D) | 200, 400, 401, 403, 404, 409, 415 |
| DELETE | `/api/v1/shipments/{trackingNumber}` | `ShipmentsController.Delete` | Deletes a shipment, with its scans, only while its status is `Created`. Admin (Part D) | 204, 401, 403, 404, 409 |
| POST | `/api/v1/shipments/{trackingNumber}/scans` | `ShipmentScansController.Upload` | Bulk upload of offline scans from Part A, idempotent by scan ID. Courier token, own `courierId` only (Part D) | 200, 400, 401, 403, 404, 415 |
| GET | `/api/v1/shipments/{trackingNumber}/scans` | `ShipmentScansController.History` | The shipment's scans, oldest first. Any signed-in user (Part D) | 200, 401, 404 |

Any route can also return 500 through the global exception handler, and a 409 when a change conflicts with another one made at the same time.

## Status codes

| Code | When | Implementation |
|---|---|---|
| 200 OK | A read, a replace, a status change or a scan upload succeeded | `Ok(...)` |
| 201 Created | A shipment was created. `Location` is the URL of `GET /api/v{version}/shipments/{trackingNumber}` in the same version | `CreatedAtAction(nameof(Get), ...)` |
| 204 No Content | A shipment was deleted | `NoContent()` |
| 400 Bad Request | Validation failed, including malformed JSON | `[ApiController]` automatic validation: `ValidationProblemDetails` |
| 401 Unauthorized | Any shipment or scan route without a valid token (missing, malformed, expired, wrong issuer, audience or signature), an anonymous request that matches no route, or the partner route without a valid `X-Api-Key` (Part D) | JWT bearer challenge written by `UseStatusCodePages`; `PartnerApiKeyMiddleware` |
| 403 Forbidden | A valid token without the required role, or a scan upload with another courier's `courierId` (Part D) | Authorization forbid written by `UseStatusCodePages`; `RequireOwnScansAttribute` |
| 404 Not Found | No shipment has the tracking number, or the route does not match (for a signed-in caller since Part D) | `ShipmentProblems.ShipmentNotFound`; `UseStatusCodePages` for unmatched routes |
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
| | `scanType` | Required; a defined `ShipmentScanType`: `Pickup`, `InTransit`, `Delivered` or `Failed`, as the number (0 to 3) or, since Part D, the name |
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

## Security (Week 4 Part D)

The design, configuration and OAuth2 background are in [SECURITY.md](SECURITY.md); this section lists what each endpoint requires. A bearer token is sent as `Authorization: Bearer <token>`; in Development it comes from `dotnet user-jwts` or `POST /dev/token`.

The rules are the same in version 1 and version 2, so the deprecated version cannot be used to get round them.

| Verb | Route (`/api/v1/...` and `/api/v2/...`) | Requires | Additional status codes |
|---|---|---|---|
| GET | `…/shipments` | Any valid bearer token (Admin role only until the Week 4 review) | 401 |
| GET | `…/shipments/{trackingNumber}` | Any valid bearer token | 401 |
| POST | `…/shipments` | Bearer token with the Admin role | 401, 403 |
| PUT | `…/shipments/{trackingNumber}` | Bearer token with the Admin role | 401, 403 |
| PATCH | `…/shipments/{trackingNumber}/status` | Bearer token with the Courier role | 401, 403 |
| DELETE | `…/shipments/{trackingNumber}` | Bearer token with the Admin role | 401, 403 |
| POST | `…/shipments/{trackingNumber}/scans` | Bearer token with the Courier role; every scan's `courierId` must equal the token's `courierId` claim, or the whole batch is refused | 401, 403 |
| GET | `…/shipments/{trackingNumber}/scans` | Any valid bearer token | 401 |
| GET | `/api/v2/partner/shipments/{trackingNumber}` (v2 only) | A partner key in the `X-Api-Key` header; no bearer token (`[AllowAnonymous]` exempts it from the fallback policy) | 401; 500 when no partner key is configured |

**Fallback policy:** any other request, including one that matches no route, needs a signed-in user. An anonymous request to an unknown route, an unsupported version or a malformed tracking number is therefore a 401, while a signed-in caller gets the 404 described above. Only the partner route, `POST /dev/token` and the Development OpenAPI documents and Swagger UI are anonymous.

**Partner route:** `PartnerShipmentsController.Get` returns `PartnerShipmentResponse { trackingNumber, status, destinationCity, latestScan }`, with `status` as its name and `latestScan` as in the v2 shipment. It is a deliberately minimal view: the recipient's name, phone number and address are not returned. Its `operationId` is `GetPartnerShipment` and its tag `Partner`.

**Every 401 and 403 is Problem Details** (`application/problem+json`). A bearer 401 also carries `WWW-Authenticate: Bearer`. The ownership 403 names the token's `courierId` in `detail` and stores no scan.

**HTTPS:** a plain `http://` request is redirected (307) to `https://` in every environment, and responses outside Development carry `Strict-Transport-Security`.

**OpenAPI security:** the v2 document's `components.securitySchemes` declares `Bearer` and `PartnerApiKey`, and every operation lists the one it requires, so Swagger UI shows an Authorize button for both. The v1 document declares `Bearer` only, because all its operations need a token too.

**Verification:** `tests/ApexPost.Api.IntegrationTests/SecurityTests.cs`. Its `AuthorizationMatrix` test runs every operation of both versions with no token, a Courier token and an Admin token (48 cases). The mapping from each requirement to its tests is in [SECURITY.md](SECURITY.md#requirement-traceability).

## OpenAPI metadata (Week 4 Part C, C3)

The built-in generator writes the documents from the controllers (Development only). Since C5 there is one per version, `/openapi/v1.json` and `/openapi/v2.json` (see [Version-specific OpenAPI documents](#version-specific-openapi-documents-week-4-part-c-c5)). The table shows version 1. Version 2 has the same operation IDs, tags and summaries under `/api/v2`, plus the Part D partner operation. Since Part D, both versions also declare 401 on every operation, and 403 on those limited to a role, as the table shows (see [Security](#security-week-4-part-d)). Each operation carries the following metadata:

| `operationId` (route name) | Verb and route | Tag | Summary (`[EndpointSummary]`) | Declared responses (`[ProducesResponseType]`) |
|---|---|---|---|---|
| `ListShipments` | GET `/api/v1/shipments` | Shipments | List shipments by page, status and city | 200, 400, 401, 500 |
| `GetShipment` | GET `/api/v1/shipments/{trackingNumber}` | Shipments | Get a shipment by tracking number | 200, 401, 404, 500 |
| `CreateShipment` | POST `/api/v1/shipments` | Shipments | Create a shipment | 201, 400, 401, 403, 409, 415, 500 |
| `ReplaceShipment` | PUT `/api/v1/shipments/{trackingNumber}` | Shipments | Replace a shipment's editable details | 200, 400, 401, 403, 404, 409, 415, 500 |
| `ChangeShipmentStatus` | PATCH `/api/v1/shipments/{trackingNumber}/status` | Shipments | Change a shipment's status | 200, 400, 401, 403, 404, 409, 415, 500 |
| `DeleteShipment` | DELETE `/api/v1/shipments/{trackingNumber}` | Shipments | Delete a shipment in Created status | 204, 401, 403, 404, 409, 500 |
| `UploadShipmentScans` | POST `/api/v1/shipments/{trackingNumber}/scans` | Scans | Upload offline courier scans | 200, 400, 401, 403, 404, 409, 415, 500 |
| `GetShipmentScans` | GET `/api/v1/shipments/{trackingNumber}/scans` | Scans | Get a shipment's scan history | 200, 401, 404, 500 |

- **Response types:** every status code an action can return is declared, with its type and media type. Success responses are `application/json`; 400 is `ValidationProblemDetails` and other errors are `ProblemDetails`, all as `application/problem+json`. 500 is declared once on each controller, because every action can return it through `ApiExceptionHandler`. The write operations also declare the 409 that the handler returns when a concurrent change outlasts the unit of work's retries.
- **Route names:** each action's route name (for example `[HttpGet(Name = "ListShipments")]`) becomes its `operationId`, and later the method name in the generated client. The names are part of the contract and must not change.
- **Tags:** `[Tags("Shipments")]` is on `ShipmentsController` and `[Tags("Scans")]` on `ShipmentScansController`, so Swagger UI groups the operations by tag.
- **XML comments:** `GenerateDocumentationFile` is on in `ApexPost.Api.csproj`, so the build fails on any public API type or member without an XML comment (`TreatWarningsAsErrors`). The generator turns the comments into document text:
  - `<remarks>` become the operation descriptions.
  - `<param>` become the parameter and request-body descriptions.
  - `<response code="…">` become the response descriptions.
  - Comments on the DTOs and their properties become the schema descriptions; `<example>` sets the request examples.
- **Two generator behaviours:**
  - The generator copies an action's XML `<summary>` over its `[EndpointSummary]`, so the two are kept word for word the same.
  - The generator writes the text of a `<param>` that is not an operation parameter, such as `cancellationToken`, onto the request body, in tag order. So the `request` tag comes last. A test checks each body's description.

**Verification:** `tests/ApexPost.Api.IntegrationTests/OpenApiDocumentationTests.cs` reads the generated documents of both versions:

| Test | Checks |
|---|---|
| `OpenApiDocument_EachOperation_HasItsMetadataAndEveryResponseCode` (17 cases: 8 in v1, 9 in v2) | For each operation: its `operationId`, its single tag, its summary and a description. Its response codes are exactly those in the table above, each with a description and the expected media type |
| `OpenApiDocument_ListsOnlyItsOwnVersion_WithOneUniqueOperationIdPerAction` (v1, v2) | Exactly the operations of the version (eight in v1; nine in v2 with the Part D partner operation), under the document's own version, with distinct operation IDs |
| `OpenApiDocument_ParametersAndRequestBodies_HaveTheirXmlDescriptions` (v1, v2) | Every list query parameter and `trackingNumber` has a description. Each request body has its request's own description |
| `OpenApiDocument_EachDto_AndEachOfItsProperties_HasAnXmlDescription` (16 cases) | Each request and response DTO of version 1, and each version 2 DTO, has a description, and so does each of its properties |

**Try it out:** on 2026-11-07 every operation was executed from Swagger UI against an isolated database with fictional data: 25 executions, each with the expected result. After versioning (C4) the same 25 executions were repeated against `/api/v1`, again all as expected. The run, with each request and the response Swagger UI displayed, and two screenshots are in [docs/evidence/week4-part-c](docs/evidence/week4-part-c/README.md).

## API versioning (Week 4 Part C, C4)

| Requirement | Implementation |
|---|---|
| URL-segment versioning, `/api/v{version}/shipments` | `Asp.Versioning.Mvc` with `UrlSegmentApiVersionReader` (`Program.cs`). The route template is `api/v{version:apiVersion}/shipments` (`ShipmentRoutes.Shipments`) |
| v1 keeps the Part B contract and is deprecated | `Controllers/ShipmentsController.cs` with `[ApiVersion("1.0", Deprecated = true)]`: the Part B actions, DTOs (`Models/`) and mapping, unchanged |
| v2 changes the response | `Controllers/V2/ShipmentsController.cs` with `[ApiVersion("2.0")]`, and `Models/V2/` |
| Both versions share the services | Both controllers call the same `TrackedShipmentApplicationService` with the same request DTOs, so they read and change the same shipments. Only the response DTOs, their mapping (`Models/V2/ShipmentMapping.cs`) and the status request differ |
| `ReportApiVersions` | `options.ReportApiVersions = true`: responses carry `api-supported-versions: 2.0` and `api-deprecated-versions: 1.0` |

**The version 2 shipment** (GET, POST, PUT, PATCH and the list items):

```json
{
  "trackingNumber": "APX-00000001",
  "recipient": { "name": "DEMO Recipient", "phone": "+919876543210", "address": "10 Demo Avenue", "city": "Pune" },
  "weightKg": 3,
  "status": "PickedUp",
  "latestScan": { "scanType": "InTransit", "location": "Pune sorting centre", "scannedAtUtc": "2026-11-07T09:30:00+00:00" }
}
```

- **`recipient`:** the four recipient fields of version 1, grouped (`RecipientResponse`).
- **`latestScan`:** the shipment's most recent scan, or `null` when it has none (`LatestScanResponse`). "Most recent" is `TrackedShipment.LatestScan` in the domain: the last scan in history order, by scan time and then scan ID, which is also the last item of `GET …/scans`. The scans are already loaded with each shipment, so no query changed.
- **`status`:** the name, such as `"PickedUp"`, through `JsonStringEnumConverter` on that property only. Version 1 still returns the number.
- **The status request:** `PATCH /api/v2/shipments/{trackingNumber}/status` takes the name, `{ "status": "PickedUp" }`, so a client sends the same value it reads (`Models/V2/ShipmentRequests.cs`). It also accepts the version 1 number. An unknown name or number is a 400.
- **Unchanged in version 2:** in the scan endpoints `scanType` stays a number (only `latestScan.scanType` is a name, the separate `LatestScanType` enum, since the Week 4 review), whose DTOs are the same in both versions. `ShipmentScansController` declares both versions. The other request DTOs, the validation rules, the status codes and the route names are also the same.

**URLs and headers:**
- **Location:** a version 2 create returns `Location: …/api/v2/shipments/{trackingNumber}`.
- **Same version:** `/api/v1.0/...` is the same version as `/api/v1/...`.
- **Unknown version:** `/api/v3/...` is a 404 Problem Details. With URL-segment versioning, an unknown version is a URL that names no resource.
- **Unversioned Part B URLs (Week 4 review):** `/api/shipments` and everything under it is an alias of version 1. A rewrite rule (`ShipmentRoutes.UnversionedAliasPattern`, registered with `UseRewriter` in `Program.cs`) turns `/api/shipments...` into `/api/v1/shipments...` before routing, which is therefore called explicitly with `UseRouting()` right after it. It is internal, not a redirect, so the method, body and query string are kept and a Part B client works unchanged. It is case-insensitive like routing and does not match `/api/shipmentsXYZ`. The documents do not list the alias. Until the review these URLs returned 404 (VERSIONING.md, changes 1 and 19).
- **Version headers on the automatic 400:** `ReportApiVersions` writes the headers from an action filter, and `[ApiController]`'s automatic 400 answers before action filters run. `Versioning/ReportApiVersionsOnEveryResponse.cs` is a global resource filter that asks the same reporter to write them on that response too.
- **415 has no version headers:** a body that is not JSON is refused by routing before any versioned action is selected, so no filter can add them.

**OpenAPI:** the API Explorer groups each operation by version (`'v'VVV`: `v1`, `v2`) and writes the version into each path. C5 builds one document per group name on top of this (see the next section).
- **Same route names in v2:** the version 2 actions use the same route names, and so the same operation IDs, because they share each route template.

**Verification:**
- **Automated:** `tests/ApexPost.Api.IntegrationTests/ApiVersioningTests.cs` has 28 tests:
  - the exact version 1 shape and the exact version 2 shape, including the most recent scan by time and a null `latestScan`
  - a version 2 create, with its `Location`, and the version 2 list, replace and status change (by name, by camel-case name, by number)
  - 400 for an unknown status, and a 409 that a version 1 client sees in the same shipment
  - version 2 scans and delete, and version 2 Problem Details
  - the version headers on 200, 404 and 400 responses in both versions
  - `v1.0`, `v3`, and the unversioned URLs
- **Domain:** `TrackedShipmentTests` has three `LatestScan` tests.
- **The other API tests:** they use `/api/v1` through the shared `Api.Shipments` helper (`ApiTestHost.cs`), so all the Part B tests now run against version 1 unchanged.

**Live HTTP:** [ApexPost.Api.http](src/ApexPost.Api/ApexPost.Api.http) now has 42 requests: requests 1–24 for version 1 and 25–42 for version 2, with a success and at least one failure for each endpoint. On 2026-11-07 all 42 were sent, in order, to the API on its `https` profile against an isolated `ApexPost_Test_*` database. Every response had its expected status, and every response except the 415 carried both version headers ([http-run.txt](docs/evidence/week4-part-c/http-run.txt)).

## Version-specific OpenAPI documents (Week 4 Part C, C5)

| Requirement | Implementation |
|---|---|
| `/openapi/v1.json` and `/openapi/v2.json` from the Asp.Versioning API Explorer group names | `Program.cs` registers the built-in `AddOpenApi(documentName, …)` once for each group name, `v1` and `v2`. The generator's default filter puts an operation into a document only when its API Explorer group name is the document name, so each document lists only its own version. `MapOpenApi()` serves both at `/openapi/{documentName}.json`. |
| Both documents in the Swagger UI dropdown, labelled "v1 (deprecated)" and "v2" | `Program.cs` adds one `SwaggerEndpoint` for each entry of the versioning library's `IApiVersionDescriptionProvider`, newest first. Each label is the group name, with " (deprecated)" added when `IsDeprecated` is true. Swagger UI opens on the first entry, "v2". |

**What each document contains** (generated on 2026-11-07):

| | `/openapi/v1.json` | `/openapi/v2.json` |
|---|---|---|
| `info.version` | `v1` | `v2` |
| `info.description` | The API description, then "Version 1.0 is deprecated; use version 2.0." | The API description |
| Paths | The 8 operations, all under `/api/v1/` | The 8 operations, all under `/api/v2/` |
| Operation IDs | `ListShipments`, `GetShipment`, `CreateShipment`, `ReplaceShipment`, `ChangeShipmentStatus`, `DeleteShipment`, `UploadShipmentScans`, `GetShipmentScans` | The same 8 |
| `deprecated` | `true` on every operation | Absent |
| `ShipmentResponse` | `trackingNumber`, `recipientName`, `recipientPhone`, `recipientAddress`, `recipientCity`, `weightKg`, `status` | `trackingNumber`, `recipient` (`RecipientResponse`), `weightKg`, `status`, `latestScan` (`LatestScanResponse` or null) |
| `ShipmentStatus` | `integer`, values 0–5 with their names (C6) | `string` enum: `Created`, `PickedUp`, `InTransit`, `OutForDelivery`, `Delivered`, `Failed` |
| `ShipmentScanType` | `integer`, values 0–3 with their names (C6) | The same as v1 |
| `RecipientResponse`, `LatestScanResponse` | Not present | `name`, `phone`, `address`, `city`; `scanType`, `location`, `scannedAtUtc` |

- **Title and contact:** both documents have the C1 title, contact and description. `ApiDocumentTransformer` runs on each document.
- **Deprecation in the document:** for a deprecated version, `ApiDocumentTransformer` (`src/ApexPost.Api/OpenApi/ApiDocumentTransformer.cs`) marks every operation `deprecated: true` and adds the sentence naming the version to use instead. It reads both facts from `IApiVersionDescriptionProvider`, so they come from `[ApiVersion("1.0", Deprecated = true)]` and are not repeated by hand. Swagger UI shows each deprecated operation struck through, with "Warning: Deprecated".
- **Schema names:** both versions have a `ShipmentResponse`, `ShipmentPageResponse` and `UpdateStatusRequest`. They do not clash, because each document holds only its own version's types.
- **Unknown documents:** `/openapi/v3.json` is a 404. Outside Development, both documents and Swagger UI are 404 (C2).
- **Published copies (C7):** both documents are saved, byte for byte, as [contracts/v1.json](contracts/v1.json) and [contracts/v2.json](contracts/v2.json). `PublishedContractTests` fails if either no longer matches what the API generates. How they are published, and every change between the versions, are in [VERSIONING.md](VERSIONING.md).
- **Analyzer rules:** `Asp.Versioning` 10 reports AV0029 and AV0030. They recommend its separate `Asp.Versioning.OpenApi` package (`AddApiVersioning().AddOpenApi()`, `MapOpenApi().WithDocumentPerVersion()`) for per-version documents. Here the built-in `AddOpenApi()` already produces one document per API Explorer group name, as the assignment asks, so the project does not add that package and suppresses both rules. The reason is written in `ApexPost.Api.csproj`.

**Verification:**
- **Automated:** `tests/ApexPost.Api.IntegrationTests/OpenApiDocumentationTests.cs`:

| Test | Checks |
|---|---|
| `OpenApiDocument_InDevelopment_IsServedWithTheApiInfo` (v1, v2) | Each document is served, parses with no errors in `Microsoft.OpenApi`, and has the title, contact, description and its own `info.version` |
| `OpenApiDocument_ListsOnlyItsOwnVersion_WithOneUniqueOperationIdPerAction` (v1, v2) | Every path starts with the document's own `/api/{version}/`, and the operations are exactly the expected ones (eight in v1, nine in v2), with unique operation IDs |
| `OpenApiDocument_EachOperation_HasItsMetadataAndEveryResponseCode` (17 cases) | The C3 metadata of every operation in both documents |
| `OpenApiDocument_MarksOnlyTheDeprecatedVersion` (v1, v2) | All eight v1 operations are `deprecated` and the v1 description names version 2. No v2 operation is deprecated |
| `OpenApiDocument_V1_HasThePartBShipmentSchema` | The flat v1 `ShipmentResponse`, the integer `ShipmentStatus`, and no v2 schema |
| `OpenApiDocument_V2_HasTheNestedRecipient_TheLatestScan_AndAStringStatus` | The v2 `ShipmentResponse`, `RecipientResponse`, `LatestScanResponse` and the string `ShipmentStatus` enum |
| `SwaggerUi_InDevelopment_ListsBothVersionDocuments_V2First` | Swagger UI's settings list exactly `/openapi/v2.json` as "v2" and then `/openapi/v1.json` as "v1 (deprecated)". Each one is served and describes the version on its label |
| `OpenApiDocument_ForAnUnknownVersion_IsNotServed` | `/openapi/v3.json` is a 404 |
| `Documentation_OutsideDevelopment_IsNotServed` (6 cases) | Both documents and Swagger UI are 404 in Production and Staging |

- **Swagger UI:** on 2026-11-07, Microsoft Edge (headless) was driven through the dropdown in Swagger UI and each version was explored ([swagger-ui-versions-run.txt](docs/evidence/week4-part-c/swagger-ui-versions-run.txt)):
  - The dropdown offered "v2" and "v1 (deprecated)" and opened on "v2".
  - "v2" showed eight `/api/v2/` operations. The 200 example of `GetShipment` had the nested `recipient`, the string `status` and `latestScan`.
  - Choosing "v1 (deprecated)" loaded `/openapi/v1.json`. It showed the deprecation sentence and eight `/api/v1/` operations, all marked deprecated. The `GetShipment` example had the flat Part B shape with a numeric `status`.
  - Screenshots: [c5-00-swagger-ui-v2.png](docs/evidence/week4-part-c/screenshots/c5-00-swagger-ui-v2.png) and [c5-01-swagger-ui-v1-deprecated.png](docs/evidence/week4-part-c/screenshots/c5-01-swagger-ui-v1-deprecated.png).

## Schema types for generated clients (Week 4 Part C, C6)

The generated client (C6, see [README.md](README.md#client-sdk-week-4-part-c-c6)) can only be as precise as the document. Three facts were not stated in the documents the built-in generator writes, so NSwag produced loose types. `ContractSchemaTransformer` (`src/ApexPost.Api/OpenApi/ContractSchemaTransformer.cs`) is a schema transformer registered on both documents in `Program.cs`. It adds each fact; the JSON the API reads and writes does not change.

| Schema | Before | After | Generated type before → after |
|---|---|---|---|
| v2 `ShipmentStatus` (written by name) | `{"enum": ["Created", …]}` with no `type` | `"type": "string"` added | An enum without a string converter, so reading `"status": "Created"` would fail → `ShipmentStatus` with `JsonStringEnumConverter<ShipmentStatus>` |
| `ShipmentScanType`, v1 `ShipmentStatus` (written by number) | `{"type": "integer"}` only | `"enum": [0, 1, …]` and `"x-enumNames": ["Pickup", …]` added | `int` → `ShipmentScanType` / `ShipmentStatus` enum with the API's names and numbers |
| Every `decimal` (`weightKg`) | `"format": "double"` | `"format": "decimal"` | `double` → `decimal` |

- **`x-enumNames`:** a vendor extension that NSwag reads to name the members of a numeric enum. Tools that do not know it ignore it.
- **The v1 document:** v1 gains the same enum values and names. This states its existing numeric enums more fully and changes neither its wire format nor its operations.
- **Verification:** `OpenApiDocumentationTests.OpenApiDocument_StatesTheEnumAndDecimalTypes` (v1, v2) checks each fact in both documents. `ClientSdkTests.GeneratedModels_HaveTheV2Types` checks the resulting C# types, and the other `ClientSdkTests` run the client against the API.

## Verification

**Automated:** `tests/ApexPost.Api.IntegrationTests` runs every endpoint against a disposable `ApexPost_Test_*` SQL Server database built by the real migrations. User Secrets are removed, so a developer's database is never used. Every error assertion checks `application/problem+json`, the `status` and a `title`.

- **Final Part C verification (latest):** on 2026-11-07 at 16:44 UTC, after C7, the full run passed 1,233 of 1,233 tests, with 0 failed and 0 skipped. 195 of them are API integration tests. The clean build had 0 warnings and 0 errors ([docs/evidence/week4-part-c](docs/evidence/week4-part-c/README.md#final-part-c-verification-2026-11-07)).
- **Historical, Week 4 Part B:** the full run on 2026-11-07 at 11:14 UTC passed 1,124 of 1,124 tests, 98 of them API integration tests, with 0 warnings ([test-results.txt](docs/evidence/week4-part-b/test-results.txt)). The table below lists the Part B tests that verify each Part B requirement.

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
