# Codex Production-Readiness Audit

Audit date: 2026-06-23

## Remediation status (2026-06-26)

This app is internal to a single trusted department. User authentication, per-user
authorization, and audit-trail logging are intentionally **out of scope** for now,
so the auth-related findings below (#2, #3, #7) are accepted risks, not bugs.

Fixed:

- **#4 Vulnerable SQLite dependency** — updated EF Core Sqlite and
  Hosting.WindowsServices to 10.0.9, and pinned `SQLitePCLRaw.bundle_e_sqlite3`
  to 3.0.0 to evict the vulnerable 2.1.11 native lib. `dotnet list package
  --vulnerable --include-transitive` now reports none.
- **#6 Exception-message leak** — the monthly-import catch block already returns a
  generic message; confirmed no raw `exception.Message` reaches clients.
- **#1 Kestrel binding** — production config trimmed to a single `0.0.0.0:5000`
  endpoint (dropped the redundant port-80 binding). Kept `0.0.0.0` deliberately:
  the app serves the department directly over LAN with no reverse proxy, so
  loopback-only would make it unreachable.
- **#5 / #11 Failing tests** — all 7 PowerShell integration scripts are green.
  Root causes were stale assertions + a file-encoding bug, not app defects:
  Lithuanian-literal test files lacked a UTF-8 BOM (PowerShell 5.1 read them as
  Windows-1252, mangling status strings); status assertions still expected the
  old 3-state "near/at/over limit" model instead of the current 2-state one;
  the project-list assertions predated the active/inactive split; and the
  schema-1.4 SMD test used malformed object numbers and expected SMD rows in the
  subcontractor total instead of the separate client-value channel.
- **#10 Dangerous cleanup script** — moved to `ops/Clean-MoneyFlowDB.ps1` and
  gated behind a typed `DELETE` confirmation prompt.

Deferred (larger refactors, low real impact at this department's data scale):

- **#9 In-memory project reads + composite indexes** — `GetRowsForProjectScopeAsync`
  loads all rows then filters in C# (the scope logic doesn't translate cleanly to
  SQL). Composite indexes give no benefit until that filter is pushed into SQL, so
  both were left as-is. Revisit only if datasets grow large enough to feel slow.
- **#8 Ad-hoc startup schema patching** — works for MVP; move to EF migrations later.
- Splitting `Program.cs` route groups and `MonthlyFlowStore.cs` responsibilities.



## 1. Executive Summary

This is a small ASP.NET Core minimal API that serves a static HTML/JavaScript UI from `wwwroot`, persists to SQLite through EF Core, and imports monthly/contract JSON from Power Automate/PAD. The core money-flow import behavior is better than expected for a vibe-coded app: monthly imports use deterministic content hashes, unique row keys, decimal money values, a process-local import lock, and integration tests for duplicate imports, added rows, edited rows, contracts, SMD rows, and manual links.

The app is not ready to expose broadly on a LAN. The highest risks are operational/security hardening: production config binds Kestrel to `0.0.0.0` on ports 80 and 5000, write protection is optional and only covers `/api/imports/*`, browser-driven manual mutation endpoints are unauthenticated, there is no user identity/audit trail, and dependency scanning reports a high-severity vulnerable transitive SQLite package. The repo also includes an administrator script that intentionally wipes the live ProgramData database after backup.

Current risk rating: **High** before LAN exposure. It can become **Medium** for a small trusted pilot after binding Kestrel to loopback, requiring a real import API key, restricting mutation endpoints, updating packages, and fixing the test harness.

## 2. Project Map

- Backend entrypoint: `Program.cs`
- Backend framework: ASP.NET Core minimal API, `net10.0`
- Persistence: EF Core SQLite via `Persistence/MoneyFlowDbContext.cs`
- Business/import services: `Services/MonthlyFlowImportService.cs`, `Services/MonthlyFlowStore.cs`, `Services/MonthlyFlowContentHasher.cs`
- DTOs: `Dtos/*.cs`
- Models: `Models/*.cs`
- Frontend: static HTML/CSS/JS in `wwwroot/index.html`, `wwwroot/project.html`, `wwwroot/app.js`, `wwwroot/project.js`, `wwwroot/nav.js`, `wwwroot/styles.css`
- Tests: PowerShell integration scripts in `tests/*.ps1`
- Deployment/scripts: `Deploy-MoneyFlow.ps1`, `Start-MoneyFlow.ps1`, `Start-MoneyFlow.bat`, `Clean-MoneyFlowDB.ps1`, `docs/deployment.md`
- Seed/dev data: `seed/monthly-money-flow.db`, `data/`, `test-data/`

## 3. Runtime, Config, and Commands

Backend packages are declared in `PADS.MoneyFlow.Api.csproj:14` and `PADS.MoneyFlow.Api.csproj:15`.

Config/env:

- `MoneyFlow:DatabasePath` or `MONEY_FLOW_DB_PATH` controls SQLite path (`Program.cs:35`).
- `MoneyFlow:ApiKey`, `MoneyFlow__ApiKey`, or `MONEY_FLOW_API_KEY` controls import API key (`Program.cs:54`).
- `ASPNETCORE_ENVIRONMENT=Production` loads `appsettings.Production.json`.
- Production Kestrel endpoints are configured in `appsettings.Production.json:10`.

Build/test commands run during audit:

- `dotnet restore .\PADS.MoneyFlow.Api.csproj`: passed.
- `dotnet build .\PADS.MoneyFlow.Api.csproj --no-restore`: passed, 0 warnings.
- Initial test run before safe fixes: import-oriented PowerShell tests failed with 401 because `MONEY_FLOW_API_KEY` was present and test requests did not send `X-Api-Key`.
- Post-fix test run: `manual-contract-links.ps1`, `subcontractor-name-matching.ps1`, `contract-snapshot.ps1`, and `monthly-flow-real-april.ps1` passed.
- Post-fix test run: `monthly-flow-import.ps1`, `contract-import.ps1`, and `monthly-flow-ui.ps1` still failed on business/assertion mismatches described in Testing Gaps.
- `dotnet list .\PADS.MoneyFlow.Api.csproj package --vulnerable --include-transitive`: high-severity transitive `SQLitePCLRaw.lib.e_sqlite3 2.1.11`.
- `dotnet list .\PADS.MoneyFlow.Api.csproj package --outdated`: EF Core SQLite and WindowsServices 10.0.0 have 10.0.9 available.

The test failures occurred because this shell had `MONEY_FLOW_API_KEY` set and tests do not send `X-Api-Key` (`tests/monthly-flow-import.ps1:155`, `tests/contract-import.ps1:278`, `tests/manual-contract-links.ps1:56`).

## 4. Data Flow

1. PAD posts monthly JSON to `POST /api/imports/monthly-flow` (`Program.cs:112`) as raw JSON or multipart file. `sourceFileName` comes from query, header, or uploaded file name (`Program.cs:123`).
2. `MonthlyFlowImportService.ImportAsync` validates source name, JSON object shape, schema version, year, month, `rows`, row fields, decimals, and warnings.
3. Imported monthly rows become `MonthlyMoneyFlowRow` entities with deterministic `RowKey` based on year, month, sheet, source row, project/object, and cleaned subcontractor/customer identity.
4. `MonthlyFlowContentHasher.Compute` hashes canonicalized year/month/rows/warnings for exact duplicate-batch detection.
5. `MonthlyFlowStore.SaveImportAsync` serializes imports with `_lock`, checks duplicate content hashes, collapses same-key rows, inserts/updates/skips rows, saves warnings, and updates display fields (`Services/MonthlyFlowStore.cs:89`).
6. Contract imports post to `POST /api/imports/contracts` (`Program.cs:166`) and `MonthlyFlowStore.ImportContractsAsync` validates/upserts contracts/project values inside an explicit SQLite transaction (`Services/MonthlyFlowStore.cs:210`).
7. UI loads `/api/projects` (`Program.cs:235`) and project detail from `/api/projects/{projectCode}/monthly-flow` (`Program.cs:306`), then renders DOM nodes mostly with `textContent`.
8. Browser UI can create/delete manual contract links and object assignments through project mutation endpoints (`Program.cs:403`, `Program.cs:434`, `Program.cs:444`, `Program.cs:475`).

## 5. Top 10 Issues

1. **[High] Production config exposes Kestrel directly on all interfaces.** `appsettings.Production.json:13` binds HTTP to `0.0.0.0:80` and `appsettings.Production.json:16` binds `0.0.0.0:5000`, conflicting with `docs/deployment.md` guidance that Kestrel should only listen on loopback. LAN users can bypass a reverse proxy and any proxy-layer TLS/access controls.

2. **[High] Authentication/authorization is incomplete and optional.** Import API key enforcement is disabled when no key is configured (`Program.cs:59`) and only applies to `POST /api/imports/*` (`Program.cs:91`). Read endpoints and browser-driven mutation endpoints remain open.

3. **[High] Manual data mutation endpoints are unauthenticated.** Any LAN client can call manual link/object-assignment POST/DELETE routes (`Program.cs:403`, `Program.cs:434`, `Program.cs:444`, `Program.cs:475`). These do not wipe the DB, but they can alter money-flow matching and totals.

4. **[High] High-severity vulnerable SQLite native dependency.** `dotnet list package --vulnerable --include-transitive` reports `SQLitePCLRaw.lib.e_sqlite3 2.1.11` with GHSA-2m69-gcr7-jv3q. Top-level packages are also outdated (`PADS.MoneyFlow.Api.csproj:14`, `PADS.MoneyFlow.Api.csproj:15`).

5. **[Medium] The test suite is not green.** The initial blocker was API-key handling in the scripts; after fixing that harness issue, three scripts still fail on behavioral/assertion mismatches: SMD/client rows versus subcontractor totals, localized status string comparison, and project-list parent-code expectations.

6. **[Medium] Monthly import exception response leaks exception messages.** The catch block logs the exception, then returns `exception.Message` to clients (`Program.cs:155`). This can disclose schema, DB, or stack-adjacent internals.

7. **[Medium] No real audit trail for who changed manual links/assignments.** Models store timestamps, but there is no authenticated user identity. If a project is manually linked wrong, the app cannot attribute the change.

8. **[Medium] Startup schema creation/migration is ad hoc.** `EnsureCreatedAsync` and raw `ALTER TABLE` migration helpers run at startup (`Services/MonthlyFlowStore.cs:33`, `Services/MonthlyFlowStore.cs:2012`). This is workable for MVP but risky for production schema evolution and rollback.

9. **[Medium] Large project reads load all monthly rows into memory.** `GetRowsForProjectScopeAsync` reads all `MonthlyFlowRows` and filters in memory (`Services/MonthlyFlowStore.cs:185`). Large historical datasets can slow the UI and amplify request abuse.

10. **[Medium] Dangerous administrator cleanup script lives at repo root.** `Clean-MoneyFlowDB.ps1` backs up, stops service, deletes the live ProgramData DB and sidecars (`Clean-MoneyFlowDB.ps1:30`). It is intentional, but too easy to run in the wrong context.

## 6. Security Findings

- Missing app-level user authentication/authorization. The current model assumes a trusted internal network (`Program.cs:85` comment), which is not sufficient for LAN exposure.
- Import API key is optional (`Program.cs:59`) and not required by production config.
- Manual mutation endpoints are not protected (`Program.cs:403`, `Program.cs:434`, `Program.cs:444`, `Program.cs:475`).
- Production binds to all interfaces (`appsettings.Production.json:13`, `appsettings.Production.json:16`).
- No CORS middleware was found. This is better than permissive CORS, but unauthenticated same-origin mutations are still exposed to any user who can reach the app.
- Request body size cap exists at 10 MB (`Program.cs:23`), which is good.
- JSON parsing is mostly strict for monthly imports, but there is no row-count or field-length limit for monthly/contract payloads.
- `sourceFileName` and multipart `file.FileName` are stored but not written to disk (`Program.cs:132`), so path traversal is low risk today. Still, store/display only a normalized basename to avoid future misuse.
- EF Core LINQ is used for user data queries; raw SQL appears limited to PRAGMA/DDL/schema helpers with allowlisted table names (`Services/MonthlyFlowStore.cs:2019`), so SQL injection risk is low.
- Frontend imported text is mostly rendered with `textContent`, reducing XSS risk (`wwwroot/app.js:93`, `wwwroot/project.js:205`). Static SVG helper uses `innerHTML` for hardcoded icon paths (`wwwroot/project.js:219`); keep it hardcoded only.
- Sensitive error detail can leak through `Results.Problem(... detail: exception.Message ...)` (`Program.cs:159`).

## 7. Data Integrity Findings

- Exact duplicate monthly imports are detected through `ImportBatches.ContentHash` unique index (`Persistence/MoneyFlowDbContext.cs:30`) and duplicate check (`Services/MonthlyFlowStore.cs:100`).
- Edited monthly rows update existing rows through stable `RowKey` and `RowsMatch`/`CopyRowValues` (`Services/MonthlyFlowStore.cs:139`).
- Same-key rows inside one monthly file are collapsed and summed (`Services/MonthlyFlowStore.cs:130`), which preserves totals but loses individual row granularity. Confirm this is acceptable for audit needs.
- Contract imports use a transaction (`Services/MonthlyFlowStore.cs:224`); monthly imports rely on one EF save operation and unique indexes but do not explicitly begin a transaction.
- Process-local `_lock` prevents concurrent imports only within one app process (`Services/MonthlyFlowStore.cs:95`). Do not run multiple service instances against the same SQLite file.
- Manual links/assignments are unique-indexed (`Persistence/MoneyFlowDbContext.cs:132`, `Persistence/MoneyFlowDbContext.cs:145`) and tested, but unauthenticated changes can alter totals.
- Decimal columns are configured for money (`Persistence/MoneyFlowDbContext.cs:46`, `Persistence/MoneyFlowDbContext.cs:86`, `Persistence/MoneyFlowDbContext.cs:100`), and parsing uses `decimal`, not floating point.

## 8. Backend Findings

- Route design is simple and readable, but all endpoints live in `Program.cs`; controllers/route groups would improve maintainability later.
- Import DTO validation is manual. Monthly validation is relatively strong; contract validation mostly skips bad rows with warnings (`Services/MonthlyFlowStore.cs:1443`).
- Business logic is heavily concentrated in `MonthlyFlowStore.cs` (large file, many responsibilities).
- Normalization intentionally includes legal form in subcontractor matching and avoids fuzzy matching. This is safer than aggressive merging, but manual link UX becomes important.
- Logging is minimal. Import failures log server-side, but successful mutation logs do not capture actor/source.
- `AllowedHosts` is `*` in both configs, acceptable behind a trusted proxy only if proxy host filtering/TLS is correct.

## 9. Frontend/UI Findings

Frontend audit score: **15/20 Good**

| Dimension | Score | Key finding |
|---|---:|---|
| Accessibility | 3 | Good labels/ARIA on major controls, but table-row clickability and modals need focus-management verification. |
| Performance | 3 | Static JS is lean, but large tables are fully rendered and can become slow. |
| Responsive | 3 | There are responsive structures and side nav/drawer patterns, but long finance tables need browser verification on narrow screens. |
| Theming | 3 | CSS variables exist and state colors are clear; some hardcoded colors remain. |
| Anti-patterns | 3 | Product UI is restrained and task-focused. No obvious AI-marketing layout problem. |

Findings:

- UI uses safe DOM creation and `textContent` for imported text in the reviewed paths.
- Manual edit/link buttons are visible in production UI (`wwwroot/project.html:116`) and call unauthenticated mutation endpoints (`wwwroot/project.js:163`, `wwwroot/project.js:737`).
- Loading and error states exist (`wwwroot/index.html:82`, `wwwroot/app.js:508`).
- Search/filter controls exist and use labels (`wwwroot/index.html:52`, `wwwroot/project.html:103`).
- Large project tables are not virtualized; performance can degrade with many rows/months.
- Confirm dialog has ARIA attributes (`wwwroot/project.js:238`) but should move focus into the dialog and restore focus after close.

## 10. Database Findings

- Schema is defined by EF model plus startup DDL patching rather than formal migrations.
- Good indexes exist for hash/row keys/project code/manual uniqueness (`Persistence/MoneyFlowDbContext.cs:30`, `Persistence/MoneyFlowDbContext.cs:40`, `Persistence/MoneyFlowDbContext.cs:69`, `Persistence/MoneyFlowDbContext.cs:80`, `Persistence/MoneyFlowDbContext.cs:95`).
- Missing likely useful composite indexes for common detail queries by year/month/project/object/subcontractor.
- `seed/monthly-money-flow.db` ships in the repo. Confirm it does not contain sensitive real subcontractor or client data before sharing the repository.
- Backup guidance exists in `docs/deployment.md`, but backup/restore is not automated by the app.

## 11. Testing Gaps

- Existing tests are integration-style PowerShell scripts, not standard `dotnet test`; there is no test project.
- Test scripts now set a deterministic `MONEY_FLOW_API_KEY` and send `X-Api-Key` for `Invoke-RestMethod`, but the suite is still not green.
- `tests/monthly-flow-import.ps1` fails because schema 1.4 SMD/client rows are excluded from subcontractor totals, while the assertion expects total `1000` and both sheets in the monthly-flow detail payload.
- `tests/contract-import.ps1` fails at the KRS status assertion even though the numeric values are correct; the response status is `Pagal planą`, while the script appears to compare against mojibake text.
- `tests/monthly-flow-ui.ps1` fails with "Project list did not include only the parent project code", likely due to current active/inactive project filtering behavior.
- Add/keep tests for:
  - invalid monthly JSON rejected without DB writes;
  - missing/invalid API key returns 401 for import routes;
  - exact duplicate monthly import does not double-count;
  - added Excel row inserts only missing row;
  - edited Excel row updates existing row;
  - concurrent duplicate imports do not create duplicate batches;
  - oversized payload returns 413;
  - manual link/object assignment validation rejects cross-project/object changes;
  - source text containing HTML/script renders escaped in UI;
  - production config smoke test verifies Kestrel loopback binding.

## 12. Deployment and LAN Risks

- Do not expose current production config directly on the LAN. Bind Kestrel to `127.0.0.1` and put TLS/auth/access control at the reverse proxy.
- Require `MONEY_FLOW_API_KEY` or equivalent secret for PAD import endpoints before any network exposure.
- Decide how browser users authenticate before enabling manual link/object-assignment edits on a shared LAN.
- Run under a dedicated least-privilege Windows service account, not LocalSystem.
- Back up SQLite with the SQLite backup API or stop-service copy, and test restore.
- Keep `Clean-MoneyFlowDB.ps1` out of casual operator workflows.

## 13. Recommended Fix Plan

### Immediate Before LAN Exposure

1. Change production Kestrel to loopback only (`http://127.0.0.1:5000`) and enforce firewall rules so clients cannot bypass the reverse proxy.
2. Require an import API key in production; fail startup or disable import routes if missing.
3. Protect manual mutation endpoints with real auth/authorization or hide/disable the edit/link UI for network exposure.
4. Update vulnerable/outdated packages and re-run dependency scan.
5. Stop returning raw exception messages to API clients.
6. Fix the test harness so it passes with API-key middleware enabled.
7. Verify seed DB contains no sensitive real data before distribution.

### Important After MVP

1. Move schema evolution to EF migrations or a versioned migration runner.
2. Split `Program.cs` route groups and `MonthlyFlowStore.cs` responsibilities.
3. Add user/action audit logging for manual links and object assignments.
4. Add row-count/string-length limits to monthly and contract imports.
5. Add composite indexes based on real query patterns.
6. Reconcile the three failing PowerShell tests with the intended current business behavior.
7. Add a standard test project for core import services plus keep end-to-end PowerShell smoke tests.

### Nice-To-Have Cleanup

1. Rename or relocate `Clean-MoneyFlowDB.ps1` into an `ops/` folder with a stronger confirmation prompt.
2. Add README instructions for running tests with `MONEY_FLOW_API_KEY`.
3. Add UI focus tests for modals/drawers.
4. Reduce hardcoded CSS colors that remain outside tokens.
