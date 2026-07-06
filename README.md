# SpazaHub

Offline-first Tuckshop/Spaza Shop management for South African township and rural retail. Blazor WebAssembly PWA client, ASP.NET Core API server, single shared multi-tenant SQL Server database. The full product specification lives in `spazahub-claude-code-prompt.md`.

## Framework note

The spec targets .NET 9 with a documented fallback to .NET 8 LTS. This build environment cannot reach the .NET 9 SDK feed, so the solution targets **.NET 8 LTS** (the sanctioned fallback). Directory.Build.props pins `net8.0` in one place; moving to `net9.0` later is a one-line change plus package bumps.

## Solution layout

```
SpazaHub.sln
├── src/
│   ├── SpazaHub.Domain            Entities, enums, domain events, no dependencies
│   ├── SpazaHub.Application       CQRS handlers, validators, ports (IMessagingProvider, IVasProvider, ITenantProvider)
│   ├── SpazaHub.Infrastructure    EF Core, tenancy plumbing, identity, JWT, dev SMS adapter
│   ├── SpazaHub.Api               ASP.NET Core minimal API, auth endpoints
│   ├── SpazaHub.Client            Blazor WASM PWA (POS UI arrives in Phase 3)
│   └── SpazaHub.Shared            DTOs, role and claim constants shared client/server
├── tests/
│   ├── SpazaHub.Domain.Tests
│   ├── SpazaHub.Application.Tests
│   └── SpazaHub.Sync.Tests        Sync protocol tests land here in Phase 2
```

## Build and test

```bash
dotnet build SpazaHub.sln
dotnet test SpazaHub.sln
```

## Phase 1: Foundation (complete)

What was built:

- **Solution scaffold** per the locked structure, with Clean Architecture dependency rules: Domain has no dependencies, Application depends only on Domain and Shared, Infrastructure and Api depend inward.
- **Domain entities** for every module: products, sales (immutable, with line-level cost snapshots), stock movements, customers, credit ledger entries, cash movements, cash-ups, cashback transactions, fee income, customer messages, VAS transactions, tenant wallet, cashiers, devices, and tenant config. Append-only event entities carry the `IAppendOnly` marker.
- **GUID v7 primary keys** (`GuidV7.NewGuid()`): client-generatable, time-ordered, RFC 9562 compliant. .NET 8 has no built-in generator, so a small implementation lives in Domain.
- **Multi-tenancy**: single shared database, `TenantId` on every tenant-owned table. One generic global query filter is applied in `OnModelCreating` to every entity implementing `ITenantOwned`; there is no per-entity filter duplication. A `SaveChanges` interceptor stamps `TenantId` from the JWT context on insert, overwrites anything the client supplied, and refuses to move rows between tenants on update.
- **Auth**: owner registration and login via phone + OTP (dev SMS provider logs the code; SMSFlow arrives in Phase 7). Verifying the OTP creates the tenant, default config, wallet, owner account, and a device registration, and returns an owner JWT plus a long-lived device refresh token. Cashiers are sub-users with a PBKDF2-hashed 4-digit PIN who log in for a shift against the device refresh token and receive a short-lived cashier JWT. Every JWT carries `tenant_id` and role claims; cashier tokens also carry `cashier_id`. Wrong PINs count toward a lockout window.
- **Domain money logic with tests**: cash rounding to a configurable increment with an explicit rounding adjustment, the cashback fee calculator (10 percent on top, three rounding modes), and weighted average cost maintenance.
- **CI**: GitHub Actions workflow restoring, building (warnings as errors), and running all test projects.

Test coverage right now: 64 passing tests, including tenant isolation (stamping, client spoof override, cross-tenant invisibility, no-context inserts failing loudly) and validator rules.

### Running the demo

The API runs on SQLite in Development so no SQL Server is needed locally (production uses SQL Server via the `InitialCreate` migration; `Database:Provider` selects the provider).

