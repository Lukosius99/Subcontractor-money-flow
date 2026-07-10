$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/ignored-monthly-rows.db"
$baseUrl = "http://localhost:5096"
$apiKey = "test-api-key"

function New-ImportJson([decimal]$firstAmount) {
    @{
        schemaVersion = "1.4"
        year = 2026
        month = 6
        sheetName = "subranga"
        rows = @(
            @{ sourceSheet = "subranga"; sourceRow = 11; projectCode = "PIGNORE-01"; objectNumber = "PIGNORE-01"; subcontractorName = "Ignore Me UAB"; amountWithoutVat = $firstAmount },
            @{ sourceSheet = "subranga"; sourceRow = 12; projectCode = "PIGNORE-01"; objectNumber = "PIGNORE-01"; subcontractorName = "Keep Me UAB"; amountWithoutVat = 200 }
        )
        warnings = @()
    } | ConvertTo-Json -Depth 8
}

function Wait-Ready {
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-WebRequest "$baseUrl/api/projects" -UseBasicParsing | Out-Null; return }
        catch { Start-Sleep -Milliseconds 500 }
    }
    throw "API did not start at $baseUrl"
}

function Start-TestServer {
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:MONEY_FLOW_DB_PATH = $dbPath
    $env:MONEY_FLOW_API_KEY = $apiKey
    $process = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden
    Wait-Ready
    return $process
}

if (Test-Path $dbPath) { Remove-Item -LiteralPath $dbPath -Force }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null
$headers = @{ "X-Api-Key" = $apiKey }
$server = $null

try {
    $server = Start-TestServer
    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=ignored-test-1.json" -Headers $headers -ContentType "application/json" -Body (New-ImportJson 100) | Out-Null
    $before = Invoke-RestMethod "$baseUrl/api/projects/PIGNORE/monthly-flow?objectNumber=PIGNORE-01"
    $liveRows = @($before.groups | ForEach-Object rows | ForEach-Object { $_ })
    if ($liveRows.Count -ne 2 -or [decimal]$before.totals.amountWithoutVat -ne 300) { throw "Initial live rows or total were incorrect." }
    $row = $liveRows | Where-Object subcontractorName -eq "Ignore Me UAB"

    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/projects/PIGNORE/monthly-flow/$($row.id)/exclude?objectNumber=PIGNORE-01" -Headers $headers -ContentType "application/json" -Body '{"reason":"Duplicate source row"}' | Out-Null
    $excluded = Invoke-RestMethod "$baseUrl/api/projects/PIGNORE/monthly-flow?objectNumber=PIGNORE-01"
    $excludedLiveRows = @($excluded.groups | ForEach-Object rows | ForEach-Object { $_ })
    if ($excludedLiveRows.id -contains $row.id -or [decimal]$excluded.totals.amountWithoutVat -ne 200 -or $excluded.ignoredRowCount -ne 1) { throw "Ignored row remained live or still affected totals." }

    $ignored = Invoke-RestMethod "$baseUrl/api/projects/PIGNORE/ignored-rows?objectNumber=PIGNORE-01"
    if (@($ignored.rows).Count -ne 1 -or $ignored.rows[0].id -ne $row.id -or $ignored.rows[0].excludedReason -ne "Duplicate source row") { throw "Ignored rows API did not return exclusion metadata." }

    # A later import may update source values, but must not erase the manual exclusion.
    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=ignored-test-2.json" -Headers $headers -ContentType "application/json" -Body (New-ImportJson 150) | Out-Null
    $afterReimport = Invoke-RestMethod "$baseUrl/api/projects/PIGNORE/monthly-flow?objectNumber=PIGNORE-01"
    if ([decimal]$afterReimport.totals.amountWithoutVat -ne 200 -or $afterReimport.ignoredRowCount -ne 1) { throw "Re-import erased the ignored state." }

    # Restart against the same SQLite file to prove the ignored row remains stored.
    Stop-Process -Id $server.Id -Force
    $server.WaitForExit()
    $server = Start-TestServer
    $afterRestart = Invoke-RestMethod "$baseUrl/api/projects/PIGNORE/ignored-rows?objectNumber=PIGNORE-01"
    if (@($afterRestart.rows).Count -ne 1 -or [decimal]$afterRestart.rows[0].amountWithoutVat -ne 150) { throw "Ignored row did not persist in SQLite across restart." }

    Invoke-RestMethod -Method Post -Uri "$baseUrl/api/projects/PIGNORE/monthly-flow/$($row.id)/restore?objectNumber=PIGNORE-01" -Headers $headers | Out-Null
    $restored = Invoke-RestMethod "$baseUrl/api/projects/PIGNORE/monthly-flow?objectNumber=PIGNORE-01"
    $restoredRows = @($restored.groups | ForEach-Object rows | ForEach-Object { $_ })
    if ($restoredRows.id -notcontains $row.id -or [decimal]$restored.totals.amountWithoutVat -ne 350 -or $restored.ignoredRowCount -ne 0) { throw "Restore did not return the row to live totals." }

    # The browser export is built only from the normal monthly-flow payload.
    if ($excludedLiveRows.subcontractorName -contains "Ignore Me UAB") { throw "Default export input included an ignored row." }
    "Ignored monthly row checks passed."
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
