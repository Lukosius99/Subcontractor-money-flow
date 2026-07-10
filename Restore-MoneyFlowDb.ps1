[CmdletBinding()]
param(
    [string]$BackupFile,
    [switch]$NoPause,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Wait-Exit([int]$Code) {
    if (-not $NoPause) { Read-Host 'Paspauskite Enter, kad uždarytumėte' | Out-Null }
    exit $Code
}
function Stop-WithError([string]$Message) {
    Write-Host "`nKLAIDA: $Message" -ForegroundColor Red
    Wait-Exit 1
}
function Get-PlainText([Security.SecureString]$SecureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($BackupFile) { $arguments += @('-BackupFile', "`"$BackupFile`"") }
    if ($NoPause) { $arguments += '-NoPause' }
    if ($Force) { $arguments += '-Force' }
    Write-Host 'DB atkūrimui prašoma administratoriaus teisių...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$passphraseFile = Join-Path $dataDirectory 'Configuration\backup-passphrase.txt'
$repoBackupDirectory = Join-Path $PSScriptRoot 'db-backups'
$serviceName = 'MoneyFlow'
$readyUrl = 'http://localhost:5000/ready'
$installedExecutable = Join-Path $env:ProgramFiles 'PADS\MoneyFlow\PADS.MoneyFlow.Api.exe'
$projectFile = Join-Path $PSScriptRoot 'PADS.MoneyFlow.Api.csproj'
$decryptedTemporaryFile = $null
$preRestorePath = $null

function Invoke-DatabaseTool([string[]]$Arguments) {
    if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
        & $installedExecutable @Arguments
    } elseif ((Test-Path -LiteralPath $projectFile -PathType Leaf) -and (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        & dotnet run --project $projectFile -c Release --no-launch-profile -- @Arguments
    } else {
        Stop-WithError 'Nerastas nei įdiegtas DB įrankis, nei .NET projektas su SDK.'
    }
    if ($LASTEXITCODE -ne 0) { Stop-WithError "DB patikros įrankis grąžino kodą $LASTEXITCODE." }
}

Write-Host 'MoneyFlow DB atkūrimas' -ForegroundColor Green
Write-Host "Duomenų bazė: $liveDatabase"

if (-not $BackupFile) {
    $candidates = @()
    if (Test-Path $backupsDirectory) {
        $candidates += @(Get-ChildItem $backupsDirectory -File | Where-Object Extension -In '.db', '.mfbackup')
    }
    if (Test-Path $repoBackupDirectory) {
        $candidates += @(Get-ChildItem $repoBackupDirectory -File | Where-Object Extension -In '.db', '.mfbackup')
    }
    $candidates = @($candidates | Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -eq 0) { Stop-WithError 'Nerasta DB arba šifruotų .mfbackup kopijų.' }

    Write-Step 'Rastos atsarginės kopijos'
    for ($index = 0; $index -lt $candidates.Count; $index++) {
        $file = $candidates[$index]
        Write-Host ("[{0,2}] {1}  {2,8} KB  {3:yyyy-MM-dd HH:mm}" -f `
            ($index + 1), $file.Name.PadRight(55), [math]::Round($file.Length / 1KB), $file.LastWriteTime)
    }
    $answer = Read-Host "`nĮveskite kopijos numerį arba Enter, kad atšauktumėte"
    $selection = 0
    if (-not [int]::TryParse($answer, [ref]$selection) -or $selection -lt 1 -or $selection -gt $candidates.Count) {
        Write-Host 'Atšaukta.' -ForegroundColor Yellow
        Wait-Exit 0
    }
    $BackupFile = $candidates[$selection - 1].FullName
}

if (-not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) { Stop-WithError "Nerasta kopija: $BackupFile" }
$BackupFile = (Resolve-Path -LiteralPath $BackupFile).Path

$restoreSource = $BackupFile
if ([IO.Path]::GetExtension($BackupFile) -eq '.mfbackup') {
    $passphrase = $env:MONEY_FLOW_BACKUP_PASSPHRASE
    if (-not $passphrase -and (Test-Path -LiteralPath $passphraseFile -PathType Leaf)) {
        $passphrase = (Get-Content -LiteralPath $passphraseFile -Raw).Trim()
    }
    if (-not $passphrase -and -not $NoPause) {
        $passphrase = Get-PlainText (Read-Host 'Įveskite backup šifravimo frazę' -AsSecureString)
    }
    if (-not $passphrase -or $passphrase.Length -lt 20) { Stop-WithError 'Nėra tinkamos backup šifravimo frazės.' }

    Write-Step 'Šifruotos kopijos iššifravimas'
    $decryptedTemporaryFile = Join-Path $env:TEMP ("MoneyFlow-restore-{0}.db" -f [guid]::NewGuid().ToString('N'))
    $previousPassphrase = $env:MONEY_FLOW_BACKUP_PASSPHRASE
    try {
        $env:MONEY_FLOW_BACKUP_PASSPHRASE = $passphrase
        Invoke-DatabaseTool @('--decrypt-db', $BackupFile, $decryptedTemporaryFile)
    } finally {
        if ($null -eq $previousPassphrase) { Remove-Item Env:MONEY_FLOW_BACKUP_PASSPHRASE -ErrorAction SilentlyContinue }
        else { $env:MONEY_FLOW_BACKUP_PASSPHRASE = $previousPassphrase }
        $passphrase = $null
    }
    $restoreSource = $decryptedTemporaryFile
} else {
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath ($BackupFile + $suffix)) {
            Stop-WithError "Šalia nešifruotos kopijos yra SQLite '$suffix'; pirmiausia sukurkite vientisą VACUUM INTO kopiją."
        }
    }
}

