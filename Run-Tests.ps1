[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$shellPath = (Get-Process -Id $PID).Path

Get-ChildItem (Join-Path $projectRoot 'tests') -Filter '*.ps1' | Sort-Object Name | ForEach-Object {
    Write-Host "Running $($_.Name)" -ForegroundColor Cyan
    & $shellPath -NoProfile -File $_.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "$($_.Name) failed with exit code $LASTEXITCODE"
    }
}

Write-Host 'All integration checks passed.' -ForegroundColor Green
