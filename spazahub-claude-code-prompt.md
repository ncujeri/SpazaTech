# SpazaHub: Claude Code Build Prompt

You are acting as a senior .NET architect and developer. Build **SpazaHub**, an offline-first Tuckshop/Spaza Shop management ecosystem for South African township and rural retail. Work step by step, phase by phase, and confirm the plan for each phase before writing large amounts of code. Produce production-grade, modular, testable C#.

---

## 1. Non-Negotiable Context and Constraints

- Target users: spaza/tuckshop owners and cashiers in rural KZN and townships. Budget Android phones and tablets. Low tech literacy. Fast-paced counter transactions.
- Environment: high latency, frequent connection drops, load-shedding. The app must be **fully functional offline**. Connectivity is treated as an occasional bonus, not an assumption.
- A sale must complete in under ~3 seconds of cashier interaction with zero network dependency.
- All money-related data must be auditable, append-only where possible, and safe under sync conflicts.
- POPIA compliance: consent capture for customer personal info, opt-out handling for SMS.
- **Never** include real connection strings, passwords, or API keys in any file. Use user-secrets locally and Azure Key Vault placeholders for production. appsettings files must contain placeholder values only.
- **Never use em dashes in any generated content, comments, or documentation.** Use commas, colons, or full stops instead.

## 2. Tech Stack (Locked Decisions, Do Not Substitute)

- **Client:** Blazor WebAssembly PWA (NOT MAUI). Installable to Android home screen, offline via service worker. .NET 9 (fall back to .NET 8 LTS if any dependency requires it).
- **Client local DB:** EF Core + SQLite running in the browser (WASM), persisted to OPFS. If OPFS/SQLite-WASM proves unstable during implementation, flag it and propose the IndexedDB fallback before switching. Same entity model shared with server where practical.
- **Server:** ASP.NET Core Web API, Clean Architecture, CQRS with MediatR, FluentValidation, EF Core with SQL Server.
- **Auth:** ASP.NET Core Identity + JWT. Owner registration via phone number + OTP (SMS). Cashiers are sub-users with a local 4-digit PIN for shift login against a long-lived device refresh token. JWT carries `tenant_id` and role claims.
- **Barcode scanning:** JS interop wrapper over the native `BarcodeDetector` API with a ZXing-WASM fallback for older devices. Also support Bluetooth scanners as keyboard-wedge input.
- **Push:** Web Push for local/low-stock notifications on Android PWA.
- **SMS:** SMSFlow (the South African provider, smsflow.co.za, NOT the Australian smsflow.com.au) behind an `IMessagingProvider` port.

## 3. Solution Structure

```
SpazaHub.sln
├── src/
│   ├── SpazaHub.Domain            // Entities, enums, domain events, no dependencies
│   ├── SpazaHub.Application       // CQRS handlers, validators, ports (IMessagingProvider, IVasProvider, ITenantProvider), sync contracts
│   ├── SpazaHub.Infrastructure    // EF Core SQL Server, tenancy plumbing, SMSFlow adapter, VAS adapters, Key Vault config
│   ├── SpazaHub.Api               // ASP.NET Core, auth, sync endpoints, webhooks
│   ├── SpazaHub.Client            // Blazor WASM PWA: POS UI, local EF Core + SQLite, outbox, background sync service
│   └── SpazaHub.Shared            // DTOs, enums, sync envelopes shared client/server
├── tests/
│   ├── SpazaHub.Domain.Tests
│   ├── SpazaHub.Application.Tests
│   └── SpazaHub.Sync.Tests        // dedicated sync protocol tests, including conflict and replay cases
```

## 4. Multi-Tenancy (Locked Design)

- Single shared SQL Server database. Every tenant-owned table has `TenantId` (Guid).
- `ITenantProvider` resolves the tenant from the JWT `tenant_id` claim.
- One global query filter defined generically in `OnModelCreating` for every entity implementing `ITenantOwned`. No per-entity filter duplication.
- A `SaveChanges` interceptor stamps `TenantId` on inserts server-side. The client can NEVER set or spoof `TenantId`; the server ignores any incoming TenantId and stamps from the authenticated context.
- Multi-device per tenant is a day-one assumption (owner phone + counter tablet).

## 5. Keys and Sync Engine (Locked Design)

