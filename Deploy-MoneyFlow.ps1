[CmdletBinding()]
param(
    [string]$InitialDatabase
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Stop-WithError([string]$Message) {
    Write-Host "`nKLAIDA: $Message" -ForegroundColor Red
    Write-Host "Diegimas nebaigtas. Pagalba: docs\deployment.md" -ForegroundColor Yellow
    Read-Host 'Paspauskite Enter, kad uždarytumėte'
    exit 1
}

# The script works both from a Git clone and from an extracted GitHub ZIP.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if (-not [string]::IsNullOrWhiteSpace($InitialDatabase)) {
        $arguments += @('-InitialDatabase', "`"$InitialDatabase`"")
    }
    Write-Host 'Prašoma administratoriaus teisių...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$repoRoot = $PSScriptRoot
$projectFile = Join-Path $repoRoot 'PADS.MoneyFlow.Api.csproj'
$installDirectory = Join-Path $env:ProgramFiles 'PADS\MoneyFlow'
$executable = Join-Path $installDirectory 'PADS.MoneyFlow.Api.exe'
$stagingDirectory = Join-Path $env:TEMP ("MoneyFlow-publish-{0}" -f [guid]::NewGuid().ToString('N'))
$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$configurationDirectory = Join-Path $dataDirectory 'Configuration'
$serviceName = 'MoneyFlow'
$serviceRegistryKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$firewallRuleName = 'MoneyFlow LAN (TCP 5000)'

if (-not (Test-Path -LiteralPath $projectFile)) { Stop-WithError "Nerastas projektas: $projectFile" }
if (-not [string]::IsNullOrWhiteSpace($InitialDatabase)) {
    if (-not (Test-Path -LiteralPath $InitialDatabase -PathType Leaf)) {
        Stop-WithError "Nerasta nurodyta pradinė duomenų bazė: $InitialDatabase"
    }
    $InitialDatabase = (Resolve-Path -LiteralPath $InitialDatabase).Path
    if ((Get-Item -LiteralPath $InitialDatabase).Length -eq 0) {
        Stop-WithError 'Nurodyta pradinė duomenų bazė yra tuščias failas.'
    }
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath ($InitialDatabase + $suffix)) {
            Stop-WithError "Šalia pradinės DB yra aktyvus SQLite failas '$suffix'. Pirmiausia saugiai uždarykite DB naudojančią programą."
        }
    }
}

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
    foreach ($requiredFile in @('appsettings.json', 'appsettings.Production.json', 'Set-MoneyFlowApiKey.ps1', 'wwwroot\index.html')) {
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
    New-Item -ItemType Directory -Path $configurationDirectory -Force | Out-Null

    # A normal local user may update only this configuration folder. The app
    # reads the key file dynamically, so changing it needs no service restart.
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')),
        [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),
        [Security.AccessControl.FileSystemRights]::Modify, $inheritance, $propagation, $allow)))
    Set-Acl -LiteralPath $configurationDirectory -AclObject $acl
    Write-Host '[GERAI] Paruoštas API rakto aplankas ne administratoriaus scenarijui.' -ForegroundColor Green
    if (Test-Path -LiteralPath $liveDatabase) {
        Write-Host '[GERAI] Esama DB palikta nepakeista.' -ForegroundColor Green
        if (-not [string]::IsNullOrWhiteSpace($InitialDatabase)) {
            Write-Warning '-InitialDatabase nepanaudota, nes gyva DB jau yra. Atkurkite atsarginę kopiją tik pagal docs\deployment.md.'
        }
    } elseif (-not [string]::IsNullOrWhiteSpace($InitialDatabase)) {
        Copy-Item -LiteralPath $InitialDatabase -Destination $liveDatabase -Force
        Write-Host '[GERAI] Pirmo diegimo DB atkurta iš nurodyto išorinio failo.' -ForegroundColor Green
    } else {
        Write-Host '[GERAI] Gyvos DB nėra; programa pirmo paleidimo metu sukurs tuščią DB.' -ForegroundColor Green
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
        'ASPNETCORE_URLS=http://0.0.0.0:5000'
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
    Write-Host "`nVidinio DNS ir reverse proxy nustatymus turi atlikti tinklo administratorius (žr. docs\deployment.md)." -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath (Join-Path $configurationDirectory 'api-key.txt'))) {
        Write-Host "`nKITAS ŽINGSNIS: paprastas vartotojas turi paleisti Set-MoneyFlowApiKey.ps1." -ForegroundColor Yellow
    }
}
catch {
    Write-Host "`nKLAIDA: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Paslaugos būsena: $((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status)" -ForegroundColor Yellow
    Write-Host 'Žr. docs\deployment.md ir docs\troubleshooting.md.' -ForegroundColor Yellow
    Read-Host 'Paspauskite Enter, kad uždarytumėte'
    exit 1
}
finally {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Read-Host 'Paspauskite Enter, kad uždarytumėte'
