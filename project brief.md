# PADS Monthly Money Flow Project Brief

## Project Overview

PADS Monthly Money Flow is an internal finance and project-control web application for tracking subcontractor monthly money flow by project and object. It imports monthly money-flow JSON exported from Excel/Office Scripts and contract data exported from Dynamics/PAD workflows, stores the normalized data in SQLite, and presents project-level and object-level dashboards for comparing invoiced amounts against contracted amounts.

The main business goal is to help finance, project managers, engineers, and responsible staff quickly answer:

- Which active projects are currently in the latest contract import?
- How much has been invoiced by subcontractors?
- How much remains against contracted values?
- Which projects or object scopes are near limit, over limit, missing contracts, or missing object numbers?
- Which monthly imports have already been processed?

## Main Users

- Finance and accounting users who import and review monthly subcontractor spend.
- Project control users who compare invoices against contract totals.
- Project managers who monitor project and object-level usage.
- Engineers and responsible staff who validate imported rows and warnings.
- Administrators who run the app locally, deploy it on a Windows server, or maintain the database.

## Technology Stack

- .NET 10 minimal API using `Microsoft.NET.Sdk.Web`.
- ASP.NET Core static file hosting for the web UI in `wwwroot`.
- Entity Framework Core 10 with SQLite via `Microsoft.EntityFrameworkCore.Sqlite`.
- SQLite database files for persistent storage.
- Windows Service hosting through `Microsoft.Extensions.Hosting.WindowsServices`.
- Plain HTML, CSS, and vanilla JavaScript frontend.
- PowerShell and batch scripts for local development, service control, cleanup, deployment support, and regression checks.
- Caddy reverse proxy is documented for LAN deployment with TLS termination.
- JSON-based integration contracts for Office Script, PAD, Cloud Flow, and Dynamics-style exports.

## Repository Structure

- `Program.cs` configures the app, dependency injection, database path resolution, static files, and API endpoints.
- `PADS.MoneyFlow.Api.csproj` defines the .NET target framework, package dependencies, and publish behavior.
- `Models/` contains EF Core entity models for imports, rows, projects, contracts, object values, subcontractors, and aliases.
- `Dtos/` contains request and response contracts for monthly-flow and contract imports.
- `Persistence/MoneyFlowDbContext.cs` defines SQLite tables, indexes, relationships, and decimal mappings.
- `Services/MonthlyFlowImportService.cs` parses and validates monthly JSON imports.
- `Services/MonthlyFlowStore.cs` owns import persistence, contract upserts, matching, project summaries, detail snapshots, and subcontractor normalization.
- `wwwroot/` contains the static frontend pages, JavaScript, and styles.
- `tests/` contains PowerShell regression tests that start the API against isolated SQLite databases.
- `docs/` contains deployment and data cleanup notes.
- `data/` contains the default app database location for local/non-production runs.
- `test-data/` contains local test databases, generated logs, and sample import data.
- `start-local.bat` and `reset-local-db.bat` support double-click local development workflows.
- `Install-MoneyFlow.ps1`, `Start-MoneyFlow.ps1`, `Stop-MoneyFlow.ps1`, and `Clean-MoneyFlowDB.ps1` support Windows operation workflows.

## Application Architecture

The application is a single ASP.NET Core process that serves both API endpoints and the static web UI.

At runtime:

1. ASP.NET Core starts the minimal API.
2. The app resolves the SQLite database path from configuration or environment variables.
3. Entity Framework Core creates or opens the SQLite database.
4. `MonthlyFlowStore` initializes tables and master data.
5. Static frontend files are served from `wwwroot`.
6. Import endpoints accept JSON or uploaded files.
7. Dashboard endpoints return project summaries and detail data to the frontend.

In production, the app is intended to run as a Windows Service behind Caddy:

- Kestrel runs the .NET app.
- Caddy terminates TLS and reverse-proxies requests to Kestrel.
- SQLite lives outside the publish directory, usually under `C:\ProgramData\PADS\MoneyFlow`.
- Backups copy or snapshot the SQLite database file.

## Data Storage

The database is SQLite. The active database path is resolved in this order:

1. `MoneyFlow:DatabasePath` from appsettings.
2. `MONEY_FLOW_DB_PATH` environment variable.
3. `data/monthly-money-flow.db` under the app base directory.

Important tables include:

- `ImportBatches`: records monthly and contract import batches, content hashes, row counters, periods, and status.
- `MonthlyFlowRows`: stores imported monthly subcontractor and SMD/client value rows.
- `ImportWarnings`: stores import warnings tied to a batch.
- `Projects`: stores project snapshots and active/latest-contract-import flags.
- `SubcontractorContracts`: stores contract rows by project, object, and subcontractor.
- `ProjectObjectValues`: stores object-level client/project value rows.
- `Subcontractors`: stores canonical subcontractor identities.
- `SubcontractorAliases`: stores raw-to-canonical subcontractor name aliases.

Indexes enforce key behaviors such as unique import content hashes, unique monthly row keys, unique contract row keys, and unique project codes.