Write-Step 'Kopijos SQLite integrity_check'
Invoke-DatabaseTool @('--validate-db', $restoreSource)

Write-Host "`nBus atkurta iš: $BackupFile" -ForegroundColor Yellow
if (-not $Force) {
    $confirmation = Read-Host 'Dabartiniai duomenys bus pakeisti. Tęsti? Įveskite TAIP'
    if ($confirmation -cne 'TAIP') { Write-Host 'Atšaukta.'; Wait-Exit 0 }
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceWasRunning = $service -and $service.Status -eq 'Running'
if ($serviceWasRunning) {
    Write-Step "Serviso '$serviceName' stabdymas"
    Stop-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

try {
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $liveDatabase -PathType Leaf) {
        Write-Step 'Dabartinės DB rollback kopija'
        $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $preRestorePath = Join-Path $backupsDirectory "pre-restore-$timestamp.db"
        Copy-Item -LiteralPath $liveDatabase -Destination $preRestorePath
        foreach ($suffix in @('-wal', '-shm')) {
            if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
                Copy-Item -LiteralPath ($liveDatabase + $suffix) -Destination ($preRestorePath + $suffix)
            }
        }
    }

    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        Remove-Item -LiteralPath ($liveDatabase + $suffix) -Force -ErrorAction SilentlyContinue
    }
    Copy-Item -LiteralPath $restoreSource -Destination $liveDatabase -Force

    if ($serviceWasRunning) {
        Write-Step "Serviso '$serviceName' paleidimas ir readiness patikra"
        Start-Service $serviceName
        $ready = $false
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                $result = Invoke-RestMethod $readyUrl -TimeoutSec 3
                if ($result.status -eq 'ready') { $ready = $true; break }
            } catch { }
            Start-Sleep -Seconds 1
        }
        if (-not $ready) { throw 'Atkurta DB nepraėjo /ready patikros.' }
    }
} catch {
    $restoreError = $_.Exception.Message
    if ($serviceWasRunning) {
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
        (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    if ($preRestorePath -and (Test-Path -LiteralPath $preRestorePath)) {
        Write-Host 'Atkūrimas nepavyko – automatiškai grąžinama ankstesnė DB.' -ForegroundColor Yellow
        foreach ($suffix in @('', '-wal', '-shm', '-journal')) {
            Remove-Item -LiteralPath ($liveDatabase + $suffix) -Force -ErrorAction SilentlyContinue
        }
        Copy-Item -LiteralPath $preRestorePath -Destination $liveDatabase -Force
        foreach ($suffix in @('-wal', '-shm')) {
            if (Test-Path -LiteralPath ($preRestorePath + $suffix)) {
                Copy-Item -LiteralPath ($preRestorePath + $suffix) -Destination ($liveDatabase + $suffix)
            }
        }
    }
    if ($serviceWasRunning) { Start-Service $serviceName -ErrorAction SilentlyContinue }
    Stop-WithError $restoreError
} finally {
    if ($decryptedTemporaryFile) {
        Remove-Item -LiteralPath $decryptedTemporaryFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`n[GERAI] DB atkūrimas ir patikra baigti." -ForegroundColor Green
Wait-Exit 0
