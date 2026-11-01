# ADR 0010: Demo logins from a user-secrets password

- Status: Accepted
- Date: 2026-10-29
- Supersedes: the "never include credentials" clause of [ADR 0003](0003-opt-in-demo-data.md)

## Context

The demo seed created staff records but no credentials, and no password was documented, so nobody could sign in to a fresh demo. The assignment requires documented demo logins per role, a README that explains user-secrets, and no secrets in source control. Separately, the hand-written JSON files under `src/ApexPost.Web/App_Data/SeedData` were never loaded and disagreed with the C# seed (for example 25 inventory rows against 150), and their sample hashes matched no known password.

## Decision

- `DemoDataInitializer` is the only seed source. The `App_Data/SeedData` files are retired: nothing loads them, they are not maintained, and they remain in the tree only until the author removes them.
- Demo logins are provisioned when `DemoData:Initialize` is true and `DemoData:StaffPassword` is set. `DemoCredentialProvisioner` hashes that password with PBKDF2-SHA256, using a random salt per staff member and the minimum supported iteration count. It adds a credential only for active seeded demo staff who have none yet, and writes them atomically to the existing separate credential file (ADR 0007). The application snapshot never contains credentials.
- The password lives in user-secrets (the Web project now has a `UserSecretsId`). The `DemoData__StaffPassword` environment variable takes precedence over it, because the application reads environment variables first. `run-demo.ps1` then says the password came from that variable, and neither shows it nor changes user-secrets. Startup rejects `DemoData:StaffPassword` outside the Development environment, and rejects a blank value.
- `scripts/run-demo.ps1` is the one-command setup. It generates and stores a random password if none exists, prints the logins, and runs the `demo` launch profile. That profile seeds the SQL Server database, and credentials and audit entries are stored there too.

## Alternatives considered

- **A fixed demo password documented in the README.** This is the simplest option, but it places a working password in source control and contradicts "no secrets in source control".
- **Committing a pre-hashed credential file.** No secret is stored, but the password would still have to be published to be useful, and anyone could reuse it against a demo that is accidentally exposed.
- **Loading the JSON seed files instead of the C# seed.** This would have kept a second, unvalidated copy of the data: the files bypass the domain services that deduct stock and date sales, and have no shipping rates.

## Consequences

Each developer's demo password is local and random unless they choose one. Credentials are never overwritten, so changing the password needs a new empty database. `-Reset` deletes all data, so it only runs on a disposable `ApexPost_Test_*` database named with `-ConnectionString`. Without the password the app still seeds data and warns that demo staff cannot sign in. Business values that the assignment leaves unspecified (tax rates, payment methods, parcel limits, money-order threshold and fee, low-stock threshold, partner key and limits) are still not seeded. The `demo` profile supplies only the zero rate and the `DEMO Cash` method that a goods sale needs, and the other workflows stay disabled until they are configured.
