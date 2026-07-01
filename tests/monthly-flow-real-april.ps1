$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/monthly-flow-real-april.db"
$baseUrl = "http://localhost:5091"

$json = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    sheetName = "Realistic April"
    rows = @(
        @{
            sourceRow = 10
            projectCode = "P1707-01"
            subcontractorName = "Keliai LT"
            objectName = "Roadbed"
            amountWithoutVat = 20000.61
        },
        @{
            sourceRow = 11
            projectCode = "P1707-01"
            subcontractorName = "Betonika"
            objectName = "Concrete"
            amountWithoutVat = 10000
        },
        @{
            sourceRow = 12
            projectCode = "P1707-01"
            subcontractorName = "Signal Pro"
            objectName = "Signals"
            amountWithoutVat = 2000
        },
        @{
            sourceRow = 13
            projectCode = "P1707-01"
            subcontractorName = "Signal Pro"
            objectName = "Cabling"
            amountWithoutVat = 436
        },
        @{
            sourceRow = 20
            projectCode = "P1800-01"
            subcontractorName = "Delta"
            amountWithoutVat = 500
        },
        @{
            sourceRow = 21
            projectCode = "P1900-01"
            subcontractorName = "Echo"
            amountWithoutVat = 600
        }
    )
    warnings = @(
        @{
            sourceRow = 30
            message = "Submitted warning from export."
        }
    )
} | ConvertTo-Json -Depth 8

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = "test-api-key"
$PSDefaultParameterValues["Invoke-RestMethod:Headers"] = @{ "X-Api-Key" = $env:MONEY_FLOW_API_KEY }
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest -Uri "$baseUrl/api/projects" -UseBasicParsing | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $ready) {
        throw "API did not start at $baseUrl"
    }

    $firstImport = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $json
    if (-not $firstImport.imported -or $firstImport.rowsInserted -ne 6 -or $firstImport.warningsImported -ne 1) {
        throw "Realistic fixture import failed: $($firstImport | ConvertTo-Json -Compress)"
    }

    # Projects are listed by their parent prefix (e.g. P1707), with P1707-01 as an
    # object inside the project. Monthly rows carry the full object code in
    # projectCode, so the list must surface the derived prefix. Without a
    # contracted snapshot, monthly-only projects stay inactive.
    $projects = Invoke-RestMethod "$baseUrl/api/projects"
    foreach ($projectCode in @("P1707", "P1800", "P1900")) {
        if ($projects.allProjectCodes -notcontains $projectCode) {
            throw "Project list did not include expected project code '$projectCode': $($projects.allProjectCodes -join ', ')"
        }

        if ($projects.inactiveProjectCodes -notcontains $projectCode) {
            throw "Monthly-only project '$projectCode' was not listed as inactive: $($projects.inactiveProjectCodes -join ', ')"
        }
    }

    if (@($projects.projectCodes).Count -ne 0) {
        throw "Monthly-only projects should not appear in the active project list: $($projects.projectCodes -join ', ')"
    }

    $monthlyFlow = Invoke-RestMethod "$baseUrl/api/projects/P1707-01/monthly-flow"
    $projectRows = @($monthlyFlow.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ })
    if ($projectRows.Count -ne 4) {
        throw "Expected P1707-01 to have 4 imported rows, got $($projectRows.Count): $($monthlyFlow | ConvertTo-Json -Depth 8 -Compress)"
    }

    if ([decimal]::Round([decimal]$monthlyFlow.totals.amountWithoutVat, 2) -ne [decimal]32436.61) {
        throw "Expected P1707-01 total 32436.61, got $($monthlyFlow.totals.amountWithoutVat)."
    }

    $subcontractorTotals = @($monthlyFlow.totals.bySubcontractor)
    $expectedSubcontractorTotals = @{
        "Keliai LT" = [decimal]20000.61
        "Betonika" = [decimal]10000.00
        "Signal Pro" = [decimal]2436.00
    }

    foreach ($name in $expectedSubcontractorTotals.Keys) {
        $actual = @($subcontractorTotals | Where-Object { $_.subcontractorName -eq $name })
        if ($actual.Count -ne 1 -or [decimal]::Round([decimal]$actual[0].amountWithoutVat, 2) -ne $expectedSubcontractorTotals[$name]) {
            throw "Unexpected subcontractor total for '$name': $($monthlyFlow.totals.bySubcontractor | ConvertTo-Json -Depth 8 -Compress)"
        }
    }

    $duplicate = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $json
    if ($duplicate.imported -or $duplicate.reason -ne "Duplicate import" -or $duplicate.rowsImported -ne 0) {
        throw "Duplicate realistic fixture import was not idempotent: $($duplicate | ConvertTo-Json -Compress)"
    }

    $monthlyFlowAfterDuplicate = Invoke-RestMethod "$baseUrl/api/projects/P1707-01/monthly-flow"
    $rowsAfterDuplicate = @($monthlyFlowAfterDuplicate.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ })
    if ([decimal]::Round([decimal]$monthlyFlowAfterDuplicate.totals.amountWithoutVat, 2) -ne [decimal]32436.61 -or $rowsAfterDuplicate.Count -ne 4) {
        throw "Duplicate import changed P1707-01 totals or rows: $($monthlyFlowAfterDuplicate | ConvertTo-Json -Depth 8 -Compress)"
    }

    "Realistic monthly-flow SQLite checks passed. Project codes: $(@($projects.projectCodes).Count). P1707-01 total: $($monthlyFlow.totals.amountWithoutVat)."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