- **All primary keys are client-generated GUID v7** so records are created fully offline and index fragmentation stays sane.
- **Append-only event model** for all money-touching data: sales, sale payments, stock movements, credit ledger entries, cash movements, cashback transactions, fee income, messages, VAS transactions. These are immutable; corrections are new compensating events referencing the original.
- **Client outbox:** every local write also appends a `SyncOutbox` row (entity type, JSON payload, monotonic per-device sequence). A background service pushes ordered batches when online; server acks; acked rows are cleared. Ordering preserves referential integrity (sale before its payment lines).
- **Server push endpoint** must be idempotent: replaying a batch (lost ack) must not duplicate rows. Dedupe on entity Id.
- **Pull deltas:** server keeps a per-tenant change log with a monotonic cursor. Client pulls "everything since cursor X". Reference data (products, prices, customers, config) flows down this way between devices.
- **Conflicts:** only mutable reference data (product details, prices, thresholds, customer info, tenant config) can conflict. Resolve last-write-wins with a server-side audit log of overwritten values. Append-only data cannot conflict by construction.
- **Stock quantity is derived, never synced.** On-hand = SUM of StockMovement rows. A denormalized `CachedQuantity` on the product is recomputed locally for UI speed but is never transmitted as truth.
- Handle the "device offline for a week" case: large outbox batches, chunked upload, resumable cursor pull.

## 6. Modules and Business Rules

### 6.1 POS
- Quick-ring UI: large tap-target grid of top sellers (self-ordered by local sales velocity), barcode scan, and a "loose amount" button for unbarcoded goods.
- One sale supports a multi-tender breakdown: `SalePayment` child rows with `PaymentMethod` enum (Cash, Card, SassaCard, Qr, StoreCredit).
- Completed sales are immutable. Refunds/voids are compensating events referencing the original sale.
- Cash rounding: round cash tenders to 10c (owner-configurable), record the rounding difference explicitly so cash-up balances.
- Snapshot the weighted average cost price onto each sale line at sale time so historical margins never shift.

### 6.2 Inventory & Alerts
- `StockMovement` types: Sale, GoodsReceived, Adjustment, StockTakeCorrection, Wastage.
- Capture cost price on every GoodsReceived; maintain weighted average cost per product. Do NOT implement FIFO layers.
- Low-stock alerts fire **locally** on the device after each sale (threshold check, local push notification). Server-side daily digest is a supplement.
- Guided stock take mode: walk products ordered by value/velocity, capture counted vs expected, generate StockTakeCorrection movements, produce a variance (shrinkage) report.

### 6.3 Reporting & Cash-Up
- `CashUp` is a first-class entity: opening float, declared counted cash, expected cash, variance, sign-off by cashier and owner.
- Expected cash = opening float + cash sales + cashback fees taken in cash - cashback paid out - payouts/expenses.
- Trading day boundary is tenant-configurable (e.g. day rolls at 04:00), not the calendar day.
- Reports run locally against SQLite first; server-side equivalents exist for the owner viewing remotely or multi-store.
- FMCG analytics: units/day velocity, margin contribution, dead stock (no movement in X days), reorder suggestion list for cash-and-carry trips.
- Roles: Owner sees everything. Cashier can sell and view own cash-up but NEVER sees cost prices, margins, or reports.

### 6.4 Makhulu Book (Customer Credit Ledger)
- Ledger model, not a balance field: `CreditEntry` rows, debits (goods on credit, linked to sale) and credits (payments). Balance is derived. Append-only.
- Customer: name, nickname, optional phone, optional photo, consent flags (`ReminderConsent`). Phone must NOT be mandatory.
- Per-customer credit limit, soft-enforced with cashier override warning. Owner config controls whether cashiers may override.
- **SMS reminders, two tiers:**
  - Tier 1 (ship first): `sms:` deep link with prefilled body, sent from the owner's own phone. Zero cost, zero aggregator dependency.
  - Tier 2: server-side send via SMSFlow behind `IMessagingProvider`. Client queues a `SendReminder` intent in the outbox; server executes on sync; delivery receipts flow back via webhook and sync down.
- `CustomerMessage` entity: template, rendered body, channel, status (Queued/Submitted/Delivered/Failed), provider message id, cost. Pass `CustomerMessage.Id` as the client reference on every SMSFlow send so webhooks map back without lookups.
- Templates must fit one 160-char GSM-7 segment. English and isiZulu templates. Live segment-count preview in admin UI. Avoid characters that flip encoding to UCS-2.
- Inbound webhook handles reply-STOP opt-outs and flips `ReminderConsent` off.
- Automated sends are owner-configurable and default conservative (max one reminder per debtor per week).

