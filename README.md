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

## Roadmap

1. Foundation (done)
2. Sync engine: client SQLite, outbox, idempotent push, cursor pull, LWW conflict audit
3. POS + Inventory
4. Cash-Up + Cashback
5. Makhulu Book (customer credit ledger)
6. Reporting polish
7. SMSFlow integration
8. VAS scaffolding
