# ApexPost API security (Week 4 Part D)

How `src/ApexPost.Api` authenticates couriers, administrators and partner systems, how it authorizes them, how keys and tokens are kept out of the repository, and why it accepts credentials only over HTTPS. All names, keys, tokens and URLs are fictional or local. The decision record is [ADR 0029](docs/adr/0029-api-authentication-and-partner-keys.md).

## Requirement traceability

| Requirement | Implementation | Verification |
|---|---|---|
| D1 JWT bearer authentication; issuer, audience, lifetime and signing key validated, read from configuration | `AddApexPostJwtAuthentication` in [src/ApexPost.Api/Security/JwtAuthentication.cs](src/ApexPost.Api/Security/JwtAuthentication.cs), called from `Program.cs` | `SecurityTests.Get_WithoutAToken_Returns401ProblemDetails_WithABearerChallenge`, `Get_WithAnInvalidToken_Returns401ProblemDetails` (7 cases), `Startup_WithoutASigningKeyForTheIssuer_Fails` |
| D1 Development tokens with `courierId` and a Courier or Admin role | `dotnet user-jwts`; `POST /dev/token` in [DevelopmentTokenEndpoint.cs](src/ApexPost.Api/Security/DevelopmentTokenEndpoint.cs), mapped in Development only | `DevelopmentToken_ForACourier_UploadsTheirOwnScans`, `DevelopmentToken_ForAnAdmin_DeletesAShipment`, `DevelopmentToken_WithAnInvalidRequest_Returns400` (4), `DevelopmentToken_OutsideDevelopment_DoesNotExist` |
| D2 Roles: scan upload and status PATCH Courier, DELETE Admin, GET any authenticated user, and the approved rules for the other operations, in v1 and v2 | `[Authorize]` attributes on every action of `ShipmentsController` (v1), `V2.ShipmentsController` and `ShipmentScansController`; a fallback policy requiring a signed-in user (`Program.cs`) | `AuthorizationMatrix` (48 cases: 8 operations × 2 versions × no token, Courier, Admin), `Get_WithAnyRole_Returns200` (2), `ChangeStatus_*`, `Delete_*`, `Upload_WithoutAToken_Returns401ProblemDetails` (v1, v2), `Upload_WithoutTheCourierRole_Returns403ProblemDetails`, `Fallback_AnonymousRequestToNoEndpoint_Returns401_AndASignedInOne404` (3) |
| D2 401 and 403 as Problem Details | `UseStatusCodePages` with `AddProblemDetails` writes the bearer scheme's empty 401 and 403; the ownership filter writes its own 403 | Every 401/403 test asserts `application/problem+json` with `status` and `title` (`Api.AssertProblemAsync`) |
| D2 A courier uploads only under their own `courierId` | [RequireOwnScansAttribute.cs](src/ApexPost.Api/Security/RequireOwnScansAttribute.cs) on `ShipmentScansController.Upload` | `Upload_WithAScanOfAnotherCourier_Returns403_AndStoresNoScan`, `Upload_WithACourierTokenWithoutACourierId_Returns403`, `Upload_AsTheCourierOfEveryScan_Returns200` (v1, v2), `Upload_AnInvalidBatch_FromAnotherCourier_IsStillA400` |
| D3 `GET /api/v2/partner/shipments/{trackingNumber}` with `X-Api-Key`, exempt from the bearer fallback | [PartnerShipmentsController.cs](src/ApexPost.Api/Controllers/V2/PartnerShipmentsController.cs) (`[AllowAnonymous]`), [PartnerApiKeyMiddleware.cs](src/ApexPost.Api/Security/PartnerApiKeyMiddleware.cs) | `Partner_WithAValidKeyAndNoToken_Returns200_InEveryEnvironment` (2), `Partner_WithAValidKey_Returns200_WithoutTheRecipientsPersonalDetails`, `Partner_WithAMissingOrInvalidKey_Returns401ProblemDetails` (3), `Partner_WithABearerTokenInsteadOfAKey_Returns401`, `PartnerKey_DoesNotReplaceTheBearerTokenOnOtherRoutes`, `Partner_ForAnUnknownTrackingNumber_Returns404`, `Partner_InVersion1_DoesNotExist`, `Partner_WithNoKeyConfigured_Returns500ProblemDetails` |
| D4 OAuth2 concepts | This file: [OAuth2 for ApexPost](#oauth2-for-apexpost-d4) | Documentation |
| D5 `UseHttpsRedirection()` everywhere, `UseHsts()` outside Development | `Program.cs` | `PlainHttp_IsRedirectedToHttps_InEveryEnvironment` (2), `Hsts_IsSentOutsideDevelopmentOnly` (2) |
| D5 Bearer scheme in the v2 OpenAPI document (also in v1, whose operations need it too); Swagger UI Authorize | [SecuritySchemeTransformer.cs](src/ApexPost.Api/OpenApi/SecuritySchemeTransformer.cs), registered for both documents | `OpenApiDocument_DeclaresTheSchemes_AndEachOperationRequiresOne` (v1, v2), `DevelopmentDocumentation_NeedsNoToken` (3), `PublishedContractTests` |

The tests are in [tests/ApexPost.Api.IntegrationTests/SecurityTests.cs](tests/ApexPost.Api.IntegrationTests/SecurityTests.cs) and run against the real API host (`WebApplicationFactory`) on a disposable SQL Server test database. The live run against the running API is in [docs/evidence/week4-part-d](docs/evidence/week4-part-d/README.md):
- all 63 `.http` requests with their responses,
- the Swagger UI lock icons and Authorize dialog for v1 and v2,
- protected v1 and v2 operations executed with Development tokens.

## Authentication: JWT bearer tokens (D1)

`AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(...)` registers the bearer scheme. `AddJwtBearer` binds the `Authentication:Schemes:Bearer` configuration section; `JwtAuthentication` then turns every check on explicitly:

| Setting | Value | Why |
|---|---|---|
| `ValidateIssuer` | `true`, against `ValidIssuer` | Only tokens from the configured issuer are accepted |
| `ValidateAudience` | `true`, against `ValidAudiences` | A token issued for another API is refused |
| `ValidateLifetime`, `RequireExpirationTime` | `true` | An expired or not-yet-valid token is refused, and a token without `exp` is never accepted |
| `ValidateIssuerSigningKey`, `RequireSignedTokens` | `true`, against `SigningKeys` | The signature must verify; an unsigned (`alg: none`) token is refused |
| `ClockSkew` | 1 minute | The default is 5 minutes. One minute tolerates small clock differences without extending a token's life much |
| `MapInboundClaims` | `false` | The claims keep their JWT names (`role`, `sub`, `courierId`). `RoleClaimType` is `role`, so `[Authorize(Roles = "...")]` reads it |

**Where the values come from:**

| Key (`Authentication:Schemes:Bearer:…`) | Development | Elsewhere |
|---|---|---|
| `ValidIssuer` | `dotnet-user-jwts`, in [appsettings.Development.json](src/ApexPost.Api/appsettings.Development.json) | The identity provider's issuer, from the host's configuration (environment variables or a secret store) |
| `ValidAudiences` | `https://localhost:7280`, in `appsettings.Development.json` | The API's registered audience |
| `SigningKeys` | User Secrets, written by `dotnet user-jwts` | Not used with a real identity provider; see [Moving to a real identity provider](#moving-to-a-real-identity-provider) |

Nothing secret is committed: `appsettings.Development.json` holds only the issuer name and the audience. **The host refuses to start** without an issuer, an audience and a signing key for that issuer (`ValidateOnStart`), so a missing setting can never quietly turn validation off.

### Development tokens

**With `dotnet user-jwts`** (the signing key is created in the API's User Secrets on first use):

```powershell
dotnet user-jwts create --project src/ApexPost.Api --name COURIER-DEMO-07 --role Courier --claim courierId=COURIER-DEMO-07 --valid-for 1h
dotnet user-jwts create --project src/ApexPost.Api --name demo.admin --role Admin --valid-for 1h
```

The token payload is `{ "sub": "COURIER-DEMO-07", "role": "Courier", "courierId": "COURIER-DEMO-07", "aud": [...], "iss": "dotnet-user-jwts", "nbf", "exp", "iat", "jti" }`. Without `--valid-for`, `user-jwts` issues tokens valid for three months, so the commands above set one hour. `dotnet user-jwts create` can also add the `Authentication:Schemes:Bearer` section to `appsettings.Development.json`. Review that file before committing: it must still hold only the issuer and audiences.

**With `POST /dev/token`** (Development only; the route does not exist in any other environment and is not in the OpenAPI documents):

```http
POST https://localhost:7280/dev/token
Content-Type: application/json

{ "courierId": "COURIER-DEMO-07", "role": "Courier" }
```

The response is `{ "accessToken": "…", "tokenType": "Bearer", "expiresAtUtc": "…" }`. The token is valid for one hour and is signed with the same issuer, audience and key the API validates with, so it is interchangeable with a `user-jwts` token. `role` must be `Courier` or `Admin`. A Courier token needs a `courierId` (1 to 50 characters, like a scan's `courierId`), otherwise the response is a 400 `ValidationProblemDetails`. It lets the courier console client and the `.http` file sign in without a separate tool. Anyone who can reach a Development API can mint an Admin token, which is acceptable only because Development runs on a developer's own machine.

## Authorization (D2)

The same rules apply to `/api/v1` (deprecated) and `/api/v2`, so the older version cannot be used to get round them:

| Operation | Route (`/api/v1/...` and `/api/v2/...`) | Rule | No, invalid or expired token | Valid token, wrong role |
|---|---|---|---|---|
| List shipments | `GET …/shipments` | `[Authorize(Roles = "Admin")]` | 401 | 403 |
| Get one shipment | `GET …/shipments/{trackingNumber}` | `[Authorize]`: any authenticated user | 401 | — |
| Create a shipment | `POST …/shipments` | `[Authorize(Roles = "Admin")]` | 401 | 403 |
| Replace details | `PUT …/shipments/{trackingNumber}` | `[Authorize(Roles = "Admin")]` | 401 | 403 |
| Change status | `PATCH …/shipments/{trackingNumber}/status` | `[Authorize(Roles = "Courier")]` | 401 | 403 |
| Delete a shipment | `DELETE …/shipments/{trackingNumber}` | `[Authorize(Roles = "Admin")]` | 401 | 403 |
| Upload scans | `POST …/shipments/{trackingNumber}/scans` | `[Authorize(Roles = "Courier")]` and `[RequireOwnScans]` | 401 | 403 |
| Scan history | `GET …/shipments/{trackingNumber}/scans` | `[Authorize]`: any authenticated user | 401 | — |
| Partner tracking (v2 only) | `GET /api/v2/partner/shipments/{trackingNumber}` | `[AllowAnonymous]` for bearer tokens; a valid `X-Api-Key` (see D3) | 401 without a valid key | — |
| Anything else | Any other request | Fallback policy: any authenticated user | 401 | — |

The roles follow who does the work. A courier in the field moves a shipment along and uploads its scans. Office administrators create, edit, list and delete shipments. Anyone signed in may read one shipment and its scans. A token with both roles may do both.

**Fallback policy:** `AddAuthorizationBuilder().SetFallbackPolicy(...)` requires an authenticated user for every endpoint without an authorization attribute of its own, and for every request that matches no endpoint at all. A future endpoint is therefore protected even if someone forgets `[Authorize]`. Three things opt out with `AllowAnonymous`:
- the partner route, which has its own key check,
- `POST /dev/token` (Development only),
- the Development OpenAPI documents and Swagger UI, so the Authorize button can be reached.

Because the fallback also covers unmatched requests, an anonymous request to a route that does not exist, such as `/api/v3/shipments` or a malformed tracking number, is a 401, while a signed-in caller gets the usual 404. An anonymous caller cannot probe which routes exist. A partner request with a malformed tracking number matches no route either, so it too is a 401 unless a bearer token is also sent.

**Ownership of scans:** `RequireOwnScansAttribute` is an action filter on the upload action. It compares the `courierId` of every scan in the batch with the token's `courierId` claim (ordinal, case-sensitive). If any scan differs, or the token has no `courierId`, the whole batch is refused with a 403 Problem Details and nothing is stored. It runs after `[ApiController]`'s automatic validation, so an invalid batch is still a 400. It is an authorization rule about the caller, so it lives in the API layer as a filter and the controller stays thin.

**401 and 403 responses:** the bearer scheme answers a missing, malformed, expired, wrongly signed or wrongly addressed token with an empty 401 and a `WWW-Authenticate: Bearer` header. It answers a valid token without the required role with an empty 403. `UseStatusCodePages`, with `AddProblemDetails`, turns both into Problem Details (`application/problem+json`) with `type`, `title`, `status` and `traceId`.

## Partner API keys (D3)

`GET /api/v2/partner/shipments/{trackingNumber}` is a read-only route for approved partner systems. It is in version 2 only.

- **How the key is checked:** the controller carries `[RequirePartnerApiKey]` endpoint metadata. `PartnerApiKeyMiddleware`, registered after authentication and before authorization, checks every endpoint with that metadata and ignores all others. It follows the Week 2 partner endpoint (`ApexPost.Web`'s `ApiKeyAndIpRestrictionMiddleware`).
- **Where keys come from:** the `PartnerApiKeys` configuration section, one entry per partner (`PartnerApiKeys:<PartnerName> = <key>`), from User Secrets in Development or the host's secret store elsewhere. No key is in the repository. Blank entries are ignored.

  ```powershell
  $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  dotnet user-secrets set "PartnerApiKeys:DemoPartner" ([Convert]::ToBase64String($bytes)) --project src/ApexPost.Api
  ```

- **How keys are compared:** the configured keys are held only as SHA-256 hashes. The presented `X-Api-Key` value is hashed and compared with every configured hash using `CryptographicOperations.FixedTimeEquals`, without stopping at the first match. So neither the comparison time nor the number of keys checked reveals how much of a key was right. Exactly one header value is accepted.
- **Responses:**
  - A missing, empty, repeated or wrong key is a 401 Problem Details: "A valid partner API key is required."
  - When no partner key is configured at all, the route returns a 500 Problem Details: "The partner API is not configured." That is a deployment fault, not the caller's.
  - Keys are never logged.
- **A key is not a bearer token, and a token is not a key:** a bearer token does not open the partner route, and a partner key does not open any bearer-protected route (both are tested).
- **What a partner sees:** `PartnerShipmentResponse` is `{ trackingNumber, status, destinationCity, latestScan }`, with `status` as its name. The recipient's name, phone number and address are never returned to a partner.

## OAuth2 for ApexPost (D4)

The development tokens above stand in for an OAuth2 authorization server. In production, ApexPost would use the two OAuth2 flows below. This section explains the concepts; it is not implemented.

### Roles in OAuth2

| Role | In ApexPost |
|---|---|
| **Identity provider / authorization server** | A service such as Microsoft Entra ID, Auth0 or Keycloak. It signs couriers and administrators in (with passwords, MFA or a corporate login), registers the client applications, and issues tokens. The API never sees a password |
| **Resource server** | `ApexPost.Api`. It only validates the tokens it receives: signature, issuer, audience, lifetime, then roles and claims |
| **Client** | The courier client application, a public client that cannot keep a secret, and each partner system, a confidential client that can |
| **Access token (JWT)** | A short-lived (for example 5–15 minutes), signed statement: who the caller is (`sub`), which API it is for (`aud`), who issued it (`iss`), when it expires (`exp`), and what it may do (roles or scopes, such as `role: Courier` and `courierId`). It is sent as `Authorization: Bearer …`. Whoever holds it can use it until it expires, so it is kept short-lived and sent only over TLS |
| **Refresh token** | A long-lived, opaque credential issued with the access token to the courier client only. The client exchanges it with the identity provider, never with the API, for a new access token when the old one expires. So a courier is not asked to sign in again every few minutes. The identity provider can revoke it, rotates it on every use and detects reuse. It must be stored as securely as a password |

### Authorization Code with PKCE: the courier client

A courier's client application is installed on a device, so it is a *public* client: any secret built into it can be extracted. The Authorization Code flow with PKCE (RFC 7636) protects it without a client secret:

1. The client creates a random `code_verifier` and its SHA-256 hash, the `code_challenge`.
2. It opens the identity provider's sign-in page in the system browser. The request carries `response_type=code`, its `client_id`, a `redirect_uri` (for a desktop or console client, a loopback address such as `http://127.0.0.1:<port>/callback`, RFC 8252), the scopes, a random `state`, and the `code_challenge` with `code_challenge_method=S256`.
3. The courier signs in at the identity provider, never inside the client.
4. The identity provider redirects to the `redirect_uri` with a one-time authorization `code` and the same `state`, which the client checks.
5. The client sends the `code` and the original `code_verifier` to the token endpoint. The identity provider hashes the verifier and compares it with the challenge from step 2. An attacker who intercepted the code has no verifier, so the code is useless to them.
6. The identity provider returns an access token, a refresh token and, with OpenID Connect, an ID token.

The client then calls the API with the access token and renews it with the refresh token.

### Client Credentials: partner systems

A partner's server-to-server integration has no user. It is a *confidential* client, registered at the identity provider with a `client_id` and a secret or, better, a certificate (private key JWT). It sends `grant_type=client_credentials` with its credentials and the API's scope, such as `api://apexpost/partner.read`, to the token endpoint, and receives a short-lived access token with no refresh token. When the token expires, it simply asks again. The API authorizes the partner by the scope in the token, not by a shared key.

### Why API keys are weaker than OAuth2 tokens

| API key (`X-Api-Key`) | OAuth2 access token |
|---|---|
| A static secret that lives until someone rotates it by hand; a leaked key works indefinitely | Expires in minutes; a leaked token is useful only briefly |
| Sent on every request, so every request is a chance to leak it | The long-lived credential (client secret or certificate) goes only to the identity provider; the API sees only short-lived tokens |
| Says only "this is partner X". Any scope or expiry has to be kept on the server | Carries its audience, scopes, expiry and issuer, and its signature proves them |
| Shared between the partner and ApexPost, so the API must store something that can be checked; ApexPost stores hashes | The API stores no partner secret; it verifies a signature with the identity provider's public keys |
| Revocation, rotation and auditing are all manual | Central: disable the client at the identity provider and no new tokens are issued |
| Cannot prove possession of a key; a copied value works anywhere | Can be bound to a certificate (mTLS) or a proof-of-possession key (DPoP) |

ApexPost still uses a key here because the assignment asks for one and the route is read-only, returns no personal data and is versioned separately. Keys are kept in configuration, compared in constant time, accepted only over HTTPS and never logged.

### Moving to a real identity provider

1. **Register the API** at the identity provider: its audience (such as `api://apexpost`), its scopes (`scans.write`, `shipments.read`, `partner.read`) and app roles (`Courier`, `Admin`). Each courier's `courierId` is issued as a claim from the courier directory.
2. **Replace the signing keys with the provider's metadata.** Configure `Authority` (or `MetadataAddress`) and `Audience` in `AddJwtBearer`. The handler then downloads the provider's OpenID Connect discovery document and public signing keys (JWKS), and follows key rotation by itself. `SigningKeys` in User Secrets, the `dotnet-user-jwts` issuer in `appsettings.Development.json` and the symmetric HS256 key all go; the provider signs with asymmetric keys (RS256 or ES256), so the API holds no signing secret at all.
3. **Delete `POST /dev/token`** and `DevelopmentTokenEndpoint.cs`. Couriers sign in with Authorization Code + PKCE and partners use Client Credentials.
4. **Map the provider's claims.** Set `RoleClaimType` to the provider's role claim (for Entra ID, `roles`) and, if needed, add a claims transformation. Add scope policies (`RequireClaim("scp", "scans.write")`) beside the roles.
5. **Retire the partner API keys** in favour of Client Credentials tokens with a `partner.read` scope, after a migration period announced to partners. Remove `PartnerApiKeyMiddleware`, `RequirePartnerApiKeyAttribute`, the `PartnerApiKeys` section and the `PartnerApiKey` OpenAPI scheme.
6. **Update the v2 document's security scheme** from `http`/`bearer` to `oauth2` (or `openIdConnect`) with the provider's authorization and token URLs. Swagger UI's Authorize button then runs the PKCE flow itself.

## Transport security: HTTPS and HSTS (D5)

- **`UseHttpsRedirection()`** runs in every environment. A plain `http://` request is answered with a 307 redirect to the same URL on HTTPS, and the request is not processed. The `https` launch profile listens on `https://localhost:7280` and, only so it can redirect, on `http://localhost:5280`. The former http-only launch profile was removed, so the API is never run on plain HTTP alone.
- **`UseHsts()`** runs outside Development. Every HTTPS response carries `Strict-Transport-Security: max-age=2592000` (30 days, the ASP.NET Core default), so a browser that has seen the API once uses only HTTPS for it afterwards. It is left out of Development because a browser would then force HTTPS for every site on `localhost`. The ASP.NET Core default also excludes `localhost`. The tests check the header on a non-local host name.
- **Why bearer tokens and API keys must never travel over plain HTTP:**
  - A bearer token is like cash: the API accepts whoever presents it, with no further proof.
  - An API key is a static secret.
  - On plain HTTP, the `Authorization` and `X-Api-Key` headers cross every network hop as readable text. Anyone on the same Wi-Fi, a compromised router or a proxy can copy them and replay them until they expire, or, for a key, until someone notices.
  - Plain HTTP also lets an attacker change requests and responses on the way.
  - TLS encrypts the headers and authenticates the server, so the client knows it is giving its token to the real API.
  - A redirect alone does not protect the first request, because a client that sends a token to `http://` has already exposed it. So the clients are configured with `https://` base URLs, and HSTS stops browsers from ever trying `http://`.
- **Certificates:** in Development the API uses the ASP.NET Core development certificate (`dotnet dev-certs https --trust`). The repository contains no code that disables certificate validation. If the Part D courier console client ever needs a development-only exception, it must be limited to `#if DEBUG` and `localhost` and be documented here.

### Swagger UI Authorize button

The v2 document declares two security schemes, and each operation lists the one it needs:
- `Bearer` (`type: http`, `scheme: bearer`, `bearerFormat: JWT`) on every shipment and scan operation.
- `PartnerApiKey` (`type: apiKey`, `in: header`, `name: X-Api-Key`) on the partner operation.

The assignment asks for the Bearer scheme in the v2 document. The v1 document declares it too, and only it, because every v1 operation now needs a token as well. Without it, the v1 document would describe its operations as open, and "Try it out" on v1 could never succeed. A document declares only the schemes its own operations use. In Swagger UI (`https://localhost:7280/swagger`, Development only), choose **v2** (or **v1 (deprecated)**) and select **Authorize**. Paste an access token from `dotnet user-jwts` or `/dev/token` under Bearer (without the word "Bearer"), or a partner key under PartnerApiKey. "Try it out" then sends the matching header. The document only describes the protection; hiding an operation from it would protect nothing.

## Secrets and configuration checklist

| Item | Where | In the repository? |
|---|---|---|
| Connection string | User Secrets `ConnectionStrings:ApexPostDb` | No |
| JWT signing key | User Secrets `Authentication:Schemes:Bearer:SigningKeys` (written by `dotnet user-jwts`) | No |
| JWT issuer and audience (Development) | `src/ApexPost.Api/appsettings.Development.json` | Yes, no secret |
| Partner API keys | User Secrets `PartnerApiKeys:<PartnerName>` | No |
| Test signing key and partner key | Generated with `RandomNumberGenerator` for each test run (`TestTokens` in `ApiTestHost.cs`) | No |
| `.http` file credentials | Tokens from `POST /dev/token` at run time; partner key from the `APEXPOST_PARTNER_API_KEY` environment variable | No |
| Console consumer token | `ApexPostApi:AccessToken`, for example the `ApexPostApi__AccessToken` environment variable | No |

Tokens, API keys and request bodies are never logged by the API's own code.