```bash
cd src/SpazaHub.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

Then walk the auth flow (Swagger UI is at `/swagger`):

1. `POST /api/auth/register-owner` with `{"phone":"0821234567","shopName":"Mama Thoko Spaza"}`. The OTP appears in the API console log (dev SMS provider).
2. `POST /api/auth/verify-otp` with the phone, the 6-digit code, and a device name. Returns the owner access token and the device refresh token.
3. `POST /api/cashiers` (owner bearer token) with `{"name":"Sipho","pin":"1234","canDoCashback":false}`.
4. `POST /api/auth/cashier-login` with the device refresh token, the cashier id, and the PIN. Returns a cashier access token.

### Secrets

No real connection strings or keys are committed. `appsettings.json` holds placeholders; use `dotnet user-secrets` locally and Azure Key Vault in production for `ConnectionStrings:SqlServer` and `Jwt:SigningKey`. The key in `appsettings.Development.json` is a dev-only constant for the SQLite demo.

## Phase 2: Sync engine (complete)

What was built:

- **Sync contracts** (`SpazaHub.Shared/Sync`): push and pull envelopes, plus `SyncJson`, the single JSON contract used on both sides. It generically strips three things from every payload: `TenantId` (the server stamps tenancy from the JWT, never from payloads), `CachedQuantity` (stock on hand is derived from movements and never transmitted as truth), and child entity collections (children sync as their own items).
- **Entity registry** (`SyncEntityRegistry`): the whitelist of syncable types, each marked append-only (dedupe on Id, immutable) or mutable last-write-wins (`IMutableSynced.UpdatedAtUtc` is the LWW timestamp). `TenantWallet` syncs down but can never be pushed (server-authoritative money).
- **Server change log**: every applied write to a registered entity, whether it came from a device push or a server-side handler, appends a `TenantChangeLogEntry` through one `SaveChanges` interceptor. The bigint identity Id is the per-tenant monotonic pull cursor.
- **Idempotent push** (`POST /api/sync/push`): ordered device batches applied in one transaction. Append-only replays dedupe on entity Id; mutable replays at the same timestamp are duplicates, not conflicts. Stale mutable edits lose LWW and land in `SyncConflictAudit` with the losing payload; winning edits audit the overwritten server values. Nothing is silently discarded. Pushing an Id that collides with another tenant's row is refused without confirming existence.
- **Cursor pull** (`GET /api/sync/pull`): pages of changes since the client cursor, resumable at any point, with echo suppression (a device never receives its own changes back).
- **Client plumbing** (`SpazaHub.Client`): local EF Core SQLite database sharing the server entity model, a `SyncOutbox` written in the same transaction as every local write, a `LocalStore` that applies pulled changes without touching the outbox (no echo loops) and recomputes `CachedQuantity` from local stock movements, and a `BackgroundSyncService` loop that drains the outbox in ordered chunks of 200 and pulls to the head of the change log, treating connectivity as an occasional bonus.
- **OPFS persistence**: the SQLite file lives in the in-memory emscripten file system and is copied to the Origin Private File System after commits, restored on boot (`wwwroot/js/opfs-db.js`). When OPFS is unavailable the app still runs, but local data does not survive a reload.

Test coverage: 85 passing tests. The sync suite covers duplicate batch replay (lost ack), out-of-order arrival within a batch, conflicting price edits from two devices, LWW winner and loser audits, cursor paging and crash-replay, echo suppression, tenant isolation of the change log, TenantId and CachedQuantity spoof attempts, the week-offline chunked backlog, and a full client-to-server-to-client round trip using the real `LocalStore` on both ends.

### Known risk: SQLite-WASM runtime and OPFS (flagged per spec)

Browser-side SQLite needs the `wasm-tools` workload (now installed in CI) and native relinking at publish. OPFS `createWritable` is solid on Chromium (the target Android fleet) but not on Safari. If real-device testing shows instability, the proposed fallback is IndexedDB persistence of the same database file bytes behind the existing `OpfsDbPersistence` seam, which is a one-class swap. No switch will be made without approval.

## Roadmap

1. Foundation (done)
2. Sync engine (done)
3. POS + Inventory
4. Cash-Up + Cashback
5. Makhulu Book (customer credit ledger)
6. Reporting polish
7. SMSFlow integration
8. VAS scaffolding
