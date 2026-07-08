[CmdletBinding()]
param(
    # Nesustabdo serviso pries kopijavima (naudoti tik testavimui arba kai servisas jau sustabdytas).
    [switch]$SkipServiceStop,
    # Daro tik lokalia kopija, nesiuncia i GitHub.
    [switch]$SkipGitHub,
    # Nelaukia Enter pabaigoje (automatiniam paleidimui).
    [switch]$NoPause
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

# Serviso stabdymui reikia administratoriaus teisių.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $SkipServiceStop -and -not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($SkipGitHub) { $arguments += '-SkipGitHub' }
    if ($NoPause) { $arguments += '-NoPause' }
    Write-Host 'Prašoma administratoriaus teisių...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$repoRoot = $PSScriptRoot
$repoBackupDirectory = Join-Path $repoRoot 'db-backups'
$serviceName = 'MoneyFlow'
$healthUrl = 'http://localhost:5000/health'
$keepCount = 30

Write-Host 'MoneyFlow DB atsarginė kopija' -ForegroundColor Green
Write-Host "Duomenų bazė: $liveDatabase"
Write-Host "Kopijos:      $backupsDirectory"

if (-not (Test-Path -LiteralPath $liveDatabase -PathType Leaf)) {
    Stop-WithError "Nerasta duomenų bazė: $liveDatabase"
}
if (-not (Test-Path -LiteralPath $backupsDirectory)) {
    New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceWasStopped = $false

if (-not $SkipServiceStop -and $service -and $service.Status -eq 'Running') {
    Write-Step "Serviso '$serviceName' stabdymas (trumpam, kad kopija būtų vientisa)"
    Stop-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $serviceWasStopped = $true
} elseif ($SkipServiceStop) {
    Write-Host 'DĖMESIO: servisas nestabdomas (-SkipServiceStop) – kopija gali būti nevientisa, jei tuo metu vyksta įrašymas.' -ForegroundColor Yellow
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupName = "monthly-money-flow-backup-$timestamp.db"
$backupPath = Join-Path $backupsDirectory $backupName

try {
    Write-Step 'Duomenų bazės kopijavimas'
    Copy-Item -LiteralPath $liveDatabase -Destination $backupPath
    foreach ($suffix in @('-wal', '-shm')) {
        if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
            Copy-Item -LiteralPath ($liveDatabase + $suffix) -Destination ($backupPath + $suffix)
        }
    }
    $backupSize = [math]::Round((Get-Item -LiteralPath $backupPath).Length / 1KB)
    Write-Host "[GERAI] Sukurta kopija: $backupName ($backupSize KB)" -ForegroundColor Green
} finally {
    if ($serviceWasStopped) {
        Write-Step "Serviso '$serviceName' paleidimas"
        Start-Service -Name $serviceName
    }
}

if ($serviceWasStopped) {
    $healthy = $false
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
            if ($health.status -eq 'ok') { $healthy = $true; break }
        } catch { Start-Sleep -Seconds 1 }
    }
    if ($healthy) {
        Write-Host '[GERAI] Servisas vėl veikia (health patikra sėkminga).' -ForegroundColor Green
    } else {
        Write-Host "DĖMESIO: servisas paleistas, bet $healthUrl neatsako. Patikrinkite servisą rankiniu būdu!" -ForegroundColor Red
    }
}

Write-Step "Senų kopijų valymas (paliekama naujausių: $keepCount)"
$oldBackups = @(Get-ChildItem -LiteralPath $backupsDirectory -Filter 'monthly-money-flow-backup-*.db' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip $keepCount)
foreach ($oldBackup in $oldBackups) {
    Remove-Item -LiteralPath $oldBackup.FullName -Force
    foreach ($suffix in @('-wal', '-shm')) {
        if (Test-Path -LiteralPath ($oldBackup.FullName + $suffix)) {
            Remove-Item -LiteralPath ($oldBackup.FullName + $suffix) -Force
        }
    }
}
Write-Host ("Ištrinta senų kopijų: {0}" -f $oldBackups.Count)

if (-not $SkipGitHub) {
    Write-Step 'Kopijos siuntimas į GitHub (projekto repozitorija, katalogas db-backups)'
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) {
        Write-Host 'DĖMESIO: nerastas git – kopija liko tik lokaliai.' -ForegroundColor Yellow
    } elseif (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
        Write-Host "DĖMESIO: $repoRoot nėra git repozitorija (parsisiųsta kaip ZIP?) – kopija liko tik lokaliai." -ForegroundColor Yellow
    } else {
        & git -C $repoRoot pull --ff-only --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'DĖMESIO: nepavyko atsinaujinti repozitorijos (git pull). Bandoma siųsti vis tiek.' -ForegroundColor Yellow
        }

        # Repozitorijoje laikoma tik naujausia kopija – senesnės versijos lieka git istorijoje.
        if (-not (Test-Path -LiteralPath $repoBackupDirectory)) {
            New-Item -ItemType Directory -Path $repoBackupDirectory -Force | Out-Null
        }
        Get-ChildItem -LiteralPath $repoBackupDirectory -File |
            Where-Object { $_.Name -like 'monthly-money-flow-latest.db*' } |
            Remove-Item -Force
        $latestPath = Join-Path $repoBackupDirectory 'monthly-money-flow-latest.db'
        Copy-Item -LiteralPath $backupPath -Destination $latestPath
        foreach ($suffix in @('-wal', '-shm')) {
            if (Test-Path -LiteralPath ($backupPath + $suffix)) {
                Copy-Item -LiteralPath ($backupPath + $suffix) -Destination ($latestPath + $suffix)
            }
        }

        $changes = & git -C $repoRoot status --porcelain -- db-backups
        if (-not $changes) {
            Write-Host '[GERAI] GitHub kopija jau naujausia (duomenys nepasikeitė nuo praeito karto).' -ForegroundColor Green
        } else {
            $pushOk = $false
            & git -C $repoRoot add -A -- db-backups
            & git -C $repoRoot commit --quiet -m "DB backup $timestamp" -- db-backups
            if ($LASTEXITCODE -eq 0) {
                & git -C $repoRoot push --quiet origin HEAD
                if ($LASTEXITCODE -eq 0) { $pushOk = $true }
            }
            if ($pushOk) {
                Write-Host '[GERAI] Kopija išsiųsta į GitHub (db-backups/monthly-money-flow-latest.db).' -ForegroundColor Green
            } else {
                Write-Host 'DĖMESIO: išsiųsti į GitHub nepavyko – kopija liko lokaliai. Patikrinkite interneto ryšį / git prisijungimą.' -ForegroundColor Yellow
            }
        }
    }
}

Write-Host "`nAtsarginė kopija baigta: $backupPath" -ForegroundColor Green
Wait-Exit 0
