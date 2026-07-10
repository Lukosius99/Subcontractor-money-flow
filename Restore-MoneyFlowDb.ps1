[CmdletBinding()]
param(
    [string]$BackupFile,
    [string]$BaseUrl = 'http://localhost:5000',
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
function Invoke-RestoreUpload(
    [string]$Uri,
    [string]$Path,
    [string]$ApiKey
) {
    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        $Uri)
    $multipart = [System.Net.Http.MultipartFormDataContent]::new()
    $stream = $null
    $fileContent = $null
    $response = $null
    try {
        if (-not $request.Headers.TryAddWithoutValidation('X-Api-Key', $ApiKey)) {
            throw 'Nepavyko pridėti X-Api-Key antraštės.'
        }
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $fileContent = [System.Net.Http.StreamContent]::new($stream)
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new(
            'application/octet-stream')
        $multipart.Add($fileContent, 'file', [IO.Path]::GetFileName($Path))
        $request.Content = $multipart

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            $message = try { ($body | ConvertFrom-Json).error } catch { $body }
            if (-not $message) { $message = $response.ReasonPhrase }
            throw "Serveris grąžino HTTP $([int]$response.StatusCode): $message"
        }
        return $body | ConvertFrom-Json
    }
    finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
        $multipart.Dispose()
        if ($fileContent) { $fileContent.Dispose() }
        if ($stream) { $stream.Dispose() }
        $client.Dispose()
    }
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$apiKeyFile = Join-Path $dataDirectory 'Configuration\api-key.txt'
$repoBackupDirectory = Join-Path $PSScriptRoot 'db-backups'
$restoreUrl = $BaseUrl.TrimEnd('/') + '/api/maintenance/db-restore'

try {
    Write-Host 'MoneyFlow DB atkūrimas be serviso stabdymo' -ForegroundColor Green

    if (-not $BackupFile) {
        $candidates = @()
        if (Test-Path $backupsDirectory) {
            $candidates += @(Get-ChildItem $backupsDirectory -File |
                Where-Object Extension -In '.db', '.mfbackup')
        }
        if (Test-Path $repoBackupDirectory) {
            $candidates += @(Get-ChildItem $repoBackupDirectory -File |
                Where-Object Extension -In '.db', '.mfbackup')
        }
        $candidates = @($candidates | Sort-Object LastWriteTime -Descending)
        if ($candidates.Count -eq 0) {
            Stop-WithError 'Nerasta DB arba šifruotų .mfbackup kopijų.'
        }

        Write-Step 'Rastos atsarginės kopijos'
        for ($index = 0; $index -lt $candidates.Count; $index++) {
            $file = $candidates[$index]
            Write-Host ("[{0,2}] {1}  {2,8} KB  {3:yyyy-MM-dd HH:mm}" -f `
                ($index + 1), $file.Name.PadRight(55),
                [math]::Round($file.Length / 1KB), $file.LastWriteTime)
        }
        $answer = Read-Host "`nĮveskite kopijos numerį arba Enter, kad atšauktumėte"
        $selection = 0
        if (-not [int]::TryParse($answer, [ref]$selection) `
            -or $selection -lt 1 `
            -or $selection -gt $candidates.Count) {
            Write-Host 'Atšaukta.' -ForegroundColor Yellow
            Wait-Exit 0
        }
        $BackupFile = $candidates[$selection - 1].FullName
    }

    if (-not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) {
        Stop-WithError "Nerasta kopija: $BackupFile"
    }
    $BackupFile = (Resolve-Path -LiteralPath $BackupFile).Path

    $apiKey = $env:MONEY_FLOW_API_KEY
    if (-not $apiKey -and (Test-Path -LiteralPath $apiKeyFile -PathType Leaf)) {
        $apiKey = (Get-Content -LiteralPath $apiKeyFile -Raw).Trim()
    }
    if (-not $apiKey -and -not $NoPause) {
        $apiKey = Get-PlainText (Read-Host 'Įveskite MoneyFlow API raktą' -AsSecureString)
    }
    if (-not $apiKey -or $apiKey.Length -lt 16) {
        Stop-WithError 'Nėra tinkamo MoneyFlow API rakto.'
    }

    Write-Host "`nBus atkurta iš: $BackupFile" -ForegroundColor Yellow
    Write-Host 'Servisas nebus stabdomas. Atkūrimo metu naujos DB užklausos trumpam gaus 503.'
    if (-not $Force) {
        $confirmation = Read-Host 'Dabartiniai duomenys bus pakeisti. Tęsti? Įveskite TAIP'
        if ($confirmation -cne 'TAIP') {
            Write-Host 'Atšaukta.'
            Wait-Exit 0
        }
    }

    Write-Step 'Kopijos siuntimas veikiančiam servisui'
    $result = Invoke-RestoreUpload -Uri $restoreUrl -Path $BackupFile -ApiKey $apiKey
    if (-not $result.restored -or $result.serviceRestarted) {
        throw "Netikėtas atkūrimo atsakymas: $($result | ConvertTo-Json -Compress)"
    }

    $ready = Invoke-RestMethod ($BaseUrl.TrimEnd('/') + '/ready') -TimeoutSec 5
    if ($ready.status -ne 'ready') {
        throw 'Po atkūrimo /ready negrąžino ready.'
    }

    Write-Host "`n[GERAI] DB atkurta, servisas nebuvo perkrautas." -ForegroundColor Green
    Write-Host "Rollback kopija: $($result.rollbackFileName)"
    $apiKey = $null
    Wait-Exit 0
}
catch {
    $apiKey = $null
    Stop-WithError $_.Exception.Message
}
