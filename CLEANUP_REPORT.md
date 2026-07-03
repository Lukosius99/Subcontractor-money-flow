# Cleanup report

Inspection date: 2026-07-02

## Summary

The repository was inspected as a single .NET 10 web application with a static frontend, SQLite persistence, PowerShell import/deployment tooling, integration scripts, and an interactive architecture document. The source hierarchy is small and understandable; no source-folder move was justified because it would add path churn without improving the build.

The release cleanup removed a tracked real-company SQLite database and the script that copied live DB data into Git, made first deployment initialize a clean DB (or accept an explicitly supplied external copy), removed two confirmed dead JavaScript functions, expanded ignore rules, reorganized deployment documentation, and added repository policy, safe configuration, CI, and practical operating docs.

The worktree already contained uncommitted feature changes before this audit (monthly-row exclusion support across endpoints, models, persistence, services, tests, and UI). Those changes were treated as user-owned and preserved. This cleanup changed only the portions listed below.

## Repository map

| Area | Paths | Purpose |
|---|---|---|
| Backend/API | `Program.cs`, `Endpoints/`, `Dtos/`, `Services/` | Host configuration, API routes, import/query business logic |
| Frontend/UI | `wwwroot/` | Static HTML/CSS/JS, local Inter fonts, ExcelJS export library |
| Database | `Models/`, `Persistence/` | EF Core entities, SQLite context, idempotent schema initialization |
| Power Automate/PAD | import endpoints, `Set-MoneyFlowApiKey.ps1`, `docs/import-flow.md` | JSON ingestion contract and local API-key setup |
| Tests | `tests/*.ps1` | Isolated end-to-end API/UI/SQLite checks |
| Deployment | `Deploy-MoneyFlow.ps1`, `appsettings.Production.json`, `docs/deployment.md` | Windows service, firewall, ProgramData DB/config |
| Documentation | `README.md`, `docs/`, `SECURITY.md`, `CONTRIBUTING.md` | Setup, architecture, operations, security and contribution policy |
| GitHub | `.github/workflows/ci.yml` | Windows build, format, package, JS and integration gates |
| Sample data | none | Intentional: only synthetic inline test fixtures remain |
| Assets | `docs/subrangos-duomenu-kelias-3d/`, `wwwroot/fonts/` | Architecture preview and runtime fonts |

## Files removed

- `seed/monthly-money-flow.db` — tracked 1.88 MB SQLite database explicitly documented as containing real company data. Runtime and seed DB files do not belong in Git.
- `Update-SeedDatabase.ps1` — its purpose was copying a live DB into the repository, which conflicts with repository privacy rules.
- `DEPLOYMENT.md` — replaced by the shorter, current `docs/deployment.md`; all script/README references were updated.

Confirmed dead code removed:

- `wwwroot/app.js`: `collectFilterOptions` had no call site.
- `wwwroot/project.js`: `scopeUsageClass` had no call site; the UI uses `usageKind` instead.

## Files moved or reorganized

- Root deployment guidance was rewritten as `docs/deployment.md`.
- Architecture, import-flow, and troubleshooting guidance now lives in `docs/`.
- The root retains only project entry points, policy files, configuration, and deployment scripts.

## Files kept intentionally

- `wwwroot/vendor-exceljs.min.js` — large but directly loaded by `project.html` and used by the browser Excel export. Kept local so the production UI does not depend on a CDN.
- `wwwroot/fonts/*.woff2` — both are referenced by `styles.css`; the Latin Extended file covers Lithuanian glyphs.
- `docs/subrangos-duomenu-kelias-3d/*` — a self-contained architecture asset referenced by the README. It exposes system categories but no credentials or records. Public hosting still needs an explicit privacy decision.
- `appsettings.Production.json` — deployed by the project and required by the Windows-service workflow. Its ProgramData paths are standard deployment paths, not personal-machine paths.
- `Set-MoneyFlowApiKey.ps1` — copied to publish output and used indirectly by operators after installation.
- All DTO/model/service/endpoint files — symbol/reference search and integration coverage found no orphaned implementation.
- All eight PowerShell tests — each covers a distinct import, snapshot, matching, manual-edit, exclusion, or UI contract.
- Explicit `SQLitePCLRaw.bundle_e_sqlite3` reference — retained because it pins the patched SQLite bundle rather than being an unused direct dependency.

