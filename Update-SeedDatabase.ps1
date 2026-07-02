[CmdletBinding()]
param(
    [string]$SourceDatabase
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$destination = Join-Path $repoRoot 'seed\monthly-money-flow.db'
$serviceName = 'MoneyFlow'
$serviceWasRunning = $false

if ([string]::IsNullOrWhiteSpace($SourceDatabase)) {
    $candidates = @(
        (Join-Path $env:ProgramData 'PADS\MoneyFlow\monthly-money-flow.db'),
        (Join-Path $repoRoot 'data\monthly-money-flow.db')
    )
    $SourceDatabase = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($SourceDatabase) -or -not (Test-Path -LiteralPath $SourceDatabase)) {
    throw 'Nerasta darbinė DB. Nurodykite ją: .\Update-SeedDatabase.ps1 -SourceDatabase "C:\kelias\monthly-money-flow.db"'
}
$SourceDatabase = (Resolve-Path -LiteralPath $SourceDatabase).Path
if ($SourceDatabase -eq (Resolve-Path -LiteralPath $destination -ErrorAction SilentlyContinue).Path) {
    throw 'Šaltinis negali būti tas pats seed failas.'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
try {
    if ($service -and $service.Status -eq 'Running' -and $SourceDatabase.StartsWith($env:ProgramData, [StringComparison]::OrdinalIgnoreCase)) {
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isAdmin) { throw 'MoneyFlow veikia. Paleiskite PowerShell kaip administratorius, kad scenarijus galėtų saugiai sustabdyti paslaugą.' }
        Write-Host 'Stabdoma MoneyFlow paslauga, kad SQLite užbaigtų WAL įrašus...' -ForegroundColor Yellow
        Stop-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        $serviceWasRunning = $true
    }

    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath ($SourceDatabase + $suffix)) {
            throw "Šalia DB liko aktyvus SQLite failas '$suffix'. Uždarykite DB naudojančią programą ir bandykite dar kartą."
        }
    }

    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    $temporary = "$destination.tmp"
    Copy-Item -LiteralPath $SourceDatabase -Destination $temporary -Force

    $sqlite = Get-Command sqlite3.exe -ErrorAction SilentlyContinue
    if ($sqlite) {
        $integrity = (& $sqlite.Source $temporary 'PRAGMA integrity_check;') -join "`n"
        if ($LASTEXITCODE -ne 0 -or $integrity.Trim() -ne 'ok') { throw "SQLite integrity_check nepavyko: $integrity" }
        Write-Host '[GERAI] SQLite integrity_check: ok' -ForegroundColor Green
    } else {
        Write-Warning 'sqlite3.exe nerastas, todėl automatinė integrity_check patikra praleista.'
    }

    Move-Item -LiteralPath $temporary -Destination $destination -Force
    $item = Get-Item -LiteralPath $destination
    Write-Host "[GERAI] Nukopijuota: $SourceDatabase" -ForegroundColor Green
    Write-Host "[GERAI] Seed DB: $destination ($($item.Length) baitų)" -ForegroundColor Green
    Write-Host 'WAL/SHM/journal failai į repo nekopijuojami.'
    Write-Warning 'Ši DB turi realius įmonės duomenis. Repozitorija privalo likti PRIVATI.'
}
finally {
    Remove-Item -LiteralPath "$destination.tmp" -Force -ErrorAction SilentlyContinue
    if ($serviceWasRunning) {
        Start-Service -Name $serviceName
        Write-Host 'MoneyFlow paslauga vėl paleista.' -ForegroundColor Green
    }
}
