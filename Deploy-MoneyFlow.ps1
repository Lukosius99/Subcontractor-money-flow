[CmdletBinding()]
param(
    [switch]$ReplaceDatabase
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Stop-WithError([string]$Message) {
    Write-Host "`nKLAIDA: $Message" -ForegroundColor Red
    Write-Host "Diegimas nebaigtas. Pagalba: DEPLOYMENT.md" -ForegroundColor Yellow
    Read-Host 'Paspauskite Enter, kad uždarytumėte'
    exit 1
}

# The script works both from a Git clone and from an extracted GitHub ZIP.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($ReplaceDatabase) { $arguments += '-ReplaceDatabase' }
    Write-Host 'Prašoma administratoriaus teisių...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$repoRoot = $PSScriptRoot
$projectFile = Join-Path $repoRoot 'PADS.MoneyFlow.Api.csproj'
$seedDatabase = Join-Path $repoRoot 'seed\monthly-money-flow.db'
$installDirectory = Join-Path $env:ProgramFiles 'PADS\MoneyFlow'
$executable = Join-Path $installDirectory 'PADS.MoneyFlow.Api.exe'
$stagingDirectory = Join-Path $env:TEMP ("MoneyFlow-publish-{0}" -f [guid]::NewGuid().ToString('N'))
$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupDirectory = Join-Path $dataDirectory 'Backups'
$serviceName = 'MoneyFlow'
$serviceRegistryKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$firewallRuleName = 'MoneyFlow LAN (TCP 5000)'

if (-not (Test-Path -LiteralPath $projectFile)) { Stop-WithError "Nerastas projektas: $projectFile" }
if (-not (Test-Path -LiteralPath $seedDatabase)) { Stop-WithError "Nerasta pradinė duomenų bazė: $seedDatabase" }
if ((Get-Item -LiteralPath $seedDatabase).Length -eq 0) { Stop-WithError 'Pradinė duomenų bazė yra tuščias failas.' }

Write-Host 'MoneyFlow diegimas' -ForegroundColor Green
Write-Host "Programa: $installDirectory"
Write-Host "Duomenys: $liveDatabase"

Write-Step '.NET 10 SDK tikrinimas'
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Stop-WithError 'Nerastas .NET 10 SDK. Įdiekite jį iš https://dotnet.microsoft.com/download/dotnet/10.0'
}
$hasRequiredSdk = @(& dotnet --list-sdks) | Where-Object { $_ -match '^10\.' }
if (-not $hasRequiredSdk) {
    & dotnet --list-sdks
    Stop-WithError 'Reikalingas .NET 10 SDK (vien Runtime nepakanka, nes programa publikuojama šiame kompiuteryje).'
}
Write-Host '[GERAI] .NET 10 SDK rastas.' -ForegroundColor Green

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

