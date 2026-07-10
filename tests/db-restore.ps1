$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot 'test-data/db-restore.db'
$backupsDir = Join-Path $projectRoot 'test-data/Backups'
$encryptedBackup = Join-Path $projectRoot 'test-data/db-restore.mfbackup'
$invalidBackup = Join-Path $projectRoot 'test-data/db-restore-invalid.db'
$baseUrl = 'http://localhost:5103'
$apiKey = 'db-restore-test-api-key'
$passphrase = 'db-restore-integration-passphrase'

function New-ContractJson([string]$ProjectCode, [decimal]$Amount) {
    @{
        sourceSystem = 'PADContractFlow'
        exportedAt = '2026-07-10T08:00:00Z'
        rows = @(
            @{
                projectCode = $ProjectCode
                projectName = "$ProjectCode restore project"
                objectNumber = "$ProjectCode-01"
                subcontractorName = 'Restore Test UAB'
                contractedAmount = $Amount
            }
        )
    } | ConvertTo-Json -Depth 8
}

function Send-RestoreFile([string]$Path, [string]$Key) {
    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "$baseUrl/api/maintenance/db-restore")
    $multipart = [System.Net.Http.MultipartFormDataContent]::new()
    $stream = $null
    $fileContent = $null
    $response = $null
    try {
        if ($Key) { $request.Headers.TryAddWithoutValidation('X-Api-Key', $Key) | Out-Null }
        $stream = [IO.File]::OpenRead($Path)
        $fileContent = [System.Net.Http.StreamContent]::new($stream)
        $multipart.Add($fileContent, 'file', [IO.Path]::GetFileName($Path))
        $request.Content = $multipart
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Body = $body
            Json = if ($body) { $body | ConvertFrom-Json } else { $null }
        }
    }
    finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
        $multipart.Dispose()
        if ($fileContent) { $fileContent.Dispose() }
        if ($stream) { $stream.Dispose() }
        $client.Dispose()
    }
}

