$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/manual-contract-links.db"
$baseUrl = "http://localhost:5094"

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

# Scenario from production data: contract is registered as "MB Energostatas",
# but the monthly source file was filled in with "UAB Energostatas". Legal form
# is part of the matching key on purpose, so the rows do NOT merge until a user
# manually connects them.
$contractJson = @{
    sourceSystem = "DynamicsAX2009"
    exportedAt = "2026-05-18T10:30:00Z"
    rows = @(
        @{ projectCode = "P1578"; objectNumber = "P1578-01"; subcontractorName = "MB Energostatas"; contractedAmount = 28000 },
        @{ projectCode = "P1578"; objectNumber = "P1578-01"; subcontractorName = "UAB Aedilis"; contractedAmount = 5000 },
        @{ projectCode = "P1578"; objectNumber = "P1578-02"; subcontractorName = "MB Kitas Objektas"; contractedAmount = 9000 }
    )
} | ConvertTo-Json -Depth 8

$monthlyAprilJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    sheetName = "April"
    rows = @(
        @{ sourceRow = 5; projectCode = "P1578-01"; objectNumber = "P1578-01"; subcontractorName = "UAB Energostatas"; objectName = "Cabling"; amountWithoutVat = 11800 },
        @{ sourceRow = 6; projectCode = "P1578-01"; objectNumber = "P1578-01"; subcontractorName = "UAB Aedilis"; objectName = "Lighting"; amountWithoutVat = 1000 }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

# Next month the user fixed the source file to use the contracted name.
$monthlyMayJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 5
    sheetName = "May"
    rows = @(
        @{ sourceRow = 5; projectCode = "P1578-01"; objectNumber = "P1578-01"; subcontractorName = "MB Energostatas"; objectName = "Cabling"; amountWithoutVat = 5000 }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

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
    if (-not $contract.imported -or $contract.contractsInserted -ne 3) {
        throw "Contract import should insert 3 rows: $($contract | ConvertTo-Json -Compress)"
    }

    $monthly = Invoke-JsonPost "$baseUrl/api/imports/monthly-flow?sourceFileName=manual-link-april.json" $monthlyAprilJson
    if (-not $monthly.imported -or $monthly.rowsInserted -ne 2) {
        throw "Monthly import should insert 2 rows: $($monthly | ConvertTo-Json -Compress)"
    }

    # Before linking: UAB Energostatas invoices fall through as imported-only.
    $project = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1578-01/monthly-flow"
    $rows = @($project.contractRows)
    if ($rows.Count -ne 3) {
        throw "Expected 3 rows before linking (2 contracts + 1 unmatched), got $($rows.Count): $($rows | ConvertTo-Json -Depth 8 -Compress)"
    }
    $unmatched = @($rows | Where-Object { $_.subcontractorName -eq "UAB Energostatas" -and $_.isImportedOnly })
    if ($unmatched.Count -ne 1) {
        throw "UAB Energostatas should be an unmatched imported-only row before linking."
    }
    $mbRow = @($rows | Where-Object { $_.subcontractorName -eq "MB Energostatas" })[0]
    if (-not $mbRow -or [decimal]$mbRow.invoiced -ne [decimal]0 -or -not $mbRow.rowKey) {
        throw "MB Energostatas contract row should exist with 0 invoiced and a rowKey before linking: $($mbRow | ConvertTo-Json -Compress)"
    }

    # Linking to a non-existing contract row must fail with 400.
    $badRequestFailed = $false
    try {
        Invoke-JsonPost "$baseUrl/api/projects/P1578/contract-links" (@{
            sourceSubcontractorName = "UAB Energostatas"
            sourceObjectNumber = "P1578-01"
            targetContractRowKey = "no-such-row-key"
        } | ConvertTo-Json)
    } catch {
        $badRequestFailed = $true
    }
    if (-not $badRequestFailed) {
        throw "Linking to an unknown contract row key should return 400."
    }

    # Cross-object links must be rejected: the unmatched row sits on P1578-01,
    # the target contract on P1578-02.
    $parentProject = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1578/monthly-flow"
    $otherObjectContract = @($parentProject.contractRows | Where-Object { $_.subcontractorName -eq "MB Kitas Objektas" })[0]
    if (-not $otherObjectContract -or -not $otherObjectContract.rowKey) {
        throw "Expected the P1578-02 contract row with a rowKey in the all-objects view."
    }
    $crossObjectFailed = $false
    try {
        Invoke-JsonPost "$baseUrl/api/projects/P1578/contract-links" (@{
            sourceSubcontractorName = "UAB Energostatas"
            sourceObjectNumber = "P1578-01"
            targetContractRowKey = $otherObjectContract.rowKey
        } | ConvertTo-Json)
    } catch {
        $crossObjectFailed = $true
    }
    if (-not $crossObjectFailed) {
        throw "Linking a P1578-01 row to a P1578-02 contract should be rejected with 400."
    }

    # Omitting the source object number must also be rejected.
    $missingObjectFailed = $false
    try {
        Invoke-JsonPost "$baseUrl/api/projects/P1578/contract-links" (@{
            sourceSubcontractorName = "UAB Energostatas"
            targetContractRowKey = $mbRow.rowKey
        } | ConvertTo-Json)
    } catch {
        $missingObjectFailed = $true
    }
    if (-not $missingObjectFailed) {
        throw "Linking without sourceObjectNumber should be rejected with 400."
    }

    # Create the manual link.
    $link = Invoke-JsonPost "$baseUrl/api/projects/P1578/contract-links" (@{
        sourceSubcontractorName = "UAB Energostatas"
        sourceObjectNumber = "P1578-01"
        targetContractRowKey = $mbRow.rowKey
    } | ConvertTo-Json)
    if (-not $link.linked -or -not $link.id -or $link.targetName -ne "MB Energostatas") {
        throw "Link creation failed: $($link | ConvertTo-Json -Compress)"
    }

    # Creating the same link twice must upsert, not duplicate or fail.
    $linkAgain = Invoke-JsonPost "$baseUrl/api/projects/P1578/contract-links" (@{
        sourceSubcontractorName = "UAB Energostatas"
        sourceObjectNumber = "P1578-01"
        targetContractRowKey = $mbRow.rowKey
    } | ConvertTo-Json)
    if (-not $linkAgain.linked -or $linkAgain.id -ne $link.id) {
        throw "Re-creating the same link should upsert the existing link: $($linkAgain | ConvertTo-Json -Compress)"
    }

    # After linking: invoices merge into the contracted row.
    $projectLinked = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1578-01/monthly-flow"
    $rowsLinked = @($projectLinked.contractRows)
    if ($rowsLinked.Count -ne 2) {
        throw "Expected 2 rows after linking, got $($rowsLinked.Count): $($rowsLinked | ConvertTo-Json -Depth 8 -Compress)"
    }
    $mbLinked = @($rowsLinked | Where-Object { $_.subcontractorName -eq "MB Energostatas" })[0]
    if ([decimal]$mbLinked.invoiced -ne [decimal]11800 -or $mbLinked.isImportedOnly) {
        throw "MB Energostatas should carry the linked 11800 invoiced: $($mbLinked | ConvertTo-Json -Compress)"
    }
    if (@($mbLinked.links).Count -ne 1 -or $mbLinked.links[0].sourceName -ne "UAB Energostatas") {
        throw "MB Energostatas row should expose the manual link metadata: $($mbLinked.links | ConvertTo-Json -Compress)"
    }
    $aedilis = @($rowsLinked | Where-Object { $_.subcontractorName -eq "UAB Aedilis" })[0]
    if (@($aedilis.links).Count -ne 0) {
        throw "UAB Aedilis must not pick up link metadata: $($aedilis.links | ConvertTo-Json -Compress)"
    }

    # Next month the source file uses the contracted name: it must keep matching
    # directly while the old linked month stays merged.
    $monthlyMay = Invoke-JsonPost "$baseUrl/api/imports/monthly-flow?sourceFileName=manual-link-may.json" $monthlyMayJson
    if (-not $monthlyMay.imported -or $monthlyMay.rowsInserted -ne 1) {
        throw "May import should insert 1 row: $($monthlyMay | ConvertTo-Json -Compress)"
    }
    $projectMay = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1578-01/monthly-flow"
    $rowsMay = @($projectMay.contractRows)
    if ($rowsMay.Count -ne 2) {
        throw "Fixed-name import must not create extra rows: got $($rowsMay.Count)."
    }
    $mbMay = @($rowsMay | Where-Object { $_.subcontractorName -eq "MB Energostatas" })[0]
    if ([decimal]$mbMay.invoiced -ne [decimal]16800) {
        throw "MB Energostatas should total 16800 (11800 linked + 5000 direct), got $($mbMay.invoiced)."
    }

    # Removing the link restores the unmatched row but keeps the direct match.
    $delete = Invoke-WebRequest -Method Delete -Uri "$baseUrl/api/projects/P1578/contract-links/$($link.id)" -UseBasicParsing
    if ($delete.StatusCode -ne 204) {
        throw "Link delete should return 204, got $($delete.StatusCode)."
    }
    $projectUnlinked = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1578-01/monthly-flow"
    $rowsUnlinked = @($projectUnlinked.contractRows)
    if ($rowsUnlinked.Count -ne 3) {
        throw "Expected 3 rows after unlinking, got $($rowsUnlinked.Count)."
    }
    $mbUnlinked = @($rowsUnlinked | Where-Object { $_.subcontractorName -eq "MB Energostatas" })[0]
    if ([decimal]$mbUnlinked.invoiced -ne [decimal]5000) {
        throw "After unlinking MB Energostatas should keep only the direct 5000, got $($mbUnlinked.invoiced)."
    }
    $uabUnlinked = @($rowsUnlinked | Where-Object { $_.subcontractorName -eq "UAB Energostatas" -and $_.isImportedOnly })
    if ($uabUnlinked.Count -ne 1 -or [decimal]$uabUnlinked[0].invoiced -ne [decimal]11800) {
        throw "After unlinking UAB Energostatas should reappear as imported-only with 11800."
    }

    # Deleting an unknown link id returns 404.
    $notFound = $false
    try {
        Invoke-WebRequest -Method Delete -Uri "$baseUrl/api/projects/P1578/contract-links/00000000-0000-0000-0000-000000000000" -UseBasicParsing | Out-Null
    } catch {
        $notFound = $true
    }
    if (-not $notFound) {
        throw "Deleting an unknown link id should return 404."
    }

    "Manual contract link integration checks passed."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
