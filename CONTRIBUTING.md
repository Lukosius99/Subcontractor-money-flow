# Contributing

This is an internal project. Confirm the business rule with the maintainer before changing import semantics, matching keys, snapshot behavior, or schema initialization.

## Workflow

1. Create a short-lived branch and keep the PR focused.
2. Do not mix generated output or data refreshes with source changes.
3. Update the relevant documentation and integration test when behavior changes.
4. Request review before merging to `main`.

## Required checks

```powershell
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release --no-restore
dotnet list package --vulnerable --include-transitive
Get-ChildItem tests -Filter *.ps1 | Sort-Object Name | ForEach-Object { & $_.FullName }
```

Also run `node --check` for changed JavaScript files. There is no npm dependency tree or frontend build step.

## Data privacy

Only synthetic data belongs in tests and docs. Never attach a live SQLite DB, SharePoint export, API key, access token, log, or screenshot containing company / personal data to a commit, PR, issue, or CI artifact.
