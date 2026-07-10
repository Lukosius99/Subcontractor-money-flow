$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$dbPath = Join-Path $projectRoot "test-data/api-security.db"
$baseUrl = "http://localhost:5091"
$apiKey = "test-api-key-for-mutations"
$PSDefaultParameterValues["Invoke-WebRequest:TimeoutSec"] = 5

if (Test-Path $dbPath) { Remove-Item -LiteralPath $dbPath -Force }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dbPath) | Out-Null

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:MONEY_FLOW_DB_PATH = $dbPath
$env:MONEY_FLOW_API_KEY = $apiKey
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
        throw "Manual project mutation without X-Api-Key should return 401, got $manualStatus."
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
}
