$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/monthly-flow-import.db"
$baseUrl = "http://localhost:5090"

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

function New-MonthlyFlowJson($rows) {
    @{
        schemaVersion = "1.3"
        year = 2026
        month = 4
        sheetName = "April"
        rows = $rows
        warnings = @()
    } | ConvertTo-Json -Depth 8
}

function New-MonthlyFlowJsonV14($rows) {
    @{
        schemaVersion = "1.4"
        year = 2026
        month = 5
        sheetName = "May"
        rows = $rows
        warnings = @()
    } | ConvertTo-Json -Depth 8
}

$firstJson = New-MonthlyFlowJson @(
    @{
        sourceRow = 5
        projectCode = "P1000-01"
        subcontractorName = "Alpha Build"
        objectName = "Road works"
        amountWithoutVat = 100
        indexedAmount = 110
        responsible = "Rasa"
        engineer = "Jonas"
    },
    @{
        sourceRow = 6
        projectCode = "P1000-01"
        subcontractorName = "Beta Works"
        objectName = "Lighting"
        amountWithoutVat = 200
    }
)

$addedRowJson = New-MonthlyFlowJson @(
    @{
        sourceRow = 5
        projectCode = "P1000-01"
        subcontractorName = "Alpha Build"
        objectName = "Road works"
        amountWithoutVat = 100
        indexedAmount = 110
        responsible = "Rasa"
        engineer = "Jonas"
    },
    @{
        sourceRow = 6
        projectCode = "P1000-01"
        subcontractorName = "Beta Works"
        objectName = "Lighting"
        amountWithoutVat = 200
    },
    @{
        sourceRow = 7
        projectCode = "P2000-01"
        subcontractorName = "Gamma"
        objectName = "Bridge"
        amountWithoutVat = 300
    }
)

$editedRowJson = New-MonthlyFlowJson @(
    @{
        sourceRow = 5
        projectCode = "P1000-01"
        subcontractorName = "Alpha Build"
        objectName = "Road works"
        amountWithoutVat = 150
        indexedAmount = 110
        responsible = "Rasa"
        engineer = "Jonas"
    },
    @{
        sourceRow = 6
        projectCode = "P1000-01"
        subcontractorName = "Beta Works"
        objectName = "Lighting"
        amountWithoutVat = 200
    },
    @{
        sourceRow = 7
        projectCode = "P2000-01"
        subcontractorName = "Gamma"
        objectName = "Bridge"
        amountWithoutVat = 300
    }
)

