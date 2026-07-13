$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/api-security.db"
$baseUrl = "http://localhost:5091"
$apiKey = "test-api-key-for-mutations"
$editPassphrase = "test-edit-passphrase-for-manual-actions"
$editPassphraseFile = Join-Path $projectRoot "test-data/api-security-edit-passphrase.txt"
$PSDefaultParameterValues["Invoke-WebRequest:TimeoutSec"] = 5

function Write-EditPassphraseHash([string]$Value, [string]$Path) {
    $salt = New-Object byte[] 16
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
    $iterations = 310000
    $derive = [Security.Cryptography.Rfc2898DeriveBytes]::new(
        $Value,
        $salt,
        $iterations,
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $editHash = $derive.GetBytes(32) } finally { $derive.Dispose() }
    try {
        $encodedEditHash = 'PBKDF2-SHA256${0}${1}${2}' -f `
            $iterations,
            [Convert]::ToBase64String($salt),
            [Convert]::ToBase64String($editHash)
        [IO.File]::WriteAllText($Path, $encodedEditHash, [Text.UTF8Encoding]::new($false))
    } finally {
        [Array]::Clear($editHash, 0, $editHash.Length)
    }
}

if (Test-Path $dbPath) { Remove-Item -LiteralPath $dbPath -Force }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null
Write-EditPassphraseHash $editPassphrase $editPassphraseFile

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = $apiKey
$env:MoneyFlow__EditPassphraseFilePath = $editPassphraseFile
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --urls $baseUrl" `
    -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

function Get-HttpStatus([scriptblock]$Request) {
    try {
        $response = & $Request
        return [int]$response.StatusCode
    } catch {
        return [int]$_.Exception.Response.StatusCode
    }
}

try {
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest "$baseUrl/api/projects" -UseBasicParsing | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) { throw "API did not start at $baseUrl" }

    $readStatus = Get-HttpStatus { Invoke-WebRequest "$baseUrl/api/projects" -UseBasicParsing }
    if ($readStatus -ne 200) { throw "Read-only GET should stay open, got $readStatus." }

    foreach ($method in @("POST", "PUT", "PATCH", "DELETE")) {
        $status = Get-HttpStatus {
            Invoke-WebRequest -Method $method -Uri "$baseUrl/api/definitely-missing" -UseBasicParsing
        }
        if ($status -ne 401) {
            throw "$method without X-Api-Key should return 401 before routing, got $status."
        }
    }

    $manualStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/projects/P1/contract-links" `
            -ContentType "application/json" -Body '{}' -UseBasicParsing
    }
    if ($manualStatus -ne 401) {
        throw "Manual project mutation without X-Edit-Passphrase should return 401, got $manualStatus."
    }

    $apiKeyCannotEditStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/projects/P1/contract-links" `
            -Headers @{ "X-Api-Key" = $apiKey } -ContentType "application/json" -Body '{}' -UseBasicParsing
    }
    if ($apiKeyCannotEditStatus -ne 401) {
        throw "API key alone must not unlock manual editing, got $apiKeyCannotEditStatus."
    }

    $wrongEditStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/access/edit/verify" `
            -Headers @{ "X-Edit-Passphrase" = "wrong-passphrase" } -UseBasicParsing
    }
    if ($wrongEditStatus -ne 401) {
        throw "Wrong edit passphrase should return 401, got $wrongEditStatus."
    }

    $editVerifyStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/access/edit/verify" `
            -Headers @{ "X-Edit-Passphrase" = $editPassphrase } -UseBasicParsing
    }
    if ($editVerifyStatus -ne 204) {
        throw "Correct edit passphrase verification should return 204, got $editVerifyStatus."
    }

    $authorizedManualStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/projects/P1/contract-links" `
            -Headers @{ "X-Edit-Passphrase" = $editPassphrase } `
            -ContentType "application/json" -Body '{}' -UseBasicParsing
    }
    if ($authorizedManualStatus -ne 400) {
        throw "Edit passphrase should pass authorization and reach validation, got $authorizedManualStatus."
    }

    $editPassphraseCannotImportStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/imports/contracts" `
            -Headers @{ "X-Edit-Passphrase" = $editPassphrase } `
            -ContentType "application/json" -Body '{}' -UseBasicParsing
    }
    if ($editPassphraseCannotImportStatus -ne 401) {
        throw "Edit passphrase alone must not unlock imports, got $editPassphraseCannotImportStatus."
    }

    $rotatedEditPassphrase = "rotated-edit-passphrase-without-restart"
    Write-EditPassphraseHash $rotatedEditPassphrase $editPassphraseFile
    $oldPassphraseAfterRotationStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/access/edit/verify" `
            -Headers @{ "X-Edit-Passphrase" = $editPassphrase } -UseBasicParsing
    }
    $newPassphraseAfterRotationStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/access/edit/verify" `
            -Headers @{ "X-Edit-Passphrase" = $rotatedEditPassphrase } -UseBasicParsing
    }
    if ($oldPassphraseAfterRotationStatus -ne 401 -or $newPassphraseAfterRotationStatus -ne 204) {
        throw "Edit passphrase rotation should apply without restart; old=$oldPassphraseAfterRotationStatus new=$newPassphraseAfterRotationStatus."
    }

    $wrongStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/access/verify" `
            -Headers @{ "X-Api-Key" = "wrong-key" } -UseBasicParsing
    }
    if ($wrongStatus -ne 401) { throw "Wrong API key should return 401, got $wrongStatus." }

    $verifyStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/access/verify" `
            -Headers @{ "X-Api-Key" = $apiKey } -UseBasicParsing
    }
    if ($verifyStatus -ne 204) { throw "Correct API key verification should return 204, got $verifyStatus." }

    $missingGetStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Get -Uri "$baseUrl/api/definitely-missing" -UseBasicParsing
    }
    if ($missingGetStatus -ne 404) { throw "Unknown API GET should return 404, got $missingGetStatus." }

    $authorizedMissingStatus = Get-HttpStatus {
        Invoke-WebRequest -Method Post -Uri "$baseUrl/api/definitely-missing" `
            -Headers @{ "X-Api-Key" = $apiKey } -UseBasicParsing
    }
    if ($authorizedMissingStatus -ne 404) {
        throw "Authorized unknown API POST should reach routing and return 404, got $authorizedMissingStatus."
    }

    "API security boundary checks passed."
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item Env:MoneyFlow__EditPassphraseFilePath -ErrorAction SilentlyContinue
    if (Test-Path $editPassphraseFile) { Remove-Item -LiteralPath $editPassphraseFile -Force }
}