## Core API Endpoints

- `POST /api/imports/monthly-flow`
  Imports monthly Office Script JSON. Accepts raw JSON or multipart file upload. `sourceFileName` must be supplied through query string, header, or uploaded file name.

- `POST /api/imports/contracts`
  Imports contract rows and project object value rows from Dynamics/PAD-style JSON.

- `GET /api/imports/monthly-flow/status`
  Returns latest and historical import batch counters.

- `GET /api/diagnostics/subcontractors`
  Returns normalized subcontractor identities and aliases for diagnostics.

- `GET /api/projects`
  Returns active projects, inactive projects, parent project codes, object summaries, totals, statuses, warnings, and display fields.

- `GET /api/projects/{projectCode}/monthly-flow`
  Returns detail data for all objects under a parent project or a selected object, including monthly rows, totals, contract rows, object values, and SMD customer rows.

## Business and Matching Rules

- Parent project code is derived from the part before the last dash.
  Example: `P1730-01` becomes parent project `P1730`.

- Object code is derived from the part after the last dash.
  Example: `P1730-01` has object code `01`.

- Object number is the stable object matching key.

- Monthly subcontractor invoices and contract rows are matched by:
  `ProjectCode + ObjectNumber + normalized SubcontractorName`.

- `ObjectName` is display-only and must not be used as a stable matching key.

- Monthly rows without object numbers are still imported and shown as missing object number or missing contract.

- Monthly row identity includes year, month, source sheet, source row, project/object context, and subcontractor/customer identity.

- Duplicate monthly imports are detected by content hash and reported as duplicates without changing totals.

- Multiple monthly rows with the same logical row key inside one file are collapsed and summed before storage.

- Subcontractor names are normalized by removing legal-form noise, quote characters, punctuation noise, repeated whitespace, and common formatting differences.

- SMD rows are treated as client monthly value rows, while regular `subranga` rows are treated as subcontractor invoice rows.

## Monthly Import Workflow

1. Excel or Office Scripts produces monthly JSON.
2. The JSON includes schema version, year, month, source rows, source sheet, project code, object number, subcontractor/customer names, amounts, responsible person, engineer, and warnings.
3. A user, PAD flow, Cloud Flow, or test script posts the JSON to `POST /api/imports/monthly-flow`.
4. `MonthlyFlowImportService` validates schema versions `1.3` and `1.4`.
5. Schema `1.4` requires `sourceSheet`.
6. Rows are classified as subcontractor invoice rows or SMD/client value rows.
7. The app computes a content hash and checks for duplicate imports.
8. New rows are inserted, changed rows are updated, and unchanged rows are skipped.
9. Warnings and import counters are stored in SQLite.
10. Dashboard totals and detail views reflect the latest persisted data.

## Contract Import Workflow

1. Dynamics/PAD-style contract data is submitted to `POST /api/imports/contracts`.
2. The request may include subcontractor contract rows and project value rows.
3. The store validates, normalizes, and upserts project snapshots.
4. Contract rows are matched by project code, object number, and normalized subcontractor name.
5. Existing contracts are updated when a logical match is found.
6. New logical rows are inserted when no match exists.
7. Projects absent from the latest contract import are marked inactive.
8. Project object values are inserted or updated by row key.
9. Import counters, warnings, and latest-contract flags are returned in the response.

## Dashboard Workflow

1. Users open the root page served from `wwwroot/index.html`.
2. `wwwroot/app.js` calls `GET /api/projects`.
3. The project register shows active projects from the latest contract import by default.
4. Users can switch between active and inactive projects.
5. Users can search, filter by responsible person or engineer, filter by status, filter warnings, and sort columns.
6. Selecting a project opens `project.html`.
7. Single-object projects open directly to that object.
8. Multi-object projects show an object scope selector with `All objects` plus individual object tabs.
9. `wwwroot/project.js` calls `GET /api/projects/{projectCode}/monthly-flow`.
10. The detail page shows contracted, invoiced, remaining, usage, warnings, monthly totals, imported source rows, and client/object values.

## Local Development Workflow

Use `start-local.bat` for the normal local workflow:

1. Double-click or run `start-local.bat`.
2. The script sets `ASPNETCORE_ENVIRONMENT=Development`.
3. The app starts at `http://localhost:5000`.
4. The script uses `test-data/local-dev.db` as the local SQLite database.
5. On first launch, it can seed local data from `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db` if present.
6. The script checks that port `5000` is not already in use.
7. The browser opens automatically after startup.

Use `reset-local-db.bat` to clear only the local development database:

1. Run `reset-local-db.bat`.
2. Confirm the prompt.
3. The script deletes `test-data/local-dev.db` and SQLite sidecar files.
4. The next local launch creates a fresh empty database.

