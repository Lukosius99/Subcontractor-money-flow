$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/contract-import.db"
$baseUrl = "http://localhost:5091"

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

function New-ContractJson($tetasAmount) {
    @{
        sourceSystem = "DynamicsAX2009"
        exportedAt = "2026-05-18T10:30:00Z"
        rows = @(
            @{
                projectCode = "P1730-01"
                objectNumber = "001"
                subcontractorName = "KRS"
                contractedAmount = 60000
            },
            @{
                projectCode = "P1730-01"
                objectNumber = "002"
                subcontractorName = "TETas"
                contractedAmount = $tetasAmount
            },
            @{
                projectCode = "P1730-01"
                objectNumber = "003"
                subcontractorName = "  Alpha   Build  "
                contractedAmount = 100000
            },
            @{
                projectCode = "P1730-01"
                objectNumber = "004"
                subcontractorName = "Delta"
                contractedAmount = 20000
            },
            @{
                projectCode = "P1730-01"
                objectNumber = "005"
                subcontractorName = "Beta Works"
                contractedAmount = 10000
            }
        )
    } | ConvertTo-Json -Depth 8
}

function New-ContractJsonV13() {
    @{
        schemaVersion = "1.3"
        sourceSystem = "SharePointContractedExcel"
        sourceSheets = @("kontraktai", "P verte")
        contractRowCount = 1
        projectValueRowCount = 1
        rows = @(
            @{
                rowType = "subcontractorContract"
                projectCode = "P1706"
                projectName = $null
                objectNumber = "P1706-01"
                objectPrintCode = "P1706 TSP-01"
                departmentCode = "TSP"
                objectIndex = "01"
                subcontractorName = "Sparkus UAB"
                contractedAmount = 129000
                projectStatus = "Statybos"
                sourceRowCount = 1
                sourceRows = @(768)
            },
            @{
                rowType = "subcontractorContract"
                projectCode = "P1737"
                projectName = $null
                objectNumber = "P1737-1"
                objectPrintCode = "P1737 KTP-1"
                departmentCode = "KTP"
                objectIndex = "1"
                subcontractorName = "Sparkus UAB"
                contractedAmount = 55644.88
                projectStatus = "Statybos"
                sourceRowCount = 1
                sourceRows = @(769)
            },
            @{
                rowType = "subcontractorContract"
                projectCode = "P1677"
                projectName = $null
                objectNumber = "P1677-01"
                objectPrintCode = "P1677 KTP5-01"
                departmentCode = "KTP5"
                objectIndex = "01"
                subcontractorName = "Road Works UAB"
                contractedAmount = 100000
                projectStatus = "Statybos"
                sourceRowCount = 1
                sourceRows = @(770)
            }
        )
        projectValueRows = @(
            @{
                rowType = "projectValue"
                projectCode = "P1706"
                objectNumber = "P1706-01"
                objectPrintCode = "P1706 TSP-01"
                departmentCode = "TSP"
                objectIndex = "01"
                projectValueAmount = 3633928.47
                sourceRowCount = 1
                sourceRows = @(42)
            },
            @{
                rowType = "projectValue"
                projectCode = "P1737"
                objectNumber = "P1737-1"
                objectPrintCode = "P1737 KTP-1"
                departmentCode = "KTP"
                objectIndex = "1"
                projectValueAmount = 458220.51
                sourceRowCount = 1
                sourceRows = @(43)
            },
            @{
                rowType = "projectValue"
                projectCode = "P1677"
                objectNumber = "P1677-01"
                objectPrintCode = "P1677 KTP5-01"
                departmentCode = "KTP5"
                objectIndex = "01"
                projectValueAmount = 777000
                sourceRowCount = 1
                sourceRows = @(44)
            }
        )
        warnings = @()
    } | ConvertTo-Json -Depth 8
}

function New-SmdMonthlyJson() {
    @{
        schemaVersion = "1.4"
        year = 2026
        month = 5
        sheetName = "May"
        rows = @(
            @{
                sourceSheet = "SMD"
                sourceRow = 15
                projectCode = "P1706-01"
                objectNumber = "P1706-01"
                subcontractorName = "UAB Kelio uzsakovas"
                objectName = "SMD object"
                amountWithoutVat = 45000
            },
            @{
                sourceSheet = "SMD"
                sourceRow = 16
                projectCode = "P1706-01"
                objectNumber = "P1706-01"
                subcontractorName = "UAB Kelio uzsakovas"
                objectName = "SMD object"
                amountWithoutVat = 5000
            },
            @{
                sourceSheet = "subranga"
                sourceRow = 17
                projectCode = "P1706-01"
                objectNumber = "P1706-01"
                subcontractorName = "Sparkus UAB"
                objectName = "Subcontractor object"
                amountWithoutVat = 10000
            },
            @{
                sourceSheet = "SMD"
                sourceRow = 60
                projectCode = "P1737-1"
                objectNumber = "P1737-1"
                subcontractorName = "AB `"VIA LIETUVA`""
                objectName = "Client object"
                amountWithoutVat = 458220.51
            },
            @{
                sourceSheet = "SMD"
                sourceRow = 61
                projectCode = "AB `"VIA LIETUVA`""
                objectNumber = "P1677-01"
                subcontractorName = "KTP5"
                objectName = "Legacy SMD object"
                amountWithoutVat = 125000
            }
        )
        warnings = @()
    } | ConvertTo-Json -Depth 8
}

