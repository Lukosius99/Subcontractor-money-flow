$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/db-backup.db"
$backupsDir = Join-Path $projectRoot "test-data/Backups"
$encryptedPath = Join-Path $projectRoot "test-data/db-backup-test.mfbackup"
$decryptedPath = Join-Path $projectRoot "test-data/db-backup-decrypted.db"
$baseUrl = "http://localhost:5097"

if (Test-Path $dbPath) {
    Remove-Item -LiteralPath $dbPath -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null
if (Test-Path $backupsDir) {
    Remove-Item -LiteralPath $backupsDir -Recurse -Force
}

$contractJson = @{
    sourceSystem = "PADContractFlow"
    exportedAt = "2026-05-18T10:30:00Z"
    rows = @(
        @{
            projectCode = "P9001-01"
            objectNumber = "001"
            subcontractorName = "Backup Test UAB"
            contractedAmount = 12345
        }
    )
} | ConvertTo-Json -Depth 8

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = "test-api-key"
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

    $readiness = Invoke-RestMethod -Uri "$baseUrl/ready"
    if ($readiness.status -ne "ready") {
        throw "Readiness endpoint did not validate the database"
    }

    $import = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/imports/contracts" -ContentType "application/json" `
        -Headers @{ "X-Api-Key" = $env:MONEY_FLOW_API_KEY } -Body $contractJson
    if (-not $import.imported) {
        throw "Seed contract import failed: $($import | ConvertTo-Json -Compress)"
    }

    # Backup without an API key must be rejected.
    $unauthorized = $false
    try {
        Invoke-RestMethod -Method Post -Uri "$baseUrl/api/maintenance/db-backup" | Out-Null
    } catch {
        if ([int]$_.Exception.Response.StatusCode -eq 401) { $unauthorized = $true }
    }
    if (-not $unauthorized) {
        throw "POST /api/maintenance/db-backup without X-Api-Key should return 401"
    }

    # Backup with the key must produce a consistent SQLite copy next to the DB.
    $backup = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/maintenance/db-backup" `
        -Headers @{ "X-Api-Key" = $env:MONEY_FLOW_API_KEY }
    if (-not $backup.backedUp -or -not $backup.fileName -or $backup.sizeBytes -le 0) {
        throw "Backup response is incomplete: $($backup | ConvertTo-Json -Compress)"
    }
    if (-not (Test-Path -LiteralPath $backup.fullPath -PathType Leaf)) {
        throw "Backup file was not created at $($backup.fullPath)"
    }
    if ((Split-Path -Parent $backup.fullPath) -ne (Resolve-Path $backupsDir).Path) {
        throw "Backup landed outside the expected Backups directory: $($backup.fullPath)"
    }

    $header = [System.Text.Encoding]::ASCII.GetString((Get-Content -LiteralPath $backup.fullPath -Encoding Byte -TotalCount 15))
    if ($header -ne "SQLite format 3") {
        throw "Backup file is not a valid SQLite database (header: '$header')"
    }

    # A second backup in the same second must not collide with the first.
    $secondBackup = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/maintenance/db-backup" `
        -Headers @{ "X-Api-Key" = $env:MONEY_FLOW_API_KEY }
    if ($secondBackup.fileName -eq $backup.fileName) {
        throw "Second backup reused the same file name: $($secondBackup.fileName)"
    }

    # Encrypted GitHub artifact must round-trip without changing the SQLite file.
    $tool = Join-Path $projectRoot "bin/Debug/net10.0/PADS.MoneyFlow.Api.exe"
    if (-not (Test-Path $tool)) { throw "DB tool was not built at $tool" }
    $env:MONEY_FLOW_BACKUP_PASSPHRASE = "integration-test-passphrase-32-chars"
    & $tool --encrypt-db $backup.fullPath $encryptedPath
    if ($LASTEXITCODE -ne 0) { throw "Backup encryption CLI failed" }
    & $tool --decrypt-db $encryptedPath $decryptedPath
    if ($LASTEXITCODE -ne 0) { throw "Backup decryption CLI failed" }
    & $tool --validate-db $decryptedPath
    if ($LASTEXITCODE -ne 0) { throw "Decrypted DB integrity validation failed" }
    if ((Get-FileHash $backup.fullPath).Hash -ne (Get-FileHash $decryptedPath).Hash) {
        throw "Encrypted backup round-trip changed the DB contents"
    }

    Write-Host "db-backup.ps1 PASSED" -ForegroundColor Green
} finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        Start-Sleep -Milliseconds 500
    }
    Remove-Item Env:MONEY_FLOW_DB_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:MONEY_FLOW_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:MONEY_FLOW_BACKUP_PASSPHRASE -ErrorAction SilentlyContinue
    if (Test-Path $dbPath) { Remove-Item -LiteralPath $dbPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $backupsDir) { Remove-Item -LiteralPath $backupsDir -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $encryptedPath, $decryptedPath -Force -ErrorAction SilentlyContinue
}
