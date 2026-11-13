# ADR 0029: API authentication, role authorization and partner keys

- Status: Accepted
- Date: 2026-11-09

## Context

Week 4 Part D (D1–D3, D5) requires `ApexPost.Api` to accept only authenticated couriers and administrators and approved partners:
- JWT bearer tokens, with the issuer, audience, lifetime and signing key validated and read from configuration.
- Development tokens from `dotnet user-jwts` or a Development-only `/dev/token`, carrying `courierId` and a Courier or Admin role.
- Role rules on scan upload, status PATCH, DELETE and GET, with 401 and 403 as Problem Details.
- A courier may upload scans only under their own `courierId`.
- A read-only partner route protected by an `X-Api-Key` header, checked by an endpoint filter or middleware.
- HTTPS redirection everywhere, HSTS outside Development, and the Bearer scheme in the v2 OpenAPI document.

The constraints are:
- No secrets in the repository.
- Thin controllers.
- No change to the Week 3 data layer.
- No change to the published v2 response shapes without recording it in VERSIONING.md.

## Decision

| Concern | Decision |
|---|---|
| Token validation | `AddJwtBearer()` binds `Authentication:Schemes:Bearer` (the section `dotnet user-jwts` writes); `JwtAuthentication` sets every validation flag explicitly, `ClockSkew` to one minute and `MapInboundClaims = false`, and `ValidateOnStart` refuses to start without an issuer, an audience and a signing key |
| Development tokens | Both: `dotnet user-jwts` for the standard tool, and `POST /dev/token` (Development only, excluded from OpenAPI) that signs with the same configured issuer, audience and key, so a client or `.http` file can sign in without a CLI |
| Role rules | `[Authorize]` / `[Authorize(Roles = ...)]` on every shipment and scan action, the same in v1 and v2. The assignment names the rules for scan upload and the status change (Courier), DELETE (Admin) and GET one shipment (any user). The project owner approved the rest: create and replace are Admin, and the scan history is any user. **Amended after the Week 4 review:** the list was first Admin-only; it now needs any signed-in user, because the brief opens GET to any authenticated user (VERSIONING.md, change 20) |
| Default | A fallback policy requires a signed-in user for every request without its own rule, including unmatched routes. The partner route, `/dev/token` and the Development documentation opt out with `AllowAnonymous` |
| Courier ownership | `RequireOwnScansAttribute`, an action filter on the upload action, refuses the whole batch with a 403 if any scan's `courierId` is not the token's |
| 401/403 bodies | The existing `UseStatusCodePages` + `AddProblemDetails` writes Problem Details for the scheme's empty 401 and 403 |
| Partner key | `[RequirePartnerApiKey]` endpoint metadata checked by `PartnerApiKeyMiddleware`: keys from the `PartnerApiKeys` configuration section, held as SHA-256 hashes, compared in constant time; 401 for a missing or wrong key, 500 when none is configured (the Week 2 pattern) |
| Partner data | A separate `PartnerShipmentResponse` without the recipient's name, phone or address |
| Transport | `UseHttpsRedirection()` always, `UseHsts()` outside Development; the http-only launch profile removed |
| OpenAPI | `SecuritySchemeTransformer` declares in each document the schemes its operations use (`Bearer` in v1 and v2, `PartnerApiKey` in v2) and lists on each operation the scheme it requires |
| `scanType` input | Accepted as the number or the name through `EnumNameInputConverter`; `ContractSchemaTransformer` keeps publishing it as the numeric enum the responses return |

## Alternatives considered

- **A custom `AuthenticationHandler` for the API key** (an `ApiKey` scheme with `[Authorize(AuthenticationSchemes = "ApiKey")]`). It fits ASP.NET Core's model well, but the assignment names an endpoint filter or middleware. It would also mix partner keys into the same challenge pipeline as bearer tokens. Rejected in favour of the Week 2 metadata-plus-middleware pattern.
- **An endpoint filter for the API key.** Endpoint filters run only for Minimal API endpoints, and the partner route is a versioned controller action like the rest of the API. Rejected.
- **Only `dotnet user-jwts`.** Simplest, but a client application cannot obtain a token without the CLI, and the `.http` file would need tokens pasted in by hand. Rejected; both are supported, sharing one key.
- **No fallback policy, only attributes.** Every current action has an attribute, so the effect today would be the same. But a future endpoint without `[Authorize]` would be open, and unmatched routes would tell anonymous callers which routes exist (404 versus 401). Rejected in favour of protecting by default.
- **Leaving version 1 open, or with fewer rules, because it is deprecated and frozen.** A deprecated version is still reachable, so any rule missing there could be bypassed through `/api/v1`. Rejected; security fixes are the one change a frozen version still takes.
- **Declaring the Bearer scheme in the v2 document only**, as the assignment words it. The v1 document would then describe its operations as open and Swagger UI could not authenticate v1 calls. Rejected; each document declares the schemes its own operations use.
- **Checking `courierId` inside `TrackedShipmentApplicationService`.** It would put a rule about the caller's identity into the Week 3-backed service, which knows nothing of tokens. Rejected; the filter keeps it in the API layer.
- **`JsonStringEnumConverter` on `scanType`.** It accepts names, but the generator then publishes the shared `ShipmentScanType` schema as a string enum. That would change the v2 response contract, where scan types are numbers. Rejected for a dedicated converter that the schema transformer recognises.

## Consequences

- Every token check is explicit and tested, and a missing setting stops the host instead of disabling validation.
- No secret is committed: the signing key and partner keys are in User Secrets, and tests generate their own per run.
- Every shipment and scan operation, in both versions, now requires a token, and declares 401 (and 403 where a role is required). Both published contracts changed; VERSIONING.md records the changes as breaking for anonymous clients.
- An anonymous request to a route that does not exist is a 401, not a 404; a signed-in caller still gets the 404.
- The v2 document gains the partner operation; both documents declare their security schemes. The NSwag client was regenerated, and the console consumer reads an optional access token from configuration (its demo needs both roles, because it creates a shipment and uploads scans).
- The integration tests sign every request with an all-roles test token by default, so the behaviour tests still run unchanged; `SecurityTests` covers each rule with narrower tokens or none.
- Moving to a real identity provider replaces the symmetric key and `/dev/token` with `Authority` metadata (SECURITY.md).