Potentially unused but retained pending visual confirmation:

- 29 CSS selectors had no literal HTML/JS reference in a static scan, including `exception-*`, `source-audit*`, `kpi-strip`, `section-bar`, and `strip-*`. CSS classes can be composed dynamically, and there is no screenshot-regression suite. Removing them during a release review would be more speculative than useful.

## Generated or local-only files

The revised `.gitignore` covers:

- `bin/`, `obj/`, `publish/`, `artifacts/`, `TestResults/`, coverage output;
- all SQLite DB and journal/WAL/SHM files;
- `test-data/`, imports, exports, PAD/Power Automate output and temp folders;
- `.env*`, API key files, local appsettings variants;
- logs, archives, backups, IDE/tool state and common OS files.

`git ls-files -ci --exclude-standard` reports only `seed/monthly-money-flow.db` because its deletion is intentionally left unstaged with the rest of the cleanup. Once the deletion is included in the cleanup commit, no tracked-and-ignored file remains.

## Dependencies

No dependency was removed:

- `Microsoft.EntityFrameworkCore.Sqlite` is the persistence provider.
- `Microsoft.Extensions.Hosting.WindowsServices` is used by `UseWindowsService`.
- `SQLitePCLRaw.bundle_e_sqlite3` is an intentional security pin.
- ExcelJS is used by client-side export.

NuGet reported no vulnerable, deprecated, or outdated direct/transitive packages from the configured sources. There is no `package.json` or npm dependency tree, so `npm ci`, `npm audit`, and frontend package checks are not applicable.

## Security findings

Current tree:

- No private key, credential assignment, SharePoint tenant URL, tracked DB, export JSON, log, or backup was found by the repository scan.
- Import POSTs require `X-Api-Key`; missing configuration returns 503 and an invalid key returns 401.
- Request bodies are capped at 10 MB.
- Production hides the subcontractor diagnostics endpoint.
- Security response headers are present.
- Import errors log the exception, not the submitted JSON or API key.
- No permissive CORS middleware is configured.

Intentional LAN risks:

- Read endpoints and browser-driven manual write/delete endpoints have no user authentication.
- Production listens on `0.0.0.0:5000` and `AllowedHosts` is `*` for flexible internal naming.
- The deployment firewall rule limits profiles to Domain/Private but does not know the company's allowed subnet.
- Local machine users permitted to modify the API-key configuration directory can also replace/read the import key.

These behaviors were retained to preserve the controlled-LAN design. `README.md`, `SECURITY.md`, and `docs/deployment.md` now require no public exposure, recommend reverse-proxy host validation/HTTPS, explicit VLAN/subnet firewall restrictions, least-privilege filesystem access, and tested backups.

Important history risk: `git log --all -- seed/monthly-money-flow.db` shows the real-data database in multiple existing commits. Removing it from the current tree does **not** remove it from Git history, remote clones, forks, caches, or prior release archives.

## GitHub professionalism improvements

- Rebuilt `README.md` with purpose, safe screenshot, data flow, stack, setup, configuration, import/API notes, database handling, deployment, checks, troubleshooting, security, status, and internal-license note.
- Added concise architecture, import, deployment, and troubleshooting docs.
- Added `appsettings.example.json` with fake/internal-example values only.
- Added `SECURITY.md` and `CONTRIBUTING.md`.
- Added `.editorconfig` and `.gitattributes` for consistent text formatting.
- Added `.github/workflows/ci.yml`; it uses Windows because the integration suite and deployment tooling are PowerShell/Windows-oriented.
- Added only one meaningful badge: the real CI workflow.
- Did not add an open-source `LICENSE`; the README states that this internal project is not licensed for public reuse.
- Did not add a `CHANGELOG.md`; there is no established release/version history yet, so an empty changelog would be ceremony rather than useful documentation.

