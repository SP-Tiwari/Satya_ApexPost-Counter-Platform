# ADR 0030: Courier console client instead of a .NET MAUI app

- Status: Accepted
- Date: 2026-11-10

## Context

Week 4 Part D (D6–D11) asks for an `ApexPost.Courier` .NET MAUI app that:
- calls the v2 API through `IHttpClientFactory` with a typed `ApexApiClient`;
- attaches the bearer token with an `AuthHeaderHandler` reading MAUI `SecureStorage`;
- uses shared, source-generated `System.Text.Json` settings and Problem Details handling;
- adds Polly resilience;
- syncs the Part A offline scans when MAUI `Connectivity` reports the network is back;
- has Login, New Scan, Pending Scans and Sync Status screens.

Following the trainer's guidance, the project owner decided that the client is a .NET console application. No MAUI package, workload, page, `SecureStorage` or other MAUI API is used anywhere in the solution. Each MAUI-specific item therefore needs a console-compatible substitute, and every substitution must be documented rather than silently dropped.

## Decision

| Concern | Decision |
|---|---|
| Projects | `src/ApexPost.Courier.Sync`, a plain `net10.0` class library with everything a client needs to talk to the API: `ApexApiClient`, its DTOs and JSON context, `AuthHeaderHandler`, `ITokenStore`, `CourierSession` and `AddCourierSync`. `src/ApexPost.Courier.Console`, the console application and composition root. The existing `src/ApexPost.Courier.Offline` (Part A) is reused, not copied: the client DTOs use its `ScanType` |
| Typed client (D6) | `services.AddHttpClient<ApexApiClient>()` in `AddCourierSync`, with `AddHttpMessageHandler<AuthHeaderHandler>()`. The client never creates an `HttpClient` |
| Hand-written client, not the Part C SDK | `ApexApiClient` is hand-written with its own DTOs (`Contracts/ApiContracts.cs`) for the two calls the courier needs: `POST /dev/token` and `POST /api/v2/shipments/{trackingNumber}/scans`. The Part C NSwag client is unchanged. It builds its own reflection-based `JsonSerializerOptions` and sends `scanType` as a number, so it cannot meet D8, and changing it would mean editing the Part C project. The assignment also names the class `ApexApiClient` |
| JSON (D8) | `CourierJsonContext`, a `JsonSerializerContext` generated at compile time with `JsonSerializerDefaults.Web` (camelCase, case-insensitive) and `UseStringEnumConverter`. Enums are written as names and still read from numbers, because v2 responses send `scanType` as a number. Every client call passes the context's generated type information, and the context's own options refuse any other type (`CourierJsonTests`). Reflection-based serialization is **not** switched off for the whole console app (`JsonSerializerIsReflectionEnabledByDefault`). In a live run with it switched off, the app failed at startup, right after Couchbase Lite 4.1.2 started and before any menu; with only that setting turned back on, the same build ran normally. This points to an interaction between the global setting and a dependency, most likely Couchbase Lite, but the exact internal cause was not established, because no stack trace was captured. The client's own JSON still uses only the generated type information |
| Errors (D8) | An answer from the server is an `ApiResult<T>`. An error is an `ApiError` (kind, status, message, field errors) read from `ValidationProblemDetails` when the body is JSON. A 5xx always gets a fixed message. A missing, malformed or incomplete body is an `UnexpectedResponse`, never an exception. A call that does not reach the server throws, so it is never mistaken for an answer |
| Base URL (D6) | `ApexPostApi:BaseUrl` from configuration: `appsettings.json` (`https://localhost:7280/`), overridable by the `ApexPostApi__BaseUrl` environment variable or `--ApexPostApi:BaseUrl=` on the command line. It must be an absolute `https` URL |
| Separate base URLs for the Android emulator and Windows (D6), console substitution | D6 asks for one value for the Android emulator and one for Windows. The console client never runs in an Android emulator, so the emulator's address for the host machine (`10.0.2.2`) does not apply, and there is no Android client, project or dependency. Each environment the console runs in supplies its own API host through the same `ApexPostApi:BaseUrl` key, in `appsettings.json`, as an environment variable or on the command line. On Windows, the default is `https://localhost:7280/` |
| Token storage (`SecureStorage` substitute, D10) | `DpapiTokenStore`: the token and its expiry are encrypted with Windows DPAPI (`ProtectedData`, `DataProtectionScope.CurrentUser`, purpose-specific entropy) into `%LOCALAPPDATA%\ApexPost\Courier\access-token.bin`. Like `SecureStorage`, it keeps the token encrypted for the current user rather than in plain text. DPAPI `CurrentUser` does not isolate applications from each other: another process running as the same Windows user may be able to decrypt the file (see [SECURITY.md](../../SECURITY.md#courier-console-client-d6-d10)) |
| Unsupported platforms | DPAPI exists only on Windows. On any other OS, `AddCourierSync`'s token store throws `PlatformNotSupportedException` with a clear message. It never falls back to a plaintext file or to memory |
| 401 handling (D10) | `AuthHeaderHandler` reports a 401 to `CourierSession`, which clears the rejected token, in one step that keeps a newer token, and raises `SignInRequired`. The console then shows its Login menu, the equivalent of navigating to the Login page. No sync runs until the courier signs in again |
| Screens (D11): pages → console menus | `CourierConsole`. Signed out: Login (request a development token through `POST /dev/token`, or paste a token), Pending Scans, Sync Status, Exit. Signed in: New Scan, Pending Scans, Sync Status, Sync Now, Logout, Exit. Every screen shows the live pending count (Part A change listener) and, while the circuit is not closed, the offline banner. A pasted token is read without echo. `CourierSignIn` checks a token's `courierId`, Courier role and expiry (`CourierTokenReader`, no signature check: only the API can) before storing it |
| Resilience (D7) | `CourierResilience` adds a `Microsoft.Extensions.Http.Resilience` (Polly) pipeline to the typed client, exactly as a MAUI app would: overall timeout, retry (3, exponential with jitter, transient failures only, retry-safe requests only), circuit breaker and attempt timeout. No substitution was needed. The values and reasons are in [RESILIENCE.md](../../RESILIENCE.md) |
| Offline sync (D9) | `ApiShipmentUploader` replaces Part A's fake uploader. The Part A outbox uploads `Pending` scans in batches of 20. Only the server's final answer makes a scan `Synced` or `Failed`; a temporary failure leaves it `Pending` and stops the run. Rules and tests are in [OFFLINE-DESIGN.md](../../OFFLINE-DESIGN.md#4-synchronization-with-the-api-part-d-d9) |
| Connectivity (`Connectivity.ConnectivityChanged` substitute, D9) | `SystemNetworkStatus` (`INetworkStatus`) uses `NetworkChange.NetworkAvailabilityChanged` and `NetworkInterface.GetIsNetworkAvailable()`. `SyncCoordinator` syncs when the network has been back for 3 s, one run at a time. These see network adapters only, not whether the API answers; an API that is down is handled by the circuit breaker |

Every decision above is implemented; the evidence is listed in README.md, SECURITY.md, RESILIENCE.md and OFFLINE-DESIGN.md.

## Alternatives considered

- **A .NET MAUI app:** rejected by the project owner, following the trainer's guidance.
- **Reuse the Part C NSwag client:** not chosen, because of the reasons in the table above.
- **Token in memory only:** the most private option, but the courier would sign in at every start, and the assignment expects persistent secure storage.
- **A plaintext token file, or user secrets:** forbidden by D10 ("never in Preferences, files or logs").
- **Windows Credential Manager through P/Invoke:** gives the same protection as DPAPI, with more native code.

## Consequences

- The client is Windows-only because of DPAPI. Running it elsewhere needs another `ITokenStore`, such as the macOS Keychain or the Linux Secret Service. Changing the store affects no other class.
- The client DTOs duplicate two request and response shapes of the published contract. They are small, and the tests pin their JSON.
- Configuration is checked at startup: a missing, relative or `http` base URL fails with a message naming the key.