# Keep the existing import key on updates. On first install, accept a machine
# environment value or ask for one without echoing it to the screen.
$apiKey = $env:MONEY_FLOW_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey) -and (Test-Path -LiteralPath $serviceRegistryKey)) {
    $serviceEnvironment = (Get-ItemProperty -LiteralPath $serviceRegistryKey -Name Environment -ErrorAction SilentlyContinue).Environment
    $keySetting = @($serviceEnvironment) | Where-Object { $_ -like 'MONEY_FLOW_API_KEY=*' } | Select-Object -First 1
    if ($keySetting) { $apiKey = $keySetting.Substring('MONEY_FLOW_API_KEY='.Length) }
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $secureKey = Read-Host 'Įveskite PAD / Cloud Flow importo API raktą' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try { $apiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
if ([string]::IsNullOrWhiteSpace($apiKey)) { Stop-WithError 'Production aplinkai būtinas importo API raktas.' }

if ($existingService -and $existingService.Status -ne 'Stopped') {
    Write-Step 'Esamos paslaugos stabdymas'
    Stop-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

try {
    Write-Step 'Programos publikavimas'
    & dotnet publish $projectFile -c Release -o $stagingDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish baigė darbą su kodu $LASTEXITCODE" }
    $stagedExe = Join-Path $stagingDirectory 'PADS.MoneyFlow.Api.exe'
    if (-not (Test-Path -LiteralPath $stagedExe)) { throw "Publikavimo aplanke nerastas $stagedExe" }
    foreach ($requiredFile in @('appsettings.json', 'appsettings.Production.json', 'wwwroot\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile))) {
            throw "Publikavimo rezultate nerastas būtinas failas: $requiredFile"
        }
    }

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $installDirectory -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $stagingDirectory '*') -Destination $installDirectory -Recurse -Force
    Write-Host "[GERAI] Programa įdiegta į $installDirectory" -ForegroundColor Green

    Write-Step 'Duomenų bazės paruošimas'
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $liveDatabase) {
        if ($ReplaceDatabase) {
            New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $backup = Join-Path $backupDirectory "monthly-money-flow-before-replace-$stamp.db"
            Copy-Item -LiteralPath $liveDatabase -Destination $backup -Force
            Copy-Item -LiteralPath $seedDatabase -Destination $liveDatabase -Force
            Write-Host "[GERAI] Senoji DB išsaugota: $backup" -ForegroundColor Green
            Write-Host '[GERAI] DB tyčia pakeista repo pradine kopija.' -ForegroundColor Green
        } else {
            Write-Host '[GERAI] Esama DB palikta nepakeista.' -ForegroundColor Green
        }
    } else {
        Copy-Item -LiteralPath $seedDatabase -Destination $liveDatabase -Force
        Write-Host '[GERAI] Pirmo diegimo DB atkurta iš seed\monthly-money-flow.db.' -ForegroundColor Green
    }

    Write-Step 'Windows paslaugos konfigūravimas'
    if (-not $existingService) {
        New-Service -Name $serviceName -BinaryPathName "`"$executable`"" -DisplayName 'MoneyFlow' `
            -Description 'PADS subcontractor money-flow application' -StartupType Automatic | Out-Null
    } else {
        & sc.exe config $serviceName binPath= "`"$executable`"" start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Nepavyko atnaujinti Windows paslaugos.' }
    }
    New-ItemProperty -LiteralPath $serviceRegistryKey -Name Environment -PropertyType MultiString -Force -Value @(
        'ASPNETCORE_ENVIRONMENT=Production',
        'ASPNETCORE_URLS=http://0.0.0.0:5000',
        "MONEY_FLOW_API_KEY=$apiKey"
    ) | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/''/0 | Out-Null

    Write-Step 'Windows užkardos taisyklės konfigūravimas'
    Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort 5000 -Profile Domain,Private | Out-Null
    Write-Host '[GERAI] TCP 5000 leidžiamas Domain ir Private tinkluose.' -ForegroundColor Green

    Write-Step 'Paslaugos paleidimas ir patikra'
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $response = $null
    for ($attempt = 1; $attempt -le 10 -and -not $response; $attempt++) {
        try { $response = Invoke-WebRequest -Uri 'http://localhost:5000' -UseBasicParsing -TimeoutSec 5 }
        catch { if ($attempt -lt 10) { Start-Sleep -Seconds 2 } }
    }
    if (-not $response -or $response.StatusCode -lt 200 -or $response.StatusCode -ge 400) {
        throw 'Paslauga paleista, bet http://localhost:5000 neatsakė sėkmingai.'
    }

    $computerName = $env:COMPUTERNAME
    $lanIps = @(Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress -Unique)
    Write-Host "`nDIEGIMAS BAIGTAS SĖKMINGAI" -ForegroundColor Green
    Write-Host '  http://localhost:5000'
    Write-Host "  http://${computerName}:5000"
    foreach ($ip in $lanIps) { Write-Host "  http://${ip}:5000" }
    Write-Host "`nktpads.lt nustatymus turi atlikti tinklo administratorius (žr. DEPLOYMENT.md)." -ForegroundColor Cyan
}
catch {
    Write-Host "`nKLAIDA: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Paslaugos būsena: $((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status)" -ForegroundColor Yellow
    Write-Host 'Žr. DEPLOYMENT.md trikčių šalinimo skyrių.' -ForegroundColor Yellow
    Read-Host 'Paspauskite Enter, kad uždarytumėte'
    exit 1
}
finally {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Read-Host 'Paspauskite Enter, kad uždarytumėte'