## Checks run

Baseline and final checks:

- `git status --short` — passed; also identified pre-existing uncommitted user work, which was preserved.
- `dotnet --info` — .NET SDK 10.0.300, runtime 10.0.8.
- `dotnet restore PADS.MoneyFlow.Api.csproj` — passed.
- `dotnet format PADS.MoneyFlow.Api.csproj --verify-no-changes --no-restore` — baseline found one whitespace diagnostic in `MonthlyFlowStore.Queries.cs`; corrected. Final run passed, 0 files needing format.
- `dotnet build PADS.MoneyFlow.Api.csproj -c Release --no-restore` — final sequential run passed with 0 warnings and 0 errors.
- `dotnet test PADS.MoneyFlow.Api.csproj -c Release --no-restore` — exit 0, but no separate test project/tests were discovered.
- `dotnet publish PADS.MoneyFlow.Api.csproj -c Release --no-restore -o publish/release-review` — passed; executable and `wwwroot/index.html` were present.
- `dotnet list PADS.MoneyFlow.Api.csproj package --vulnerable --include-transitive` — passed; none found.
- `dotnet list PADS.MoneyFlow.Api.csproj package --deprecated` — passed; none found.
- `dotnet list PADS.MoneyFlow.Api.csproj package --outdated --include-transitive` — passed; no updates found.
- PowerShell AST parse for deployment/helper/test scripts — passed, 0 parse errors.
- `node --check` for all `wwwroot` and documentation JavaScript — passed, 0 parse failures.
- All `tests/*.ps1` in name order — 8/8 passed: contract import, contract snapshot, ignored rows, manual links, monthly import, realistic April data, UI smoke, and subcontractor matching.
- `git diff --check` — passed.
- Local Markdown link validation — passed, 0 broken links.
- Secret/data-like tracked-file scan — no current-tree findings.
- `.gitignore` sample checks — passed for DB, seed, test data, output, `.env`, API-key, and publish paths.

One intermediate validation was intentionally recorded rather than hidden: running Release `dotnet build` and `dotnet publish` concurrently caused `CS2012` because both processes wrote `obj/Release/net10.0/PADS.MoneyFlow.Api.dll`. Publish completed, and the build passed immediately when rerun sequentially. This was command contention, not a source failure; CI runs the steps sequentially.

## Remaining risks

1. The removed company database remains in Git history and may already exist in the GitHub remote, clones, forks, caches, and downloaded ZIPs.
2. GitHub Pages may publicly expose the internal architecture page depending on repository/account settings. The README no longer links a public Pages URL, but settings require manual review.
3. Unauthenticated manual edits are safe only while the LAN/reverse-proxy boundary is trustworthy; there is no per-user audit identity.
4. `0.0.0.0:5000`, wildcard hosts, and a Domain/Private-only firewall rule still need site-specific subnet/host restrictions from the network administrator.
5. Schema changes use custom idempotent initialization rather than versioned EF migrations; restoration testing against an older production copy remains operationally important.
6. CSS cleanup needs browser visual regression coverage before the 29 suspicious selectors can be removed confidently.
7. The interactive 3D documentation loads `3d-force-graph` from `unpkg.com`; it will not work on an offline LAN and should not be treated as a production dependency.

## Recommended next actions

1. Before the next release, coordinate a history rewrite (`git filter-repo` or BFG) to purge `seed/monthly-money-flow.db` from every branch/tag, then force-push, invalidate old clones/archives, and verify GitHub Pages/fork access. This is intentionally not automated here because it is destructive and requires maintainer coordination.
2. Have the network administrator restrict the firewall/reverse proxy to the intended VLAN/subnets and allowed internal hostnames.
3. Add browser screenshot regression tests before removing the suspicious legacy CSS selectors.
