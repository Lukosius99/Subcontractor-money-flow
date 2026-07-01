$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/subcontractor-name-matching.db"
$baseUrl = "http://localhost:5093"

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

# Lithuanian quote characters (built from code points so the file's encoding
# can never corrupt them): „ = U+201E, " = U+201C, " = U+201D.
$qOpen = [char]0x201E
$qClose = [char]0x201D

# Monthly source writes the SAME companies differently to the contracted source.
$stakordaMonthly = "UAB ${qOpen}Stakorda${qClose}"          # contracted: "Stakorda UAB"
$globalitaMonthly = "UAB ${qOpen}Globalita projects${qClose}" # contracted: "Globalita projects UAB"
$viaMonthly = "AB `"Via Lietuva`" | TKTP2"                    # contracted: "AB `"Via Lietuva`""
$arboraMonthly = "Arbora LT,UAB"                              # contracted: "Arbora LT UAB"
$infraMonthly = 'MB"4infra.LT"'                               # contracted: "MB 4infra.LT"
$infraDifferentMonthly = "UAB 4infra.LT"                       # must NOT merge with MB 4infra.LT
$triaMonthly = "Tria maria UAB"                                # contracted: UAB "Tria maria"
$fimaMonthly = "Fima UAB"                                      # must NOT merge with UAB "Fima ITS"

# Real-world convention (verified against the live DB):
#   - Contracted source (Dynamics AX) stores the PROJECT PREFIX in projectCode
#     ("P1900") and the full object code in objectNumber ("P1900-01").
#   - Monthly source stores the full object code in BOTH projectCode and
#     objectNumber ("P1900-01"). All subcontractors sit on the same object.
$contractJson = @{
    sourceSystem = "DynamicsAX2009"
    exportedAt = "2026-05-18T10:30:00Z"
    rows = @(
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "Stakorda UAB"; contractedAmount = 50000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "Globalita projects UAB"; contractedAmount = 80000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "AB `"Via Lietuva`""; contractedAmount = 100000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "Arbora LT UAB"; contractedAmount = 10000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "4infraLT MB"; contractedAmount = 7000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "UAB 4infra.LT"; contractedAmount = 9000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "UAB `"Tria maria`""; contractedAmount = 11000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "UAB `"Fima ITS`""; contractedAmount = 12000 },
        @{ projectCode = "P1900"; objectNumber = "P1900-01"; subcontractorName = "Fima UAB"; contractedAmount = 13000 }
    )
} | ConvertTo-Json -Depth 8

$monthlyJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    sheetName = "April"
    rows = @(
        @{ sourceRow = 5; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $stakordaMonthly; objectName = "Marking"; amountWithoutVat = 20000 },
        @{ sourceRow = 6; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $globalitaMonthly; objectName = "Drainage"; amountWithoutVat = 30000 },
        @{ sourceRow = 7; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $viaMonthly; objectName = "Lighting"; amountWithoutVat = 40000 },
        @{ sourceRow = 8; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $arboraMonthly; objectName = "Earthworks"; amountWithoutVat = 5000 },
        @{ sourceRow = 9; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $infraMonthly; objectName = "Design"; amountWithoutVat = 3000 },
        @{ sourceRow = 10; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $infraDifferentMonthly; objectName = "Design"; amountWithoutVat = 4000 },
        @{ sourceRow = 11; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $triaMonthly; objectName = "Design"; amountWithoutVat = 6000 },
        @{ sourceRow = 12; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = $fimaMonthly; objectName = "Design"; amountWithoutVat = 7000 },
        @{ sourceRow = 13; projectCode = "P1900-01"; objectNumber = "P1900-01"; subcontractorName = "UAB `"Fima ITS`""; objectName = "Design"; amountWithoutVat = 8000 }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

# Force Development so the machine-wide Production profile (which hard-binds
# Kestrel to port 5000 and uses the live ProgramData DB) does not hijack the test.
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = "test-api-key"
$PSDefaultParameterValues["Invoke-RestMethod:Headers"] = @{ "X-Api-Key" = $env:MONEY_FLOW_API_KEY }
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

# PowerShell 5.1 does not send a string -Body as UTF-8, which corrupts the
# Lithuanian smart quotes before they reach the API. Send raw UTF-8 bytes so the
# server receives exactly what Power Automate would send in production.
function Invoke-JsonPost($uri, $json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Method Post -Uri $uri -ContentType "application/json; charset=utf-8" -Body $bytes
}

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
    if (-not $ready) { throw "API did not start at $baseUrl" }

    $contract = Invoke-JsonPost "$baseUrl/api/imports/contracts" $contractJson
    if (-not $contract.imported -or $contract.contractsInserted -ne 9) {
        throw "Contract import should insert 9 rows: $($contract | ConvertTo-Json -Compress)"
    }

    $monthly = Invoke-JsonPost "$baseUrl/api/imports/monthly-flow?sourceFileName=subcontractor-name-test.json" $monthlyJson
    if (-not $monthly.imported -or $monthly.rowsInserted -ne 9) {
        throw "Monthly import should insert 9 rows: $($monthly | ConvertTo-Json -Compress)"
    }

    $project = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1900-01/monthly-flow"
    $contractRows = @($project.contractRows)

    # The whole point: contracts + monthly variants must collapse by deterministic
    # LEGALFORM|BASENAME keys, never by raw names or fuzzy word matching.
    # never 8, and none should fall through to "Missing contract".
    if ($contractRows.Count -ne 9) {
        throw "Name variants must match contracts, not create extra rows. Expected 9, got $($contractRows.Count): $($project | ConvertTo-Json -Depth 8 -Compress)"
    }

    $missing = $contractRows | Where-Object { $_.status -eq "Trūksta sutarties" -or $_.isImportedOnly }
    if ($missing) {
        throw "No row should be Missing contract once names are normalized: $($missing | ConvertTo-Json -Depth 8 -Compress)"
    }

    # Each matched row must keep the readable contracted display name and carry the
    # monthly invoiced amount.
    # All four sit on the same object, so identify each matched row by its
    # readable contracted display name (the monthly variant must NOT appear).
    $expected = @(
        @{ name = "UAB Stakorda"; contracted = 50000; invoiced = 20000 },
        @{ name = "UAB Globalita Projects"; contracted = 80000; invoiced = 30000 },
        @{ name = "AB VIA Lietuva"; contracted = 100000; invoiced = 40000 },
        @{ name = "UAB Arbora LT"; contracted = 10000; invoiced = 5000 },
        @{ name = "MB 4infra.LT"; contracted = 7000; invoiced = 3000 },
        @{ name = "UAB 4infra.LT"; contracted = 9000; invoiced = 4000 },
        @{ name = "UAB Tria Maria"; contracted = 11000; invoiced = 6000 },
        @{ name = "UAB Fima ITS"; contracted = 12000; invoiced = 8000 },
        @{ name = "UAB Fima"; contracted = 13000; invoiced = 7000 }
    )
    foreach ($e in $expected) {
        $row = @($contractRows | Where-Object { $_.subcontractorName -eq $e.name })
        if ($row.Count -ne 1) {
            throw "Expected exactly one row displaying contracted name '$($e.name)', got $($row.Count): $($contractRows | ConvertTo-Json -Depth 8 -Compress)"
        }
        if ([decimal]$row[0].contracted -ne [decimal]$e.contracted -or [decimal]$row[0].invoiced -ne [decimal]$e.invoiced) {
            throw "Row '$($e.name)' amounts wrong (contracted=$($row[0].contracted) invoiced=$($row[0].invoiced)): $($row[0] | ConvertTo-Json -Depth 8 -Compress)"
        }
    }

    $diagnostics = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/diagnostics/subcontractors"
    $infraMapping = @($diagnostics.rows | Where-Object { $_.normalizedKey -eq "MB|4INFRA.LT" })
    if ($infraMapping.Count -ne 1 -or $infraMapping[0].canonicalName -ne "MB 4infra.LT" -or @($infraMapping[0].rawNames).Count -lt 2) {
        throw "Diagnostics should expose MB 4infra.LT raw-name aliases under MB|4INFRA.LT: $($diagnostics | ConvertTo-Json -Depth 8 -Compress)"
    }

    # Re-importing the identical monthly file is a duplicate and must not double-count.
    $duplicate = Invoke-JsonPost "$baseUrl/api/imports/monthly-flow?sourceFileName=subcontractor-name-test.json" $monthlyJson
    if ($duplicate.imported -or $duplicate.reason -ne "Duplicate import") {
        throw "Re-importing identical monthly data should be a duplicate: $($duplicate | ConvertTo-Json -Compress)"
    }

    $projectAfter = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1900-01/monthly-flow"
    $rowsAfter = @($projectAfter.contractRows)
    if ($rowsAfter.Count -ne 9) {
        throw "Duplicate re-import must not change row count: got $($rowsAfter.Count)."
    }
    $stakordaAfter = @($rowsAfter | Where-Object { $_.subcontractorName -eq "UAB Stakorda" })[0]
    if ([decimal]$stakordaAfter.invoiced -ne [decimal]20000) {
        throw "Duplicate re-import must not double-count invoiced (expected 20000, got $($stakordaAfter.invoiced))."
    }

    "Subcontractor name matching integration checks passed."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