Manual local run:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = "test-data/local-dev.db"
dotnet run --urls http://localhost:5000
```

## Testing and Verification Workflow

Basic build verification:

```powershell
dotnet build
```

PowerShell regression scripts in `tests/` start the API on test ports, point it at isolated SQLite databases under `test-data/`, run API calls, assert responses and totals, then stop the process.

Current regression scripts:

- `tests/monthly-flow-import.ps1`: validates monthly import insert, update, skip, duplicate, schema `1.4`, multi-sheet behavior, and parent project listing.
- `tests/contract-import.ps1`: validates contract import behavior and matching.
- `tests/contract-snapshot.ps1`: validates project and contract snapshot behavior.
- `tests/monthly-flow-real-april.ps1`: validates a realistic April monthly import sample.
- `tests/monthly-flow-ui.ps1`: validates UI-facing project grouping and object workflow behavior.
- `tests/subcontractor-name-matching.ps1`: validates subcontractor normalization and alias matching.

Example test commands:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\monthly-flow-import.ps1
powershell -ExecutionPolicy Bypass -File .\tests\contract-import.ps1
powershell -ExecutionPolicy Bypass -File .\tests\monthly-flow-ui.ps1
```

## Deployment Workflow

The documented LAN deployment model is:

1. Publish the app:

```powershell
dotnet publish .\PADS.MoneyFlow.Api.csproj -c Release -o C:\Apps\MoneyFlow --self-contained false
```

2. Store persistent data outside the app folder:

```powershell
New-Item -ItemType Directory -Force -Path "C:\ProgramData\PADS\MoneyFlow"
```

3. Install and run the app as a Windows Service named `MoneyFlow` with display name `PADS Monthly Money Flow`.

4. Configure production database path in `appsettings.Production.json`:

```json
{
  "MoneyFlow": {
    "DatabasePath": "C:\\ProgramData\\PADS\\MoneyFlow\\monthly-money-flow.db"
  }
}
```

5. Put Caddy in front of Kestrel for LAN HTTPS.

6. Open only proxy ports `80` and `443` in the firewall.

7. Keep Kestrel internal to the server where possible.

8. Back up the SQLite database nightly.

9. Redeploy by publishing new files, stopping the service, copying app files, and starting the service again.

Supporting service scripts:

- `Install-MoneyFlow.ps1`: installs and starts the Windows Service.
- `Start-MoneyFlow.ps1`: starts the service and performs a simple HTTP check.
- `Stop-MoneyFlow.ps1`: stops the service.
- `Clean-MoneyFlowDB.ps1`: supports database cleanup workflows.

## Configuration

Development configuration lives in `appsettings.json` and primarily controls logging.

Production configuration lives in `appsettings.Production.json` and includes:

- Logging levels.
- Kestrel endpoint configuration.
- `MoneyFlow:DatabasePath`.
- Allowed hosts.

Environment variables used by workflows:

- `ASPNETCORE_ENVIRONMENT`: selects Development or Production behavior.
- `ASPNETCORE_URLS`: can override Kestrel binding.
- `MONEY_FLOW_DB_PATH`: overrides the SQLite database path.

## Frontend Technologies and UX

The frontend is intentionally lightweight:

- Static HTML pages.
- Shared CSS in `wwwroot/styles.css`.
- Vanilla JavaScript in `wwwroot/app.js` and `wwwroot/project.js`.
- Browser `fetch` calls to API endpoints.
- Client-side sorting, search, filters, tab switching, table rendering, and formatting.

Main frontend pages:

- `wwwroot/index.html`: project register.
- `wwwroot/project.html`: project/object detail view.

Key UI features:

- Active/inactive project tabs.
- Project search.
- Responsible and engineer filters.
- Status and warning filters.
- Sortable table columns.
- Parent-project grouping.
- Object scope tabs.
- Contract usage visualization.
- Monthly source row details.
- Client/SMD value section.

## Operational Notes

- Do not delete the production database folder during redeploys.
- `data/` and `test-data/` are excluded from publish content by the project file.
- Local development databases are separate from production data.
- The API creates the SQLite database and tables if needed.
- Import endpoints should remain stable because PAD/Cloud Flow integrations depend on their shape.
- Existing monthly JSON schema and contract import schema should only change intentionally.
- The app currently has no authentication or role-based access.
- Import preview, manual row editing, export to Excel/PDF, scheduled imports, and a full row-level audit UI are not implemented yet.

## Current MVP Status

Implemented:

- Monthly JSON import for schema versions `1.3` and `1.4`.
- Raw JSON and multipart monthly upload support.
- Contract import endpoint.
- SQLite persistence through EF Core.
- Duplicate monthly import detection.
- Subcontractor normalization and alias tracking.
- Parent-project grouping.
- Active and inactive project tracking from contract imports.
- All-objects and single-object detail views.
- Subcontractor contract usage calculations.
- SMD/client monthly value handling.
- PowerShell regression tests.
- Local run and local reset scripts.
- Windows Service deployment path.
- LAN deployment documentation with Caddy.

Not yet implemented:

- Authentication and authorization.
- Manual editing of imported rows.
- Rich import preview before save.
- Excel upload UI for contract imports.
- Scheduled automated imports.
- Export back to Excel or PDF.
- Full audit-history UI.
- Advanced reporting beyond the current dashboard.

