$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/contract-snapshot.db"
$baseUrl = "http://localhost:5094"

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

function New-ContractJson($rows) {
    @{
        sourceSystem = "DynamicsAX2009"
        exportedAt = "2026-06-03T08:00:00Z"
        rows = $rows
    } | ConvertTo-Json -Depth 8
}

function New-ContractRow($projectCode, $amount) {
    @{
        projectCode = $projectCode
        projectName = "$projectCode contracted project"
        objectNumber = "$projectCode-01"
        objectPrintCode = "$projectCode KTP1-01"
        departmentCode = "KTP1"
        objectIndex = "01"
        subcontractorName = "Snapshot Builder UAB"
        contractedAmount = $amount
        projectStatus = "Statybos"
        sourceRowCount = 1
        sourceRows = @(10)
    }
}

$monthlyJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    sheetName = "April"
    rows = @(
        @{
            sourceRow = 5
            projectCode = "P1677-01"
            projectName = "Monthly P1677"
            objectNumber = "P1677-01"
            subcontractorName = "Snapshot Builder UAB"
            objectName = "Historical monthly row"
            amountWithoutVat = 250
            responsible = "J. Engineer"
            engineer = "P. Control"
        }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = "test-api-key"
$PSDefaultParameterValues["Invoke-RestMethod:Headers"] = @{ "X-Api-Key" = $env:MONEY_FLOW_API_KEY }
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            Invoke-WebRequest -Uri "$baseUrl/api/projects" -UseBasicParsing | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) { throw "API did not start at $baseUrl" }

    $firstContract = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson @(
        (New-ContractRow "P1677" 1000),
        (New-ContractRow "P2000" 2000)
    ))
    if (-not $firstContract.imported -or $firstContract.contractsInserted -ne 2) {
        throw "First contract snapshot should insert two contracts: $($firstContract | ConvertTo-Json -Compress)"
    }

    $projectsAfterFirst = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsAfterFirst.projectCodes -notcontains "P1677") {
        throw "P1677 should be active after first snapshot: $($projectsAfterFirst | ConvertTo-Json -Depth 8 -Compress)"
    }

    $monthly = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=contract-snapshot-monthly.json" -ContentType "application/json" -Body $monthlyJson
    if (-not $monthly.imported -or $monthly.rowsInserted -ne 1) {
        throw "Monthly history import failed: $($monthly | ConvertTo-Json -Compress)"
    }

    $secondContract = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson @(
        (New-ContractRow "P2000" 2500)
    ))
    if (-not $secondContract.imported -or $secondContract.contractsInserted -ne 0 -or $secondContract.contractsUpdated -ne 1) {
        throw "Second snapshot should update P2000 and not insert duplicates: $($secondContract | ConvertTo-Json -Compress)"
    }

    $projectsAfterSecond = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsAfterSecond.projectCodes -contains "P1677") {
        throw "P1677 should leave the active project list after missing from latest snapshot: $($projectsAfterSecond | ConvertTo-Json -Depth 8 -Compress)"
    }
    if (@($projectsAfterSecond.inactiveProjects | Where-Object { $_.projectCode -eq "P1677" }).Count -ne 1) {
        throw "P1677 should appear in inactive projects: $($projectsAfterSecond | ConvertTo-Json -Depth 8 -Compress)"
    }

    $p1677Detail = Invoke-RestMethod "$baseUrl/api/projects/P1677/monthly-flow"
    if (@($p1677Detail.groups).Count -eq 0 -or @($p1677Detail.contractRows).Count -eq 0) {
        throw "Inactive P1677 should keep historical monthly and contract detail: $($p1677Detail | ConvertTo-Json -Depth 8 -Compress)"
    }

    $duplicateSecond = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson @(
        (New-ContractRow "P2000" 2500)
    ))
    if (-not $duplicateSecond.imported -or $duplicateSecond.contractsInserted -ne 0 -or $duplicateSecond.contractsUpdated -ne 1) {
        throw "Repeated contracted snapshot should update existing row only: $($duplicateSecond | ConvertTo-Json -Compress)"
    }

    $emptyContract = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson @())
    if (-not $emptyContract.imported -or $emptyContract.importedRows -ne 0) {
        throw "Empty contracted import should be accepted as a no-op snapshot: $($emptyContract | ConvertTo-Json -Compress)"
    }

    $projectsAfterEmpty = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsAfterEmpty.projectCodes -notcontains "P2000") {
        throw "Empty contracted import must not mark all active projects inactive: $($projectsAfterEmpty | ConvertTo-Json -Depth 8 -Compress)"
    }

    "Contract snapshot integration checks passed."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