if (Test-Path $dbPath) { Remove-Item -LiteralPath $dbPath -Force }
if (Test-Path $backupsDir) { Remove-Item -LiteralPath $backupsDir -Recurse -Force }
if (Test-Path $encryptedBackup) { Remove-Item -LiteralPath $encryptedBackup -Force }
if (Test-Path $invalidBackup) { Remove-Item -LiteralPath $invalidBackup -Force }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = $apiKey
$env:MONEY_FLOW_BACKUP_PASSPHRASE = $passphrase
$server = Start-Process -FilePath 'dotnet' -ArgumentList "run --urls $baseUrl" `
    -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            Invoke-WebRequest -Uri "$baseUrl/api/projects" -UseBasicParsing | Out-Null
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "API did not start at $baseUrl" }

    $headers = @{ 'X-Api-Key' = $apiKey }
    $initial = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" `
        -Headers $headers -ContentType 'application/json' -Body (New-ContractJson 'PRESTORE' 1000)
    if (-not $initial.imported) { throw 'Initial restore test import failed.' }

    $backup = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/maintenance/db-backup" -Headers $headers
    if (-not $backup.backedUp -or -not (Test-Path -LiteralPath $backup.fullPath)) {
        throw 'Restore test backup was not created.'
    }

    $tool = Join-Path $projectRoot 'bin/Debug/net10.0/PADS.MoneyFlow.Api.exe'
    if (-not (Test-Path $tool)) { throw "DB tool was not built at $tool" }
    & $tool --encrypt-db $backup.fullPath $encryptedBackup
    if ($LASTEXITCODE -ne 0) { throw 'Restore test backup encryption failed.' }

    $changed = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" `
        -Headers $headers -ContentType 'application/json' -Body (New-ContractJson 'PCHANGED' 2000)
    if (-not $changed.imported) { throw 'Changed restore test import failed.' }
    $projectsBeforeRestore = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsBeforeRestore.projectCodes -notcontains 'PCHANGED' `
        -or $projectsBeforeRestore.projectCodes -contains 'PRESTORE') {
        throw 'Database did not reach the expected pre-restore state.'
    }

    $unauthorized = Send-RestoreFile -Path $encryptedBackup -Key ''
    if ($unauthorized.Status -ne 401) {
        throw "Restore without API key should return 401, got $($unauthorized.Status)."
    }

    $serverId = $server.Id
    $restored = Send-RestoreFile -Path $encryptedBackup -Key $apiKey
    if ($restored.Status -ne 200 `
        -or -not $restored.Json.restored `
        -or $restored.Json.serviceRestarted) {
        throw "Online restore failed: HTTP $($restored.Status) $($restored.Body)"
    }

    $server.Refresh()
    if ($server.HasExited -or $server.Id -ne $serverId) {
        throw 'Online restore restarted or stopped the service process.'
    }

    $projectsAfterRestore = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsAfterRestore.projectCodes -notcontains 'PRESTORE' `
        -or $projectsAfterRestore.projectCodes -contains 'PCHANGED') {
        throw "Restored database content is incorrect: $($projectsAfterRestore | ConvertTo-Json -Depth 8 -Compress)"
    }

    $readiness = Invoke-RestMethod "$baseUrl/ready"
    if ($readiness.status -ne 'ready') { throw 'Service was not ready after online restore.' }
    if (-not (Test-Path (Join-Path $backupsDir $restored.Json.rollbackFileName))) {
        throw 'Online restore did not retain its rollback copy.'
    }

    # Exercise the actual operator-facing script. It must restore through the
    # running service without elevation or a process restart.
    $changedAgain = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" `
        -Headers $headers -ContentType 'application/json' -Body (New-ContractJson 'PCHANGED2' 3000)
    if (-not $changedAgain.imported) { throw 'Second changed restore test import failed.' }

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'Restore-MoneyFlowDb.ps1') `
        -BackupFile $encryptedBackup -BaseUrl $baseUrl -NoPause -Force
    if ($LASTEXITCODE -ne 0) { throw "Restore-MoneyFlowDb.ps1 returned $LASTEXITCODE." }

    $server.Refresh()
    if ($server.HasExited -or $server.Id -ne $serverId) {
        throw 'Operator restore script restarted or stopped the service process.'
    }
    $projectsAfterScriptRestore = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsAfterScriptRestore.projectCodes -notcontains 'PRESTORE' `
        -or $projectsAfterScriptRestore.projectCodes -contains 'PCHANGED2') {
        throw 'Operator restore script did not restore the expected database content.'
    }

    [IO.File]::WriteAllText($invalidBackup, 'not a sqlite database')
    $invalidRestore = Send-RestoreFile -Path $invalidBackup -Key $apiKey
    if ($invalidRestore.Status -ne 400) {
        throw "Invalid restore file should return 400, got $($invalidRestore.Status)."
    }
    $projectsAfterInvalidRestore = Invoke-RestMethod "$baseUrl/api/projects"
    if ($projectsAfterInvalidRestore.projectCodes -notcontains 'PRESTORE') {
        throw 'Invalid restore request changed the live database.'
    }

    Write-Host 'Online DB restore checks passed.' -ForegroundColor Green
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item Env:MONEY_FLOW_DB_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:MONEY_FLOW_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:MONEY_FLOW_BACKUP_PASSPHRASE -ErrorAction SilentlyContinue
    if (Test-Path $dbPath) { Remove-Item -LiteralPath $dbPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $backupsDir) { Remove-Item -LiteralPath $backupsDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $encryptedBackup) { Remove-Item -LiteralPath $encryptedBackup -Force -ErrorAction SilentlyContinue }
    if (Test-Path $invalidBackup) { Remove-Item -LiteralPath $invalidBackup -Force -ErrorAction SilentlyContinue }
}
