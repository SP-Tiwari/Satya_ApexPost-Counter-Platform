# Week 4 Part D evidence: API security (Milestone 1)

Evidence for the JWT authentication, role authorization, courier ownership rule, partner API key, HTTPS and OpenAPI security schemes of `src/ApexPost.Api`. The design is in [SECURITY.md](../../../SECURITY.md) and the per-endpoint rules in [API-NOTES.md](../../../API-NOTES.md#security-week-4-part-d). Everything was captured on 2026-11-09 against the running API, on the `feature/week4-part-d-api-security` branch. All data is fictional.

**No secret is in this folder.**
- **Tokens:** every access token was a Development token from `POST /dev/token`, signed with a random key that existed only in the API process's environment for the run.
- **Partner key:** random as well, and it is not recorded.
- **Text files:** tokens and the key are replaced by placeholders such as `Bearer <courierToken: redacted>`.
- **Screenshots:** before each screenshot, token and key text on the page was replaced with `<access token redacted>` or `<partner key redacted>`. The Authorize dialog shows Swagger UI's own `******` mask.

## Files

| File | Shows |
|---|---|
| [http-run.txt](http-run.txt) | All 63 requests in [ApexPost.Api.http](../../../src/ApexPost.Api/ApexPost.Api.http), sent in order to the running API. It starts with a summary of each request's status against the status in its title (all 63 match), followed by each request and its real response: status, content type, `WWW-Authenticate`, `Location`, version headers and body |
| [swagger-ui-run.txt](swagger-ui-run.txt) | The Swagger UI run: each Authorize step (which token or key was entered), each Try it out execution with its status and response body, and the screenshot taken |
| [screenshots/](screenshots/) | The 16 screenshots listed below |

## Required checks

| Check | `.http` request | Result | Swagger UI screenshot |
|---|---|---|---|
| Missing JWT → 401 | 43 (v2 GET), 48 (v2 upload), 56 (v1 list), 61 (v2 scan history) | 401 Problem Details with `WWW-Authenticate: Bearer` | [d2-01](screenshots/d2-01-v2-get-without-token-401.png) |
| Invalid JWT → 401 | 44 | 401 | — |
| Insufficient role → 403 | 46 (v2 PATCH as Admin), 47 (v2 DELETE as Courier), 57 (v1 list as Courier), 58 (v1 PATCH as Admin), 59 (v1 DELETE as Courier), 60 (v2 create as Courier) | 403 Problem Details | [d2-02](screenshots/d2-02-v2-delete-as-courier-403.png), [d2-04](screenshots/d2-04-v1-patch-as-admin-403.png), [d2-05](screenshots/d2-05-v1-list-as-courier-403.png) |
| Courier token with another `courierId` → 403 | 49 | 403 "Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored." | [d2-03](screenshots/d2-03-v2-upload-other-courier-403.png) |
| Courier token with the correct `courierId` → success | 50 (scan type sent as the name `"InTransit"`), 17, 37 | 200, `accepted` 1 | [d2-07](screenshots/d2-07-v2-upload-own-courier-200.png) |
| Invalid partner API key → 401 | 53 (wrong key), 52 (no key) | 401 "A valid partner API key is required." | [d3-01](screenshots/d3-01-partner-invalid-key-401.png) |
| Valid partner API key without a JWT → success | 51 (and 54: valid key, unknown tracking number → 404) | 200 `{ trackingNumber, status, destinationCity, latestScan }`, without the recipient's name, phone or address | [d3-02](screenshots/d3-02-partner-valid-key-no-jwt-200.png): the request sends only `X-Api-Key`, with no `Authorization` header |
| Allowed roles succeed, v1 and v2 | 1–42 (each with the token its rule requires), 45 | As in Parts B and C: 200, 201, 204, and the same 400, 404, 409 and 415 errors | [d2-06](screenshots/d2-06-v2-create-as-admin-201.png) (v2 create as Admin), [d2-08](screenshots/d2-08-v2-get-as-courier-200.png) (v2 GET as Courier), [d2-09](screenshots/d2-09-v1-list-as-admin-200.png) (v1 list as Admin), [d2-10](screenshots/d2-10-v1-patch-as-courier-200.png) (v1 PATCH as Courier) |
| Development token endpoint | 0a, 0b (200), 55 (400 for an unknown role) | Tokens issued; response bodies recorded with `accessToken` redacted | — |

## Swagger UI security schemes and lock icons

| Screenshot | Shows |
|---|---|
| [d5-01-swagger-v2-locks.png](screenshots/d5-01-swagger-v2-locks.png) | "v2": the **Authorize** button and a lock icon on all nine operations, including the partner operation |
| [d5-04-swagger-v1-locks.png](screenshots/d5-04-swagger-v1-locks.png) | "v1 (deprecated)": the Authorize button and a lock icon on all eight deprecated operations |
| [d5-02-swagger-v2-authorize-dialog.png](screenshots/d5-02-swagger-v2-authorize-dialog.png) | The v2 Authorize dialog: `Bearer (http, Bearer)` with its description, and `PartnerApiKey (apiKey)`, header `X-Api-Key` |
| [d5-03-swagger-v2-authorized-courier.png](screenshots/d5-03-swagger-v2-authorized-courier.png) | After entering a Courier token and selecting Authorize: "Authorized", value `******` |

Swagger UI then sent the token with every bearer operation and the key with the partner operation. The curl line in each execution screenshot shows the header, redacted.

## How the run was done

1. **API without a database** (lock icons, Authorize and the 401/403 executions, which are refused before any database access): `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`, trusted development certificate). The signing key, the partner key and an unreachable connection string were passed as environment variables, so no User Secrets were written.
2. **Database for the success cases:** a disposable database, `ApexPost_Test_`, was created on `.` with its files in `E:\SqlData\ApexPost\Tests`, the folder the test fixture uses. `dotnet ef database update --connection …` applied the five migrations (22 tables). The API was restarted against it with new random keys. No developer database, User Secrets or LocalDB setting was used or changed.
3. **`.http` run:** a script outside the repository parsed `ApexPost.Api.http` and sent every request in order with its variables resolved, as an `.http` client does. The tokens came from requests 0a and 0b, and the partner key from the `APEXPOST_PARTNER_API_KEY` environment variable. Before the database run, the 17 requests that never reach the database (43, 44, 46–49, 52, 53, 55–61) were also sent against the database-less API, with the same results.
4. **Swagger UI:** a script outside the repository drove Microsoft Edge (headless, through `puppeteer-core`) on `https://localhost:7280/swagger/index.html`. It used the Authorize dialog and, for each operation, Try it out and Execute.
5. **Cleanup:** the API was stopped, the database was dropped and its files were removed, and the random keys were discarded.

## Automated tests

[tests/ApexPost.Api.IntegrationTests/SecurityTests.cs](../../../tests/ApexPost.Api.IntegrationTests/SecurityTests.cs) holds 105 security tests, including `AuthorizationMatrix`: 8 operations × 2 versions × no token, Courier and Admin. The full run is recorded in the [README](../../../README.md).
