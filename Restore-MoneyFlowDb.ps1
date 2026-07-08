[CmdletBinding()]
param(
    # Pilnas kelias iki atkuriamos kopijos failo. Jei nenurodyta, parodo lokaliu kopiju sarasa.
    [string]$BackupFile,
    # Nelaukia Enter pabaigoje (automatiniam paleidimui; reikalauja -BackupFile ir -Force).
    [switch]$NoPause,
    # Praleidzia patvirtinimo klausima (naudoti atsargiai).
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

# Serviso stabdymui reikia administratoriaus teisių.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if (-not [string]::IsNullOrWhiteSpace($BackupFile)) { $arguments += @('-BackupFile', "`"$BackupFile`"") }
    if ($NoPause) { $arguments += '-NoPause' }
    if ($Force) { $arguments += '-Force' }
    Write-Host 'Prašoma administratoriaus teisių...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$repoBackupDirectory = Join-Path $PSScriptRoot 'db-backups'
$serviceName = 'MoneyFlow'
$healthUrl = 'http://localhost:5000/health'

Write-Host 'MoneyFlow DB atstatymas iš atsarginės kopijos' -ForegroundColor Green
Write-Host "Duomenų bazė: $liveDatabase"

if ([string]::IsNullOrWhiteSpace($BackupFile)) {
    # Rodomos lokalios kopijos (ProgramData\...\Backups) ir kopija is projekto katalogo db-backups (is GitHub).
    $candidates = @()
    if (Test-Path -LiteralPath $backupsDirectory) {
        $candidates += @(Get-ChildItem -LiteralPath $backupsDirectory -Filter '*.db' -File)
    }
    if (Test-Path -LiteralPath $repoBackupDirectory) {
        $candidates += @(Get-ChildItem -LiteralPath $repoBackupDirectory -Filter '*.db' -File)
    }
    $candidates = @($candidates | Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -eq 0) {
        Stop-WithError "Nerasta nė vienos kopijos (nei $backupsDirectory, nei $repoBackupDirectory). Parsisiųskite projektą iš GitHub su db-backups katalogu arba nurodykite failą: -BackupFile <kelias>"
    }

    Write-Step 'Rastos atsarginės kopijos'
    for ($index = 0; $index -lt $candidates.Count; $index++) {
        $file = $candidates[$index]
        $sizeKb = [math]::Round($file.Length / 1KB)
        Write-Host ("[{0,2}] {1}  {2,8} KB  {3:yyyy-MM-dd HH:mm}" -f ($index + 1), $file.Name.PadRight(55), $sizeKb, $file.LastWriteTime)
    }
    $answer = Read-Host "`nĮveskite kopijos numerį (1-$($candidates.Count)) arba Enter, kad atšauktumėte"
    $selection = 0
    if (-not [int]::TryParse($answer, [ref]$selection) -or $selection -lt 1 -or $selection -gt $candidates.Count) {
        Write-Host 'Atšaukta.' -ForegroundColor Yellow
        Wait-Exit 0
    }
    $BackupFile = $candidates[$selection - 1].FullName
}

if (-not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) {
    Stop-WithError "Nerastas kopijos failas: $BackupFile"
}
$BackupFile = (Resolve-Path -LiteralPath $BackupFile).Path
if ((Get-Item -LiteralPath $BackupFile).Length -eq 0) {
    Stop-WithError 'Pasirinkta kopija yra tuščias failas.'
}

Write-Host "`nBus atstatoma iš: $BackupFile" -ForegroundColor Yellow
Write-Host 'DĖMESIO: dabartiniai duomenys bus pakeisti kopijos duomenimis!' -ForegroundColor Yellow
Write-Host '(Prieš tai dabartinė DB bus išsaugota kaip pre-restore kopija.)'
if (-not $Force) {
    $confirmation = Read-Host "Jei tikrai norite tęsti, įveskite TAIP"
    if ($confirmation -cne 'TAIP') {
        Write-Host 'Atšaukta.' -ForegroundColor Yellow
        Wait-Exit 0
    }
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceWasStopped = $false
if ($service -and $service.Status -eq 'Running') {
    Write-Step "Serviso '$serviceName' stabdymas"
    Stop-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $serviceWasStopped = $true
} elseif (-not $service) {
    Write-Host "DĖMESIO: servisas '$serviceName' nerastas – atstatomas tik failas." -ForegroundColor Yellow
}

try {
    if (Test-Path -LiteralPath $liveDatabase -PathType Leaf) {
        Write-Step 'Dabartinės DB saugumo kopija (pre-restore)'
        if (-not (Test-Path -LiteralPath $backupsDirectory)) {
            New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
        }
        $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $preRestorePath = Join-Path $backupsDirectory "pre-restore-$timestamp.db"
        Copy-Item -LiteralPath $liveDatabase -Destination $preRestorePath
        foreach ($suffix in @('-wal', '-shm')) {
            if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
                Copy-Item -LiteralPath ($liveDatabase + $suffix) -Destination ($preRestorePath + $suffix)
            }
        }
        Write-Host "[GERAI] Išsaugota: $preRestorePath" -ForegroundColor Green
    }

    Write-Step 'Kopijos atstatymas'
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
            Remove-Item -LiteralPath ($liveDatabase + $suffix) -Force
        }
    }
    Copy-Item -LiteralPath $BackupFile -Destination $liveDatabase -Force
    foreach ($suffix in @('-wal', '-shm')) {
        if (Test-Path -LiteralPath ($BackupFile + $suffix)) {
            Copy-Item -LiteralPath ($BackupFile + $suffix) -Destination ($liveDatabase + $suffix) -Force
        }
    }
    Write-Host '[GERAI] Duomenų bazė atstatyta.' -ForegroundColor Green
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
        Write-Host '[GERAI] Servisas veikia (health patikra sėkminga).' -ForegroundColor Green
    } else {
        Write-Host "DĖMESIO: servisas paleistas, bet $healthUrl neatsako. Patikrinkite servisą rankiniu būdu!" -ForegroundColor Red
    }
}

Write-Host "`nAtstatymas baigtas." -ForegroundColor Green
Wait-Exit 0