### 6.5 Cashback (Till Cashback)
- Customer draws cash from the drawer against a card/SASSA payment, or standalone (no basket). Standalone cashback is its own transaction type, not a zero-line sale.
- **Fee: 10% of cashback amount, charged ON TOP** (customer receives R100, card is charged R110). Fee rounding: round UP to the nearest rand, but implement as a config enum (`NearestRand`, `UpToRand`, `Exact`). Fee rate is tenant config, default 0.10.
- Snapshot `FeeRateApplied` on every transaction so historical reports never shift if the rate changes.
- POS screen must show all three numbers before confirm: "Cash out: R100 | Fee: R10 | Card charge: R110".
- Event stream per cashback: `CashbackTransaction` + `CashMovement (CashbackPaid, negative)` + `FeeIncome (Cashback)`.
- Cashback fees appear on the daily report as service income, separate from goods margin.
- Controls (theft vector): per-transaction and per-day cashback limits (owner-set), cashback permission as a role flag, every event stamped with cashier id, owner-review flag for cashbacks above a threshold, and a drawer-floor check that blocks cashback if projected drawer cash falls below a configured minimum.
- The app records the transaction only. Card processing happens on the external Yoco/bank terminal; records reconcile against the provider statement.

### 6.6 VAS (Prepaid Electricity, Airtime/Data) - Scaffolding Only
- Online-only by definition. VAS buttons grey out with a "waiting for signal" state; never fail mid-transaction.
- `IVasProvider` port: `VendAirtime`, `VendElectricity`, `QueryProduct`, `QueryTransactionByIdempotencyKey`. Build a `FakeVasProvider` now; real aggregator (Flash/Blu Label/Kazang) later.
- **Idempotency is critical:** every vend carries a client-generated idempotency key. On lost response, the client re-queries by key, never re-vends.
- `VasTransaction` states: Pending, Confirmed, Failed. The till counts it only once Confirmed. Reconciliation against provider.
- Model a `TenantWallet` (float balance) concept, shared by VAS float and SMS credits.

## 7. Build Phases (Implement In This Order)

1. **Foundation:** solution scaffold, Domain entities, multi-tenant DbContext with generic global query filter + TenantId interceptor, GUID v7 generation, auth (phone+OTP owner, cashier PIN, roles), CI-friendly build.
2. **Sync engine:** client SQLite setup, outbox, background sync service, server push (idempotent) + pull (cursor) endpoints, LWW conflict handling with audit, sync protocol tests including replay and week-offline scenarios.
3. **POS + Inventory:** product catalog, quick-ring UI, barcode scanning, multi-tender sale flow, stock movements, cached quantity, low-stock local notifications.
4. **Cash-Up + Cashback:** cash movements, cashback flow with 10% fee, cash-up entity and screen, daily report.
5. **Makhulu Book:** customers, credit ledger, limits, `sms:` deep-link reminders.
6. **Reporting polish:** FMCG analytics, stock take mode, variance report, owner remote views.
7. **SMSFlow integration:** IMessagingProvider, SmsFlowProvider (typed HttpClient, Polly retry with exponential backoff, Key Vault key), delivery + opt-out webhooks, CustomerMessage lifecycle, per-tenant SMS cost accounting via TenantWallet.
8. **VAS scaffolding:** port, fake provider, VasTransaction lifecycle, idempotent vend flow, wallet.

Each phase must end with: passing unit tests, a short README section describing what was built, and a runnable demo state.

## 8. Coding Standards and Quality Bar

- Clean Architecture dependency rule strictly enforced: Domain has no dependencies; Application depends only on Domain; Infrastructure and Api depend inward.
- CQRS: one MediatR request + handler + FluentValidation validator per use case. No fat controllers; thin endpoints dispatching to MediatR.
- Nullable reference types enabled, warnings as errors.
- All money as `decimal`. Never `double` for currency.
- Unit tests for all domain logic and every sync edge case (duplicate batch, out-of-order arrival, cursor replay, conflicting price edits).
- xml-doc or concise comments on public APIs; no noise comments.
- UI: large touch targets (min 48dp), high contrast, minimal text, works one-handed on a 5-inch budget Android. Cashier flows must be operable without reading English fluently.
- Keep every generated file free of secrets and free of em dashes.

## 9. How To Work

- Start by generating the solution scaffold and Phase 1 plan. Present the plan, then implement.
- When a technical risk materialises (e.g. SQLite-WASM/OPFS instability, BarcodeDetector unavailability), stop, explain the tradeoff, propose the fallback, and wait for approval before switching.
- Ask before adding any NuGet package not implied by this spec.
- Prefer boring, proven patterns over clever ones. This system handles other people's money in a low-connectivity environment; predictability beats elegance.