$monthlyJson = @{
    schemaVersion = "1.3"
    year = 2026
    month = 4
    sheetName = "April"
    rows = @(
        @{
            sourceRow = 5
            projectCode = "P1730-01"
            projectName = "Monthly Project Name"
            objectNumber = "001"
            subcontractorName = " krs "
            objectName = "Laikinas dangos zenklinimas from monthly"
            amountWithoutVat = 30000
            responsible = "J. Bielevicius"
            engineer = "P. Verbickas"
        },
        @{
            sourceRow = 6
            projectCode = "P1730-01"
            objectNumber = "002"
            subcontractorName = "TETas"
            objectName = "Monthly object name can differ"
            amountWithoutVat = 4
        },
        @{
            sourceRow = 7
            projectCode = "P1730-01"
            objectNumber = "003"
            subcontractorName = "Alpha Build"
            objectName = "Road`r`nworks"
            amountWithoutVat = 95000
        },
        @{
            sourceRow = 8
            projectCode = "P1730-01"
            objectNumber = "004"
            subcontractorName = "Delta"
            objectName = "Drainage"
            amountWithoutVat = 25000
        },
        @{
            sourceRow = 9
            projectCode = "P1730-01"
            objectNumber = "005"
            subcontractorName = "Beta Works"
            objectName = "Lighting full text"
            amountWithoutVat = 10000
        },
        @{
            sourceRow = 10
            projectCode = "P1730-01"
            subcontractorName = "No Object Number Co"
            objectName = "Imported row without object number"
            amountWithoutVat = 1250
        }
    )
    warnings = @()
} | ConvertTo-Json -Depth 8

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

    $firstContract = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson 4)
    if (-not $firstContract.imported -or $firstContract.projectsCreated -ne 1 -or $firstContract.contractsInserted -ne 5 -or $firstContract.contractsUpdated -ne 0) {
        throw "AX-style contract import should insert 5 rows: $($firstContract | ConvertTo-Json -Compress)"
    }

    $secondContract = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson 4)
    if (-not $secondContract.imported -or $secondContract.projectsCreated -ne 0 -or $secondContract.contractsInserted -ne 0 -or $secondContract.contractsUpdated -ne 5) {
        throw "Repeated AX-style contract import should update 5 rows and insert 0: $($secondContract | ConvertTo-Json -Compress)"
    }

    $contractV13 = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJsonV13)
    if (-not $contractV13.imported -or $contractV13.contractsInserted -ne 3 -or $contractV13.projectValuesInserted -ne 3) {
        throw "Contracted schema 1.3 should import one subcontractor contract and one project value row: $($contractV13 | ConvertTo-Json -Compress)"
    }

    $duplicateContractV13 = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJsonV13)
    if (-not $duplicateContractV13.imported -or $duplicateContractV13.contractsInserted -ne 0 -or $duplicateContractV13.projectValuesInserted -ne 0 -or $duplicateContractV13.projectValuesUpdated -ne 3) {
        throw "Repeated schema 1.3 contracted import should update the project value row, not duplicate it: $($duplicateContractV13 | ConvertTo-Json -Compress)"
    }

    $smdMonthly = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=smd-customer-test.json" -ContentType "application/json" -Body (New-SmdMonthlyJson)
    if (-not $smdMonthly.imported -or $smdMonthly.rowsInserted -ne 4 -or $smdMonthly.rowsReceived -ne 5) {
        throw "SMD + subranga monthly import failed: $($smdMonthly | ConvertTo-Json -Compress)"
    }

    $project1706 = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1706/monthly-flow"
    $projectValues = @($project1706.projectObjectValues)
    if ($projectValues.Count -ne 1 -or $projectValues[0].objectNumber -ne "P1706-01" -or [decimal]$projectValues[0].projectValueAmount -ne [decimal]3633928.47) {
        throw "Project/object value row was not exposed on detail page payload: $($project1706 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $smdRows = @($project1706.smdCustomerRows)
    if ($smdRows.Count -ne 1 -or $smdRows[0].objectNumber -ne "P1706-01" -or $smdRows[0].customerName -ne "Kelio uzsakovas" -or [decimal]$smdRows[0].clientMonthlyAmount -ne [decimal]50000 -or [decimal]$smdRows[0].totalYtdAmount -ne [decimal]50000) {
        throw "SMD customer row should be exposed separately and matched by objectNumber: $($project1706 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $project1706ContractRows = @($project1706.contractRows)
    if ($project1706ContractRows.Count -ne 1 -or $project1706ContractRows[0].subcontractorName -ne "UAB Sparkus" -or [decimal]$project1706ContractRows[0].invoiced -ne [decimal]10000) {
        throw "SMD customer row must not appear as a missing subcontractor contract: $($project1706ContractRows | ConvertTo-Json -Depth 8 -Compress)"
    }

    $project1737 = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1737/monthly-flow?objectNumber=P1737-1"
    $sparkus = @(@($project1737.contractRows) | Where-Object { $_.subcontractorName -eq "UAB Sparkus" })
    if ($sparkus.Count -ne 1 -or [decimal]$sparkus[0].contracted -ne [decimal]55644.88 -or [decimal]$sparkus[0].invoiced -ne [decimal]0 -or [decimal]$sparkus[0].remaining -ne [decimal]55644.88) {
        throw "P1737 Sparkus subcontractor row must stay unchanged by SMD client value: $($project1737 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $p1737Smd = @($project1737.smdCustomerRows)
    if ($p1737Smd.Count -ne 1 -or $p1737Smd[0].objectNumber -ne "P1737-1" -or $p1737Smd[0].customerName -ne 'Via Lietuva' -or [decimal]$p1737Smd[0].clientMonthlyAmount -ne [decimal]458220.51 -or $p1737Smd[0].sourceSheet -ne "SMD" -or @($p1737Smd[0].sourceRows)[0] -ne 60) {
        throw "P1737 SMD client value should render as a separate business row with source SMD #60: $($project1737 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $project1677 = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1677/monthly-flow?objectNumber=P1677-01"
    $p1677Smd = @($project1677.smdCustomerRows)
    if ($p1677Smd.Count -ne 1 -or $p1677Smd[0].objectNumber -ne "P1677-01" -or $p1677Smd[0].customerName -ne 'Via Lietuva' -or [decimal]$p1677Smd[0].clientMonthlyAmount -ne [decimal]125000) {
        throw "Legacy SMD mapping should use projectCode as client when subcontractorName is a department code: $($project1677 | ConvertTo-Json -Depth 8 -Compress)"
    }

    $p1677ContractRows = @($project1677.contractRows)
    if ($p1677ContractRows.Count -ne 1 -or $p1677ContractRows[0].subcontractorName -ne "UAB Road Works" -or [decimal]$p1677ContractRows[0].invoiced -ne [decimal]0) {
        throw "Legacy SMD rows must not create fake subcontractor or missing-contract rows: $($p1677ContractRows | ConvertTo-Json -Depth 8 -Compress)"
    }

    $monthly = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=contract-object-number-test.json" -ContentType "application/json" -Body $monthlyJson
    if (-not $monthly.imported -or $monthly.rowsInserted -ne 6) {
        throw "Existing monthly import endpoint failed with optional objectNumber: $($monthly | ConvertTo-Json -Compress)"
    }

    $project = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1730-01/monthly-flow"
    $contractRows = @($project.contractRows)
    if ($contractRows.Count -ne 6) {
        throw "Expected five contract rows and one missing-object-number row: $($project | ConvertTo-Json -Depth 8 -Compress)"
    }

    $krs = $contractRows | Where-Object { $_.objectNumber -eq "001" -and $_.subcontractorName -eq "KRS" }
    if ([decimal]$krs.contracted -ne [decimal]60000 -or [decimal]$krs.invoiced -ne [decimal]30000 -or [decimal]$krs.remaining -ne [decimal]30000 -or [decimal]$krs.usagePercent -ne [decimal]50 -or $krs.status -ne "Pagal planą" -or $krs.objectName -ne "Laikinas dangos zenklinimas from monthly") {
        throw "Object-number matched KRS calculations were incorrect: $($krs | ConvertTo-Json -Depth 8 -Compress)"
    }

    if ($project.projectName -ne "Monthly Project Name") {
        throw "Monthly import should update blank project display name: $($project | ConvertTo-Json -Depth 8 -Compress)"
    }

    $near = $contractRows | Where-Object { $_.objectNumber -eq "003" }
    if ([decimal]$near.remaining -ne [decimal]5000 -or [decimal]$near.usagePercent -ne [decimal]95 -or $near.status -ne "Artėja prie ribos") {
        throw "Near-limit row was incorrect: $($near | ConvertTo-Json -Depth 8 -Compress)"
    }

    $over = $contractRows | Where-Object { $_.objectNumber -eq "004" }
    if ([decimal]$over.remaining -ne [decimal](-5000) -or [decimal]$over.usagePercent -ne [decimal]125 -or $over.status -ne "Viršyta riba") {
        throw "Over-limit row should show negative remaining and Over limit status: $($over | ConvertTo-Json -Depth 8 -Compress)"
    }

    $atLimit = $contractRows | Where-Object { $_.objectNumber -eq "005" }
    if ([decimal]$atLimit.remaining -ne [decimal]0 -or [decimal]$atLimit.usagePercent -ne [decimal]100 -or $atLimit.status -ne "Pasiekta riba") {
        throw "At-limit row was incorrect: $($atLimit | ConvertTo-Json -Depth 8 -Compress)"
    }

    $missingObjectNumber = $contractRows | Where-Object { $_.subcontractorName -eq "No Object Number Co" }
    if (-not $missingObjectNumber.isImportedOnly -or $missingObjectNumber.status -ne "Trūksta sutarties" -or $missingObjectNumber.warning -ne "Trūksta objekto numerio" -or [decimal]$missingObjectNumber.invoiced -ne [decimal]1250) {
        throw "Monthly row without objectNumber should import but show missing object number: $($missingObjectNumber | ConvertTo-Json -Depth 8 -Compress)"
    }

    $duplicateMonthly = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/monthly-flow?sourceFileName=contract-object-number-test.json" -ContentType "application/json" -Body $monthlyJson
    if ($duplicateMonthly.imported -or $duplicateMonthly.reason -ne "Duplicate import") {
        throw "Re-importing identical monthly data should be detected as duplicate: $($duplicateMonthly | ConvertTo-Json -Compress)"
    }

    $projectAfterDuplicateMonthly = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1730-01/monthly-flow"
    if (@($projectAfterDuplicateMonthly.contractRows).Count -ne 6) {
        throw "Re-importing monthly data should not duplicate logical rows: $($projectAfterDuplicateMonthly | ConvertTo-Json -Depth 8 -Compress)"
    }

    $tetasBeforeUpdate = $contractRows | Where-Object { $_.objectNumber -eq "002" }
    if ([decimal]$tetasBeforeUpdate.contracted -ne [decimal]4 -or [decimal]$tetasBeforeUpdate.invoiced -ne [decimal]4 -or [decimal]$tetasBeforeUpdate.remaining -ne [decimal]0 -or [decimal]$tetasBeforeUpdate.usagePercent -ne [decimal]100 -or $tetasBeforeUpdate.status -ne "Pasiekta riba") {
        throw "TETas baseline contract detail was incorrect: $($tetasBeforeUpdate | ConvertTo-Json -Depth 8 -Compress)"
    }

    $tetasUpdate = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" -Body (New-ContractJson 40)
    if (-not $tetasUpdate.imported -or $tetasUpdate.contractsInserted -ne 0 -or $tetasUpdate.contractsUpdated -ne 5) {
        throw "TETas contract update should update existing object-number rows and insert 0: $($tetasUpdate | ConvertTo-Json -Compress)"
    }

    $projectAfterTetasUpdate = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1730-01/monthly-flow"
    $tetasAfterUpdate = @($projectAfterTetasUpdate.contractRows) | Where-Object { $_.objectNumber -eq "002" }
    if ([decimal]$tetasAfterUpdate.contracted -ne [decimal]40 -or [decimal]$tetasAfterUpdate.invoiced -ne [decimal]4 -or [decimal]$tetasAfterUpdate.remaining -ne [decimal]36 -or [decimal]$tetasAfterUpdate.usagePercent -ne [decimal]10 -or $tetasAfterUpdate.status -ne "Pagal planą") {
        throw "Project detail did not reflect TETas object-number contract update from 4 to 40: $($tetasAfterUpdate | ConvertTo-Json -Depth 8 -Compress)"
    }

    $projectAfterDynamicsUpdate = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/projects/P1730-01/monthly-flow"
    $krsAfterDynamicsUpdate = @($projectAfterDynamicsUpdate.contractRows) | Where-Object { $_.objectNumber -eq "001" }
    if ($krsAfterDynamicsUpdate.objectName -ne "Laikinas dangos zenklinimas from monthly" -or $projectAfterDynamicsUpdate.projectName -ne "Monthly Project Name") {
        throw "Dynamics re-import must not clear monthly display fields: $($projectAfterDynamicsUpdate | ConvertTo-Json -Depth 8 -Compress)"
    }

    "Contract import integration checks passed."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
