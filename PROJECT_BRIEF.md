# PADS Monthly Money Flow Project Brief

## Purpose

This application tracks monthly subcontractor money flow by project and object. It imports monthly Excel export data and Dynamics contract data, then compares invoiced amounts against contracted amounts for project monitoring.

## Core Users

- Finance and project control users reviewing monthly subcontractor spend.
- Project managers checking object-level contract usage.
- Engineers or responsible staff validating imported monthly rows.

## Primary Workflows

1. Import Dynamics contract data through:
   `POST /api/imports/contracts`

2. Import monthly Office Script JSON through:
   `POST /api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json`

3. Review parent projects on the Projects page.

4. Open a project:
   - If it has one object, open that object detail directly.
   - If it has multiple objects, show an object selector.

5. Review either:
   - All objects combined under a parent project.
   - A single selected object.

## Key Data Rules

- Parent project code is derived from the part before the last dash.
  - `P1730-01` -> `P1730`
  - `P1732-02` -> `P1732`

- Object code is the part after the last dash.
  - `P1730-01` -> `01`
  - `P1732-02` -> `02`

- Object number remains the stable matching key internally.

- Contract and monthly matching uses:
  `ProjectCode + ObjectNumber + SubcontractorName`

- `ObjectName` is display-only and must not be used for matching.

- Monthly rows without `ObjectNumber` are still imported, but appear as missing object number / missing contract.

- `SourceSheet` is stored and used in monthly row identity so rows from `subranga` and `SMD` do not collide.

## Monthly Import Expectations

Monthly Office Script JSON supports rows shaped like:

```json
{
  "sourceSheet": "subranga",
  "sourceRow": 7,
  "projectCode": "P1730-01",
  "objectNumber": "P1730-01",
  "subcontractorName": "KRS",
  "objectName": "Display object name",
  "amountWithoutVat": 10000,
  "indexedAmount": null,
  "responsible": "J. Bielevicius",
  "engineer": "P. Verbickas"
}
```

Supported monthly schema versions:

- `1.3`
- `1.4`

For schema `1.4`, `sourceSheet` is required.

## UI Behavior

### Projects Page

The first Projects page shows only parent project codes, for example:

- `P1730`
- `P1732`
- `P1706`

It does not show object-level project codes like `P1730-01`.

Project rows/cards include:

- Project
- Objects count
- Contracted total
- Invoiced total
- Remaining
- Rows
- Subcontractors
- Warnings
- Status

### Object Selector

When a parent project has multiple distinct object numbers, the detail page shows:

- All objects
- One option per object number, such as `P1730-01`, `P1730-02`

### Project Detail

All-objects mode:

- Combines totals across all objects under the parent project.
- Includes an Object number column in the contract/invoice table.

Single-object mode:

- Shows only the selected object number.
- Keeps the existing object-level detail behavior.

## Current Verification Scripts

Useful regression checks:

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File .\tests\monthly-flow-import.ps1
powershell -ExecutionPolicy Bypass -File .\tests\contract-import.ps1
powershell -ExecutionPolicy Bypass -File .\tests\monthly-flow-ui.ps1
```

## MVP Status

### Already Done

- Monthly Office Script JSON import endpoint is implemented.
- Raw JSON monthly import request body is supported.
- Monthly schema versions `1.3` and `1.4` are supported.
- `sourceSheet` is supported for monthly rows.
- Monthly rows from both `subranga` and `SMD` can be imported.
- Monthly row identity includes `SourceSheet` so same row numbers from different sheets do not collide.
- `indexedAmount` can be `null`.
- Monthly rows store `ObjectNumber`.
- Dynamics contract import endpoint is implemented.
- Contract rows store `ObjectNumber`.
- Contract matching uses `ProjectCode + ObjectNumber + SubcontractorName`.
- `ObjectName` is display-only for matching.
- Monthly rows with matching `ObjectNumber` fill existing contract rows instead of creating duplicate missing-contract rows.
- Monthly rows without `ObjectNumber` still import and show as missing object number / missing contract.
- Monthly import can fill blank display fields on existing contracts.
- Dynamics contract import does not require `ProjectName`, `ObjectName`, `Responsible`, or `Engineer`.
- Dynamics contract import does not overwrite existing display fields with null or empty values.
- Projects page groups object-level project codes into parent project codes.
- Parent project object count is calculated from merged monthly and contract object numbers.
- Single-object parent projects open directly to object detail.
- Multi-object parent projects open the object selector.
- Object selector supports All objects and one option per object number.
- All-objects detail view aggregates totals across object numbers.
- Single-object detail view filters to one selected object number.
- Existing import endpoints remain unchanged.
- Regression scripts exist for monthly import, contract import, and UI grouping behavior.

### MVP Definition

The MVP is a working internal dashboard that lets users:

1. Import contract data.
2. Import monthly money-flow data.
3. Match monthly invoices to contract rows by stable object/subcontractor keys.
4. Review parent-level project totals.
5. Drill into all objects or a single object.
6. Identify missing object numbers and missing contracts.
7. Validate contracted, invoiced, remaining, usage, and status values.

### Not In MVP Yet

- User authentication or role-based access.
- Manual editing of imported monthly rows.
- Full audit history UI for every row-level change.
- Rich import preview before saving.
- Excel upload UI for contract imports.
- Advanced filtering by responsible person, engineer, subcontractor, or status.
- Exporting dashboard views back to Excel or PDF.
- Automated scheduled imports.
- Production deployment configuration.

## Constraints

- Do not change import endpoints unless explicitly requested.
- Do not change PAD monthly import flow unless explicitly requested.
- Do not change monthly JSON schema unless explicitly requested.
- Do not change contract import schema unless explicitly requested.
- Do not wipe existing data automatically.
