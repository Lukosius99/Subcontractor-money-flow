$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$expectedDbPath = Join-Path $projectRoot "test-data/production-env-override.db"
$fallbackDbPath = Join-Path $projectRoot "test-data/production-config-fallback.db"
$baseUrl = "http://127.0.0.1:5092"

foreach ($path in @($expectedDbPath, $fallbackDbPath)) {
    if (Test-Path $path) { Remove-Item -LiteralPath $path -Force }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $expectedDbPath) | Out-Null

# The standard configuration key deliberately points at a harmless fallback.
# MONEY_FLOW_DB_PATH must win even in Production; a regression therefore writes
# only another test-data file and can never touch ProgramData.
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:MONEY_FLOW_DB_PATH = $expectedDbPath
$env:MoneyFlow__DatabasePath = $fallbackDbPath
$env:MONEY_FLOW_API_KEY = "production-config-test-key"
$env:Kestrel__Endpoints__Http__Url = $baseUrl
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --no-launch-profile" `
    -WorkingDirectory $projectRoot -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $response = Invoke-RestMethod "$baseUrl/ready"
            if ($response.status -eq "ready") {
                $ready = $true
                break
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) { throw "Production process did not become ready at $baseUrl." }
    if (-not (Test-Path -LiteralPath $expectedDbPath -PathType Leaf)) {
        throw "MONEY_FLOW_DB_PATH did not create the expected production database."
    }
    if (Test-Path -LiteralPath $fallbackDbPath) {
        throw "Normal configuration overrode MONEY_FLOW_DB_PATH in Production."
    }

    $deployScript = Get-Content -LiteralPath (Join-Path $projectRoot "Deploy-MoneyFlow.ps1") -Raw
    $reverseProxyLauncher = Join-Path $projectRoot "Deploy-MoneyFlow-ReverseProxy.bat"
    if ($deployScript -notmatch "ValidateSet\('DirectLan', 'ReverseProxy'\)" `
        -or $deployScript -notmatch "http://127\.0\.0\.1:5000" `
        -or $deployScript -notmatch "deployment-network-mode\.txt" `
        -or $deployScript -notmatch 'Kestrel__Endpoints__Http__Url=\$listenUrl' `
        -or -not (Test-Path -LiteralPath $reverseProxyLauncher -PathType Leaf)) {
        throw "Persistent DirectLan/ReverseProxy deployment modes are incomplete."
    }

    "Production database-path precedence check passed."
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item Env:ASPNETCORE_ENVIRONMENT,Env:MONEY_FLOW_DB_PATH,Env:MoneyFlow__DatabasePath,Env:MONEY_FLOW_API_KEY,Env:Kestrel__Endpoints__Http__Url -ErrorAction SilentlyContinue
}