$multiSheetJson = New-MonthlyFlowJsonV14 @(
    @{
        sourceSheet = "subranga"
        sourceRow = 5
        projectCode = "P3000-01"
        objectNumber = "OBJ-001"
        subcontractorName = "Delta"
        objectName = "Drainage"
        amountWithoutVat = 400
        indexedAmount = $null
        responsible = "Rasa"
        engineer = "Jonas"
    },
    @{
        sourceSheet = "SMD"
        sourceRow = 5
        projectCode = "P3000-01"
        objectNumber = "OBJ-001"
        subcontractorName = "Delta"
        objectName = "Drainage"
        amountWithoutVat = 600
        indexedAmount = $null
        responsible = "Rasa"
        engineer = "Jonas"
    }
)

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
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

    $first = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $firstJson
    if (-not $first.imported -or $first.rowsInserted -ne 2 -or $first.rowsUpdated -ne 0 -or $first.rowsSkipped -ne 0) {
        throw "First import did not insert two rows: $($first | ConvertTo-Json -Compress)"
    }

    $duplicate = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $firstJson
    if ($duplicate.imported -or $duplicate.reason -ne "Duplicate import" -or $duplicate.rowsImported -ne 0) {
        throw "Duplicate import was not idempotent: $($duplicate | ConvertTo-Json -Compress)"
    }

    $afterDuplicate = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1000-01/monthly-flow"
    $rowsAfterDuplicate = @($afterDuplicate.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ })
    if ($rowsAfterDuplicate.Count -ne 2 -or [decimal]::Round([decimal]$afterDuplicate.totals.amountWithoutVat, 2) -ne [decimal]300.00) {
        throw "Duplicate import changed rows or totals: $($afterDuplicate | ConvertTo-Json -Depth 8 -Compress)"
    }

    $added = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $addedRowJson
    if (-not $added.imported -or $added.rowsInserted -ne 1 -or $added.rowsUpdated -ne 0 -or $added.rowsSkipped -ne 2) {
        throw "Added-row import should insert one row and skip two unchanged rows: $($added | ConvertTo-Json -Compress)"
    }

    $edited = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $editedRowJson
    if (-not $edited.imported -or $edited.rowsInserted -ne 0 -or $edited.rowsUpdated -ne 1 -or $edited.rowsSkipped -ne 2) {
        throw "Edited-row import should update one row and skip two unchanged rows: $($edited | ConvertTo-Json -Compress)"
    }

    $project1000 = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1000-01/monthly-flow"
    $project1000Rows = @($project1000.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ })
    if ($project1000Rows.Count -ne 2 -or [decimal]::Round([decimal]$project1000.totals.amountWithoutVat, 2) -ne [decimal]350.00) {
        throw "Edited import did not keep P1000-01 totals correct: $($project1000 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $project2000 = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P2000-01/monthly-flow"
    if ([decimal]::Round([decimal]$project2000.totals.amountWithoutVat, 2) -ne [decimal]300.00) {
        throw "Added row total for P2000-01 was incorrect: $($project2000 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $multiSheet = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json" -ContentType "application/json" -Body $multiSheetJson
    if (-not $multiSheet.imported -or $multiSheet.rowsInserted -ne 2 -or $multiSheet.rowsUpdated -ne 0 -or $multiSheet.rowsSkipped -ne 0) {
        throw "Schema 1.4 multi-sheet import should insert both same-sourceRow rows: $($multiSheet | ConvertTo-Json -Compress)"
    }

    $project3000 = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P3000-01/monthly-flow"
    $project3000Rows = @($project3000.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ })
    $project3000Sheets = @($project3000Rows | ForEach-Object { $_.sourceSheet })
    if ($project3000Rows.Count -ne 2 -or [decimal]::Round([decimal]$project3000.totals.amountWithoutVat, 2) -ne [decimal]1000.00) {
        throw "Schema 1.4 multi-sheet rows did not total correctly: $($project3000 | ConvertTo-Json -Depth 8 -Compress)"
    }

    if ($project3000Sheets -notcontains "subranga" -or $project3000Sheets -notcontains "SMD") {
        throw "Schema 1.4 sourceSheet was not exposed on imported rows: $($project3000 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $history = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/imports/monthly-flow/status"
    if ($null -eq $history.latestImport -or $history.latestImport.rowsInserted -ne 2) {
        throw "Import status did not expose latest SQLite batch counters: $($history | ConvertTo-Json -Depth 8 -Compress)"
    }

    $projects = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects"
    if ($projects.allProjectCodes -notcontains "P1000" -or $projects.allProjectCodes -notcontains "P2000" -or $projects.allProjectCodes -notcontains "P3000" -or $projects.allProjectCodes -contains "P1000-01") {
        throw "Project list did not include only parent project codes in allProjectCodes: $($projects | ConvertTo-Json -Compress)"
    }

    if (@($projects.projectCodes).Count -ne 0 -or $projects.inactiveProjectCodes -notcontains "P1000" -or $projects.inactiveProjectCodes -notcontains "P2000" -or $projects.inactiveProjectCodes -notcontains "P3000") {
        throw "Monthly-only projects should stay out of the active list and appear inactive: $($projects | ConvertTo-Json -Compress)"
    }

    "All monthly-flow SQLite import checks passed."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
