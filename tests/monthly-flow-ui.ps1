$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/monthly-flow-ui.db"
$baseUrl = "http://localhost:5089"
$expectedProjectCode = "PUI-01"
$expectedParentProjectCode = "PUI"
$json = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    rows = @(
        @{
            sourceRow = 5
            projectCode = $expectedProjectCode
            subcontractorName = "UI Smoke"
            amountWithoutVat = 123
        }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

$multiObjectJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    rows = @(
        @{
            sourceRow = 6
            projectCode = "PSEL-01"
            objectNumber = "PSEL-01"
            subcontractorName = "Selector One"
            amountWithoutVat = 100
        },
        @{
            sourceRow = 7
            projectCode = "PSEL-02"
            objectNumber = "PSEL-02"
            subcontractorName = "Selector Two"
            amountWithoutVat = 200
        }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

$sharedProjectCodeMultiObjectJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    rows = @(
        @{
            sourceRow = 8
            projectCode = "PBUG-01"
            objectNumber = "PBUG-01"
            subcontractorName = "Bug One"
            amountWithoutVat = 300
        },
        @{
            sourceRow = 9
            projectCode = "PBUG-01"
            objectNumber = "PBUG-02"
            subcontractorName = "Bug Two"
            amountWithoutVat = 400
        }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest -Uri "$baseUrl/" -UseBasicParsing | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $ready) {
        throw "API did not start at $baseUrl"
    }

    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=monthly-flow-ui-test.json" -ContentType "application/json" -Body $json | Out-Null
    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=monthly-flow-selector-test.json" -ContentType "application/json" -Body $multiObjectJson | Out-Null
    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=monthly-flow-shared-code-selector-test.json" -ContentType "application/json" -Body $sharedProjectCodeMultiObjectJson | Out-Null

    $projects = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projects.projectCodes -notcontains $expectedParentProjectCode -or $projects.projectCodes -contains $expectedProjectCode) {
        throw "Project list did not include only the parent project code."
    }

    if ($projects.projectCodes -notcontains "PSEL" -or $projects.projectCodes -contains "PSEL-01" -or $projects.projectCodes -contains "PSEL-02") {
        throw "Project list should show PSEL parent only for multiple objects: $($projects | ConvertTo-Json -Depth 8 -Compress)"
    }

    $selectorProject = @($projects.projects) | Where-Object { $_.projectCode -eq "PSEL" }
    if ($selectorProject.objectCount -ne 2) {
        throw "PSEL should report two objects: $($selectorProject | ConvertTo-Json -Depth 8 -Compress)"
    }

    $sharedCodeProject = @($projects.projects) | Where-Object { $_.projectCode -eq "PBUG" }
    if ($sharedCodeProject.objectCount -ne 2 -or @($sharedCodeProject.objects).Count -ne 2 -or @($sharedCodeProject.objects | Where-Object { $_.objectNumber -in @("PBUG-01", "PBUG-02") }).Count -ne 2) {
        throw "PBUG should count distinct objectNumber values even when projectCode is shared: $($sharedCodeProject | ConvertTo-Json -Depth 8 -Compress)"
    }

    $status = Invoke-RestMethod "$baseUrl/api/imports/monthly-flow/status"
    if ($null -eq $status.latestImport -or @($status.imports | Where-Object { $_.sourceFileName -eq "monthly-flow-ui-test.json" }).Count -ne 1) {
        throw "Import status endpoint returned unexpected data: $($status | ConvertTo-Json -Depth 8 -Compress)"
    }

    $index = Invoke-WebRequest -Uri "$baseUrl/" -UseBasicParsing
    if ($index.Content -notmatch "Projects" -or $index.Content -notmatch "importLine") {
        throw "Projects page did not load expected HTML."
    }

    $detail = Invoke-WebRequest -Uri "$baseUrl/project.html?projectCode=$expectedProjectCode" -UseBasicParsing
    if ($detail.Content -notmatch "Project detail") {
        throw "Project detail page did not load expected HTML."
    }

    $monthlyFlow = Invoke-RestMethod "$baseUrl/api/projects/$expectedParentProjectCode/monthly-flow?objectNumber=$expectedProjectCode"
    if ($monthlyFlow.groups.Count -lt 1 -or $null -eq $monthlyFlow.totals) {
        throw "Monthly flow endpoint returned no groups."
    }

    $allObjectsFlow = Invoke-RestMethod "$baseUrl/api/projects/PSEL/monthly-flow"
    if (-not $allObjectsFlow.isAllObjects -or @($allObjectsFlow.objects).Count -ne 2 -or [decimal]$allObjectsFlow.totals.amountWithoutVat -ne [decimal]300) {
        throw "All-objects project detail did not combine PSEL objects: $($allObjectsFlow | ConvertTo-Json -Depth 8 -Compress)"
    }

    $singleObjectFlow = Invoke-RestMethod "$baseUrl/api/projects/PSEL/monthly-flow?objectNumber=PSEL-01"
    if ($singleObjectFlow.isAllObjects -or [decimal]$singleObjectFlow.totals.amountWithoutVat -ne [decimal]100 -or @($singleObjectFlow.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ } | Where-Object { $_.projectCode -ne "PSEL-01" }).Count -ne 0) {
        throw "Single-object project detail did not filter to PSEL-01: $($singleObjectFlow | ConvertTo-Json -Depth 8 -Compress)"
    }

    $sharedCodeAllObjectsFlow = Invoke-RestMethod "$baseUrl/api/projects/PBUG/monthly-flow"
    if (-not $sharedCodeAllObjectsFlow.isAllObjects -or @($sharedCodeAllObjectsFlow.objects).Count -ne 2 -or [decimal]$sharedCodeAllObjectsFlow.totals.amountWithoutVat -ne [decimal]700) {
        throw "All-objects project detail did not combine PBUG objectNumbers: $($sharedCodeAllObjectsFlow | ConvertTo-Json -Depth 8 -Compress)"
    }

    $sharedCodeSingleObjectFlow = Invoke-RestMethod "$baseUrl/api/projects/PBUG/monthly-flow?objectNumber=PBUG-02"
    $sharedCodeSingleRows = @($sharedCodeSingleObjectFlow.groups | ForEach-Object { $_.rows } | ForEach-Object { $_ })
    if ($sharedCodeSingleObjectFlow.isAllObjects -or [decimal]$sharedCodeSingleObjectFlow.totals.amountWithoutVat -ne [decimal]400 -or @($sharedCodeSingleRows | Where-Object { $_.objectNumber -ne "PBUG-02" }).Count -ne 0) {
        throw "Single-object project detail did not filter to PBUG-02 objectNumber: $($sharedCodeSingleObjectFlow | ConvertTo-Json -Depth 8 -Compress)"
    }

    "Monthly-flow UI smoke checks passed."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
