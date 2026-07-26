# SpazaHub

Offline-first Tuckshop/Spaza Shop management for South African township and rural retail. Blazor WebAssembly PWA client, ASP.NET Core API server, single shared multi-tenant SQL Server database. The full product specification lives in `spazahub-claude-code-prompt.md`.

## Framework note

The spec targets .NET 9 with a documented fallback to .NET 8 LTS. This build environment cannot reach the .NET 9 SDK feed, so the solution targets **.NET 8 LTS** (the sanctioned fallback). Directory.Build.props pins `net8.0` in one place; moving to `net9.0` later is a one-line change plus package bumps.

## Solution layout

```
SpazaHub.sln
├── src/
│   ├── SpazaHub.Shared            Domain entities, enums, pure domain services, sync contracts, DTOs
│   ├── SpazaHub.Api               ASP.NET Core server: CQRS, EF Core, tenancy, identity, JWT, sync engine
│   └── SpazaHub.Client            Blazor WebAssembly PWA: POS, local SQLite, outbox, background sync
└── tests/
    └── SpazaHub.Tests             One test project covering domain logic, tenancy, sync protocol, and client flows
```

Clean Architecture boundaries live on as folders inside SpazaHub.Api (Application/, Infrastructure/) rather than separate assemblies.

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

#### Seeding demo data

To skip the manual walk-through, seed one fully-populated shop (20 products with stock, 2 cashiers, 5 Makhulu Book customers, and a fortnight of sales). Idempotent — safe to re-run.

```bash
# SQLite dev database (spazahub-dev.db)
dotnet run --project src/SpazaHub.Api -- seed

# SQL Server (Spazahub, from appsettings.json)
ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/SpazaHub.Api -- seed
```

On Windows, `.\seed-db.ps1` (add `-SqlServer` for the SQL Server target) wraps the same commands, or use the `seed` / `seed-sqlserver` launch profiles.

Demo owner: phone `+27821234567`, owner PIN `4321`. Cashiers: Nomsa (PIN `1111`), Sipho (PIN `2222`). Sign in via the OTP flow below (the OTP prints to the API console).

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

## Phase 3: POS + Inventory (complete)

What was built:

- **SaleBuilder** (Domain, pure logic): assembles the immutable sale graph from cart lines and tenders. Cash is never entered as a tender; it is always the rounded remainder, with the rounding difference recorded explicitly on the sale so cash-up balances. Unit costs are snapshotted onto every line at sale time. Voids are compensating sales referencing the original, and the cash refund mirrors the rounded amount the customer actually paid, not the raw total.
- **PosService**: completes a sale entirely locally in one pass: sale, lines, payments, negative stock movements, and the drawer cash movement all written through the outbox in parent-first order, cached quantities recomputed, low-stock thresholds checked. Zero network on the sale path.
- **InventoryService**: product catalog CRUD, goods received (captures cost per movement and moves the weighted average), adjustments, and wastage as distinct movement types.
- **Quick ring**: the POS grid self-orders by local sales velocity (units sold over the trailing 14 days, computed from this device's sale lines).
- **Barcode scanning**: native `BarcodeDetector` where available, vendored ZXing UMD fallback for older devices (`wwwroot/lib/zxing`), and Bluetooth keyboard-wedge scanners handled as fast Enter-terminated keystroke bursts.
- **Low-stock alerts**: fire locally after each sale via the browser Notification API, with an inline banner fallback when permission is denied.
- **UI**: setup page (phone + OTP login against the API, or a fully offline demo mode), POS screen and product management built for budget Android: 48dp+ touch targets, high contrast, minimal text, one-handed layout.

Verified in a real browser (headless Chromium against the published PWA): boot, offline demo setup, product creation, goods received, quick-ring cash sale, and stock decrement, with EF Core SQLite running inside WebAssembly and no console errors. 102 passing tests overall; POS flow tests assert the outbox ordering the sync engine depends on (sale rows always precede their children).

## Phase 4: Cash-Up + Cashback (complete)

What was built:

- **CashbackBuilder** (Domain, pure): produces the full append-only event stream per cashback: the `CashbackTransaction` with `FeeRateApplied` snapshotted, the negative `CashbackPaid` drawer movement, and the `FeeIncome` row. The fee is charged on top (customer receives R100, card charged R110), and every control from the spec is enforced before anything is written: per-transaction limit, per-day limit, drawer cash floor, cashier permission flag, and the owner-review flag at the configured threshold. Refusals come back as typed reasons the UI translates into plain language.
- **TradingDay** (Domain): the trading day rolls at the tenant-configured hour of shop local time (default 04:00 SAST), so a 02:00 sale belongs to the previous day. All window queries share this one implementation.
- **CashUpCalculator + CashUpService**: the drawer lifecycle. Opening the day records the float as both a cash movement and the `CashUp` row; payouts, expenses, and bank drops are explicit movements; expected cash is always the sum of the day's movement stream (the spec formula falls out of the signs); variance is counted minus expected, with cashier sign-off and owner countersign.
- **DailyReportCalculator + ReportService**: goods revenue and margin from the cost snapshots taken at sale time, cashback fees on their own service-income line (never inside goods margin), tender breakdown, payouts, and expected cash. Voided sales net to zero and are not counted.
- **UI**: a cashback screen that shows all three numbers before confirm ("Cash out | Fee | Charge card") with friendly blocked-state reasons, a cash-up screen with live drawer cash and the day's movement list, and a daily report page. All local-first; the card itself is charged on the external terminal per the spec.

Verified in headless Chromium against the published PWA: open drawer with R500 float, R100 cashback quoted 100/10/110 and completed, drawer live-drops to R400, cash-up balances at zero variance, and the report shows the R10 fee as service income. 127 passing tests.

## Expiry tracking (added after Phase 4)

Best-before dates are captured per goods-received batch on the stock movement, not as a single field on the product, because different deliveries expire on different dates. `ExpiryEvaluator` (Domain, pure) estimates what is still on the shelf: shops rotate stock oldest-first, so the on-hand quantity is allocated to the newest batches, and a fully sold old batch never warns. This allocation is a warning heuristic only; costing stays weighted average. Batches at or past their date, or within the owner-configurable warning window (`TenantConfig.ExpiryWarningDays`, default 7), surface as a banner on the Products page and a local notification on app open. The batch expiry rides the normal sync payload so every device warns.

## Makhulu Book, stock take, and the trip list

The features that map to how township shops actually run:

- **Makhulu Book (customer credit ledger)**: informal credit is the backbone of spaza trade. Customers have a name, an optional nickname and phone, a soft book limit, and a POPIA consent flag with a capture timestamp. The book is a ledger, never a balance field: what someone owes is always debits minus credits, so two devices can write offline and agree after sync. Payments received in cash flow into the drawer stream so cash-up stays honest.
- **On the book at the till**: the tender screen has an ON THE BOOK option that completes the sale as StoreCredit and writes a linked debit. Over-limit warns once in plain language; the second tap allows it, because the limit is the owner's judgement call, not the app's.
- **SMS reminders, tier 1**: a Send reminder button builds an `sms:` deep link that opens the owner's own SMS app with a friendly English or isiZulu message prefilled, always under one GSM-7 segment, only for consented customers with a phone and a balance. Zero cost, no aggregator. Every reminder is recorded as a CustomerMessage.
- **Stock take with a shrinkage price tag**: walk the shelves biggest-money first, type what you count, and the app writes StockTakeCorrection movements and values the variance at cost. Missing stock finally has a rand number.
- **Cash-and-carry trip list**: the daily report now ends with what to buy (velocity-based top-up suggestions) and what is not moving (no sale in 30 days), which is the owner's weekly buying decision made for them.
- **Sync status chip**: the brand bar always shows whether everything reached the server ("Synced"), how much is still local ("48 to sync"), or that the shop is running purely on this phone.

## Production hardening round one

- **Sessions survive reloads.** The device refresh token, tenant, device, and shop name persist in localStorage; on boot the app restores them and exchanges the refresh token for a fresh access token when there is signal. The sync loop self-heals when the hourly access token expires, and a server-side revocation cleanly pauses sync while the shop keeps trading locally.
- **Cashier shift login, fully offline.** The shift screen lists cashiers as big buttons with a PIN keypad; PINs verify locally against the synced PBKDF2 hash, so shift changes need zero signal. Owners add cashiers on the device. Switching back to owner mode requires the owner PIN (stored hashed on tenant config, synced LWW). Five wrong PINs back off for two minutes.
- **Roles are enforced on-screen now.** Cashiers never see the Report tab (or the page, even by URL), the trip list, cost prices on receiving, or the stock take. Sales, book debits, cashbacks, and cash-ups are stamped with the cashier who did them, and the cashback permission flag is enforced through the same path the till uses.
- **Real SMS delivery.** SMSFlow (smsflow.co.za) adapter behind IMessagingProvider: typed HttpClient with Polly exponential backoff, selected by Messaging:Provider config (the logging dev provider remains the default until an API key is configured). Delivery receipts and inbound STOP replies arrive on secret-protected webhooks; STOP withdraws POPIA consent everywhere that phone number appears, and every status change flows back to devices through the normal change log.
- **OTP endpoint rate limiting**: five requests per ten minutes per address, because every OTP costs real SMS money.
- **Critical persistence bug fixed.** Microsoft.Data.Sqlite defaults new databases to WAL journal mode, so committed rows lived in a side file the OPFS copy never included: local data silently vanished on every reload. The store now forces the rollback journal at schema creation (and copies the WAL file defensively). Verified in the browser: the full database (172KB) survives reload, where before only an empty 4KB page came back.

## Counter polish

- **Search everywhere it matters**: the POS has a search box that filters by name or barcode prefix (the quick ring stays for the top sellers; search covers the other 200 lines), and the Stock page filters the same way.
- **Change calculator**: the tender screen takes "cash given" and shows the change in big green numerals before the sale is completed, then repeats it in the confirmation flash. Shortfalls show in red.
- **Two-tap confirm plus undo on the dangerous taps**: wastage and drawer money-out both ask "Tap to confirm" with the amount in the button, and after saving show an UNDO banner. Undo writes a compensating movement, never a deleted row, so the audit trail stays honest.
- **Update prompt**: when a new build is deployed, a yellow "New version ready" bar appears; one tap activates the new service worker and reloads. The registration uses updateViaCache none, without which browsers serve the cached asset manifest during update checks and shops would genuinely run stale builds for weeks.

## Roadmap

1. Foundation (done)
2. Sync engine (done)
3. POS + Inventory (done)
4. Cash-Up + Cashback (done)
5. Makhulu Book (customer credit ledger)
6. Reporting polish
7. SMSFlow integration
8. VAS scaffolding
