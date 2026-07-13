[CmdletBinding()]
param(
    [string]$InitialDatabase,
    [ValidateSet('DirectLan', 'ReverseProxy')]
    [string]$NetworkMode
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Stop-WithError([string]$Message) { throw $Message }
function Get-PlainText([Security.SecureString]$SecureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
function Add-FileRule(
    [Security.AccessControl.DirectorySecurity]$Acl,
    [string]$Identity,
    [Security.AccessControl.FileSystemRights]$Rights,
    [Security.AccessControl.InheritanceFlags]$Inheritance
) {
    $identityReference = if ($Identity -like 'S-1-*') {
        New-Object Security.Principal.SecurityIdentifier($Identity)
    } else {
        New-Object Security.Principal.NTAccount($Identity)
    }
    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        $identityReference, $Rights, $Inheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $Acl.AddAccessRule($rule)
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($InitialDatabase) { $arguments += @('-InitialDatabase', "`"$InitialDatabase`"") }
    if ($PSBoundParameters.ContainsKey('NetworkMode')) { $arguments += @('-NetworkMode', $NetworkMode) }
    Write-Host 'Prašoma administratoriaus teisių...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$repoRoot = $PSScriptRoot
$projectFile = Join-Path $repoRoot 'PADS.MoneyFlow.Api.csproj'
$installParent = Join-Path $env:ProgramFiles 'PADS'
$installDirectory = Join-Path $installParent 'MoneyFlow'
$previousDirectory = Join-Path $installParent 'MoneyFlow.previous'
$stagingDirectory = Join-Path $installParent ("MoneyFlow.staging.{0}" -f [guid]::NewGuid().ToString('N'))
$executable = Join-Path $installDirectory 'PADS.MoneyFlow.Api.exe'
$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$configurationDirectory = Join-Path $dataDirectory 'Configuration'
$networkModeFile = Join-Path $configurationDirectory 'deployment-network-mode.txt'
$passphraseFile = Join-Path $configurationDirectory 'backup-passphrase.txt'
$serviceName = 'MoneyFlow'
$serviceAccount = "NT SERVICE\$serviceName"
$serviceRegistryKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$firewallRuleName = 'MoneyFlow LAN (TCP 5000)'
$decryptedInitialDatabase = $null
$databaseToInstall = $null
$oldInstallMoved = $false
$newInstallActivated = $false
$serviceWasRunning = $false
$existingService = $null
$preDeployDatabase = $null

try {
    if (-not (Test-Path -LiteralPath $projectFile)) { Stop-WithError "Nerastas projektas: $projectFile" }
    if ($InitialDatabase) {
        if (Test-Path -LiteralPath $liveDatabase -PathType Leaf) {
            Write-Warning '-InitialDatabase ignoruojama, nes gyva DB jau egzistuoja.'
            $InitialDatabase = $null
        } elseif (-not (Test-Path -LiteralPath $InitialDatabase -PathType Leaf)) {
            Stop-WithError "Nerasta pradinė DB kopija: $InitialDatabase"
        } else {
            $InitialDatabase = (Resolve-Path -LiteralPath $InitialDatabase).Path
        }
    }

    Write-Host 'MoneyFlow transakcinis diegimas' -ForegroundColor Green
    Write-Host "Programa: $installDirectory"
    Write-Host "Duomenys: $liveDatabase"

    Write-Step '.NET 10 SDK tikrinimas'
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        Stop-WithError 'Nerastas .NET 10 SDK.'
    }
    if (-not (@(& dotnet --list-sdks) | Where-Object { $_ -match '^10\.' })) {
        Stop-WithError 'Reikalingas .NET 10 SDK.'
    }

    # Publish and validation happen while the old service is still running.
    Write-Step 'Naujos win-x64 versijos paruošimas'
    New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained false -o $stagingDirectory --nologo
    if ($LASTEXITCODE -ne 0) { Stop-WithError "dotnet publish baigė darbą kodu $LASTEXITCODE" }
    $stagedExecutable = Join-Path $stagingDirectory 'PADS.MoneyFlow.Api.exe'
    foreach ($requiredFile in @(
        'PADS.MoneyFlow.Api.exe',
        'appsettings.json',
        'appsettings.Production.json',
        'Set-MoneyFlowApiKey.ps1',
        'Set-MoneyFlowApiKey.bat',
        'Set-MoneyFlowBackupPassphrase.ps1',
        'Set-MoneyFlowBackupPassphrase.bat',
        'Set-MoneyFlowEditPassphrase.ps1',
        'Set-MoneyFlowEditPassphrase.bat',
        'wwwroot\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile))) {
            Stop-WithError "Publish rezultate nerastas: $requiredFile"
        }
    }

    if ($InitialDatabase) {
        $databaseToInstall = $InitialDatabase
        if ([IO.Path]::GetExtension($InitialDatabase) -eq '.mfbackup') {
            Write-Step 'Pradinės DB kopijos iššifravimas'
            $passphrase = $env:MONEY_FLOW_BACKUP_PASSPHRASE
            if (-not $passphrase -and (Test-Path -LiteralPath $passphraseFile -PathType Leaf)) {
                $passphrase = (Get-Content -LiteralPath $passphraseFile -Raw).Trim()
            }
            if (-not $passphrase) {
                $passphrase = Get-PlainText (Read-Host 'Įveskite backup šifravimo frazę' -AsSecureString)
            }
            if (-not $passphrase -or $passphrase.Length -lt 20) {
                Stop-WithError 'Nėra tinkamos backup šifravimo frazės.'
            }
            $decryptedInitialDatabase = Join-Path $env:TEMP ("MoneyFlow-initial-{0}.db" -f [guid]::NewGuid().ToString('N'))
            $previousPassphrase = $env:MONEY_FLOW_BACKUP_PASSPHRASE
            try {
                $env:MONEY_FLOW_BACKUP_PASSPHRASE = $passphrase
                & $stagedExecutable --decrypt-db $InitialDatabase $decryptedInitialDatabase
                if ($LASTEXITCODE -ne 0) { Stop-WithError 'Pradinės DB iššifravimas nepavyko.' }
            } finally {
                if ($null -eq $previousPassphrase) { Remove-Item Env:MONEY_FLOW_BACKUP_PASSPHRASE -ErrorAction SilentlyContinue }
                else { $env:MONEY_FLOW_BACKUP_PASSPHRASE = $previousPassphrase }
                $passphrase = $null
            }
            $databaseToInstall = $decryptedInitialDatabase
        } else {
            foreach ($suffix in @('-wal', '-shm', '-journal')) {
                if (Test-Path -LiteralPath ($InitialDatabase + $suffix)) {
                    Stop-WithError "Šalia pradinės DB yra aktyvus SQLite failas '$suffix'."
                }
            }
        }

        Write-Step 'Pradinės DB integrity_check'
        & $stagedExecutable --validate-db $databaseToInstall
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'Pradinė DB nepraėjo integrity_check.' }
    }

    $existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceWasRunning = $existingService -and $existingService.Status -ne 'Stopped'
    if ($serviceWasRunning) {
        Write-Step 'Esamos paslaugos stabdymas'
        Stop-Service $serviceName
        (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    if (Test-Path -LiteralPath $liveDatabase -PathType Leaf) {
        New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
        $preDeployDatabase = Join-Path $backupsDirectory ("pre-deploy-{0}.db" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Copy-Item -LiteralPath $liveDatabase -Destination $preDeployDatabase
        foreach ($suffix in @('-wal', '-shm')) {
            if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
                Copy-Item -LiteralPath ($liveDatabase + $suffix) -Destination ($preDeployDatabase + $suffix)
            }
        }
        Get-ChildItem $backupsDirectory -Filter 'pre-deploy-*.db' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -Skip 10 |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    Write-Step 'Atominis programos versijos pakeitimas'
    Remove-Item -LiteralPath $previousDirectory -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $installDirectory) {
        Move-Item -LiteralPath $installDirectory -Destination $previousDirectory
        $oldInstallMoved = $true
    }
    Move-Item -LiteralPath $stagingDirectory -Destination $installDirectory
    $newInstallActivated = $true

    Write-Step 'Duomenų katalogų ir DB paruošimas'
    New-Item -ItemType Directory -Path $dataDirectory, $backupsDirectory, $configurationDirectory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $liveDatabase) -and $databaseToInstall) {
        Copy-Item -LiteralPath $databaseToInstall -Destination $liveDatabase
    }

    $effectiveNetworkMode = $NetworkMode
    if (-not $effectiveNetworkMode -and (Test-Path -LiteralPath $networkModeFile -PathType Leaf)) {
        $savedNetworkMode = (Get-Content -LiteralPath $networkModeFile -Raw).Trim()
        if ($savedNetworkMode -in @('DirectLan', 'ReverseProxy')) {
            $effectiveNetworkMode = $savedNetworkMode
        } else {
            Stop-WithError "Neatpažintas išsaugotas tinklo režimas: $savedNetworkMode"
        }
    }
    if (-not $effectiveNetworkMode) { $effectiveNetworkMode = 'DirectLan' }
    $listenUrl = if ($effectiveNetworkMode -eq 'ReverseProxy') {
        'http://127.0.0.1:5000'
    } else {
        'http://0.0.0.0:5000'
    }

    Write-Step 'Windows paslaugos konfigūravimas'
    if (-not $existingService) {
        New-Service -Name $serviceName -BinaryPathName "`"$executable`"" -DisplayName 'MoneyFlow' `
            -Description 'PADS subcontractor money-flow application' -StartupType Automatic | Out-Null
    }
    & sc.exe config $serviceName binPath= "`"$executable`"" start= auto obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Nepavyko sukonfigūruoti serviso paskyros.' }
    $configuredService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if (-not $configuredService -or $configuredService.StartName -ne $serviceAccount) {
        Stop-WithError "Servisas nesukonfigūruotas su mažiausių teisių paskyra '$serviceAccount'."
    }
    New-ItemProperty -LiteralPath $serviceRegistryKey -Name Environment -PropertyType MultiString -Force -Value @(
        'ASPNETCORE_ENVIRONMENT=Production',
        "Kestrel__Endpoints__Http__Url=$listenUrl"
    ) | Out-Null
    if (-not [Diagnostics.EventLog]::SourceExists('MoneyFlow')) {
        New-EventLog -LogName Application -Source 'MoneyFlow'
    }
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/''/0 | Out-Null

    Write-Step 'Mažiausių failų teisių pritaikymas'
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.InheritanceFlags]::None
    $dataAcl = New-Object Security.AccessControl.DirectorySecurity
    $dataAcl.SetAccessRuleProtection($true, $false)
    Add-FileRule $dataAcl 'S-1-5-18' 'FullControl' $inherit
    Add-FileRule $dataAcl 'S-1-5-32-544' 'FullControl' $inherit
    Add-FileRule $dataAcl $serviceAccount 'Modify' $inherit
    Add-FileRule $dataAcl 'S-1-5-32-545' 'ReadAndExecute' $none
    Set-Acl $dataDirectory $dataAcl

    $configurationAcl = New-Object Security.AccessControl.DirectorySecurity
    $configurationAcl.SetAccessRuleProtection($true, $false)
    Add-FileRule $configurationAcl 'S-1-5-18' 'FullControl' $inherit
    Add-FileRule $configurationAcl 'S-1-5-32-544' 'FullControl' $inherit
    Add-FileRule $configurationAcl $serviceAccount 'ReadAndExecute' $inherit
    Add-FileRule $configurationAcl 'S-1-5-32-545' 'Modify' $inherit
    Set-Acl $configurationDirectory $configurationAcl

    $backupAcl = New-Object Security.AccessControl.DirectorySecurity
    $backupAcl.SetAccessRuleProtection($true, $false)
    Add-FileRule $backupAcl 'S-1-5-18' 'FullControl' $inherit
    Add-FileRule $backupAcl 'S-1-5-32-544' 'FullControl' $inherit
    Add-FileRule $backupAcl $serviceAccount 'Modify' $inherit
    Add-FileRule $backupAcl 'S-1-5-32-545' 'ReadAndExecute' $inherit
    Set-Acl $backupsDirectory $backupAcl

    $installAcl = Get-Acl $installDirectory
    Add-FileRule $installAcl $serviceAccount 'ReadAndExecute' $inherit
    Set-Acl $installDirectory $installAcl

    Write-Step 'Ugniasienės taisyklės konfigūravimas'
    Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($effectiveNetworkMode -eq 'DirectLan') {
        New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP `
            -LocalPort 5000 -Profile Domain,Private | Out-Null
    } else {
        Write-Host 'ReverseProxy režimas: TCP 5000 iš LAN neatidaromas.' -ForegroundColor Yellow
    }

    Write-Step 'Naujos versijos paleidimas ir readiness patikra'
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $ready = $false
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            $response = Invoke-RestMethod 'http://localhost:5000/ready' -TimeoutSec 3
            if ($response.status -eq 'ready') { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { Stop-WithError 'Nauja versija nepraėjo /ready patikros.' }

    Remove-Item -LiteralPath $previousDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $oldInstallMoved = $false
    [IO.File]::WriteAllText($networkModeFile, $effectiveNetworkMode, [Text.UTF8Encoding]::new($false))
    Write-Host "`nDIEGIMAS BAIGTAS SĖKMINGAI" -ForegroundColor Green
    Write-Host "  Tinklo režimas: $effectiveNetworkMode"
    Write-Host '  Patikra: http://localhost:5000'
    if (-not (Test-Path (Join-Path $configurationDirectory 'api-key.txt'))) {
        Write-Host 'KITAS ŽINGSNIS: paleiskite Set-MoneyFlowApiKey.bat.' -ForegroundColor Yellow
    }
    if (-not (Test-Path $passphraseFile)) {
        Write-Host 'KITAS ŽINGSNIS: paleiskite Set-MoneyFlowBackupPassphrase.bat.' -ForegroundColor Yellow
    }
    if (-not (Test-Path (Join-Path $configurationDirectory 'edit-passphrase.txt'))) {
        Write-Host 'KITAS ŽINGSNIS: paleiskite Set-MoneyFlowEditPassphrase.bat.' -ForegroundColor Yellow
    }
} catch {
    $deploymentError = $_.Exception.Message
    if ($newInstallActivated -and $oldInstallMoved -and (Test-Path $previousDirectory)) {
        Write-Host "`nNauja versija nepavyko. Vykdomas automatinis rollback..." -ForegroundColor Yellow
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $previousDirectory -Destination $installDirectory
        if ($preDeployDatabase -and (Test-Path -LiteralPath $preDeployDatabase)) {
            foreach ($suffix in @('', '-wal', '-shm', '-journal')) {
                Remove-Item -LiteralPath ($liveDatabase + $suffix) -Force -ErrorAction SilentlyContinue
            }
            Copy-Item -LiteralPath $preDeployDatabase -Destination $liveDatabase
            foreach ($suffix in @('-wal', '-shm')) {
                if (Test-Path -LiteralPath ($preDeployDatabase + $suffix)) {
                    Copy-Item -LiteralPath ($preDeployDatabase + $suffix) -Destination ($liveDatabase + $suffix)
                }
            }
        }
        $oldExecutable = Join-Path $installDirectory 'PADS.MoneyFlow.Api.exe'
        & sc.exe config $serviceName binPath= "`"$oldExecutable`"" | Out-Null
        Start-Service $serviceName -ErrorAction SilentlyContinue
        Write-Host '[GERAI] Ankstesnė programos versija grąžinta.' -ForegroundColor Green
    } elseif ($serviceWasRunning -and (Get-Service $serviceName -ErrorAction SilentlyContinue).Status -eq 'Stopped') {
        Start-Service $serviceName -ErrorAction SilentlyContinue
    }
    Write-Host "`nKLAIDA: $deploymentError" -ForegroundColor Red
    Write-Host 'Diegimas nebaigtas. Žr. docs\deployment.md.' -ForegroundColor Yellow
    if ($Host.Name -notlike '*ServerRemoteHost*') { Read-Host 'Paspauskite Enter, kad uždarytumėte' | Out-Null }
    exit 1
} finally {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    if ($decryptedInitialDatabase) {
        Remove-Item -LiteralPath $decryptedInitialDatabase -Force -ErrorAction SilentlyContinue
    }
}

Read-Host 'Paspauskite Enter, kad uždarytumėte' | Out-Null
