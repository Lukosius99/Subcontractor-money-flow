# LAN Deployment Guide

Internal Windows deployment of the Monthly Money Flow app.

**Architecture**

```
   Clients on LAN                         Server (Windows)
   ──────────────                         ─────────────────────────────────
   https://moneyflow.pads.local  ─────►   Caddy (443/80, TLS)
                                              │
                                              └─►  Kestrel @ http://127.0.0.1:5000
                                                       │
                                                       └─►  SQLite file
                                                            C:\ProgramData\PADS\MoneyFlow\
```

- Kestrel **never** binds to a public interface — only `127.0.0.1`.
- Caddy terminates TLS and reverse-proxies to Kestrel.
- The .NET app runs as a Windows Service (`PADS Monthly Money Flow`).
- SQLite database lives outside the publish folder so redeploys never touch data.

---

## 1. Prerequisites (on the server)

- Windows 10/11 or Windows Server with admin rights.
- [.NET 8 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0) (ASP.NET Core Runtime, not just the SDK — the SDK is only needed if you publish on the server itself).
- [Caddy 2](https://caddyserver.com/download) for Windows.

---

## 2. Publish the app

From your dev machine (or the server if it has the SDK):

```powershell
dotnet publish .\PADS.MoneyFlow.Api.csproj `
  -c Release `
  -o C:\Apps\MoneyFlow `
  --self-contained false
```

Result: `C:\Apps\MoneyFlow\PADS.MoneyFlow.Api.exe` plus `appsettings.json`, `appsettings.Production.json`, `wwwroot\`, and dependencies.

> The `appsettings.Production.json` ships with the publish output because the `.csproj` marks it `CopyToOutputDirectory=PreserveNewest`. Verify both `appsettings*.json` files are present in `C:\Apps\MoneyFlow\` after publish.

Copy the folder to the server (e.g. via `robocopy` or any file share).

---

## 3. Prepare the data folder

```powershell
New-Item -ItemType Directory -Force `
  -Path "C:\ProgramData\PADS\MoneyFlow"
```

This is where the SQLite file lives. The path is set in `appsettings.Production.json` under `MoneyFlow:DatabasePath` — change it there if you prefer a different location (network share, dedicated data drive, etc.).

---

## 4. Install as a Windows Service

Run **elevated PowerShell**:

```powershell
# Create the service.
sc.exe create MoneyFlow `
  binPath= "C:\Apps\MoneyFlow\PADS.MoneyFlow.Api.exe" `
  start= auto `
  DisplayName= "PADS Monthly Money Flow"

sc.exe description MoneyFlow "Internal subcontractor monthly money flow tracker"

# Make sure it runs as Production, not Development.
[Environment]::SetEnvironmentVariable(
  "ASPNETCORE_ENVIRONMENT", "Production", "Machine")

# Make Kestrel listen only on loopback (also set in appsettings.Production.json
# — this env var is the belt-and-braces safety).
[Environment]::SetEnvironmentVariable(
  "ASPNETCORE_URLS", "http://127.0.0.1:5000", "Machine")

# Start it now.
sc.exe start MoneyFlow
```

Verify:

```powershell
Get-Service MoneyFlow            # should show Status: Running
Invoke-WebRequest http://127.0.0.1:5000/  # should return 200 + HTML
```

### Recommended: run under a dedicated service account

By default the service runs as `LocalSystem` — too much privilege.

```powershell
# Create a service-only local account.
$pwd = Read-Host -AsSecureString "Password for svc-moneyflow"
New-LocalUser -Name "svc-moneyflow" -Password $pwd `
  -PasswordNeverExpires -AccountNeverExpires `
  -Description "Service account for Monthly Money Flow"

# Grant the data folder.
icacls "C:\ProgramData\PADS\MoneyFlow" /grant "svc-moneyflow:(OI)(CI)M" /T

# Grant the app folder (read+execute only).
icacls "C:\Apps\MoneyFlow" /grant "svc-moneyflow:(OI)(CI)RX" /T

# Reassign service identity. Account must have 'Log on as a service' right
# (Local Security Policy → User Rights Assignment → Log on as a service).
sc.exe config MoneyFlow obj= ".\svc-moneyflow" password= "<password>"
Restart-Service MoneyFlow
```

### Lifecycle commands

```powershell
sc.exe start  MoneyFlow
sc.exe stop   MoneyFlow
Restart-Service MoneyFlow
sc.exe query  MoneyFlow
sc.exe delete MoneyFlow      # uninstall (stop it first)
```

---

## 5. Reverse proxy — Caddy

Install Caddy and create `C:\Caddy\Caddyfile`:

```caddy
moneyflow.pads.local {
    reverse_proxy 127.0.0.1:5000 {
        # PAD/Cloud Flow uploads can be large; keep streaming.
        flush_interval -1
    }

    # Use Caddy's internal CA for LAN-only hostnames.
    # For a public hostname, replace with: tls you@example.com
    tls internal

    encode gzip

    log {
        output file C:\Logs\caddy-moneyflow.log {
            roll_size 50mb
            roll_keep 10
        }
        format console
    }
}
```

Install Caddy as a service (one-time):

```powershell
# From the folder containing caddy.exe and Caddyfile:
New-Item -ItemType Directory -Force -Path C:\Logs | Out-Null
.\caddy.exe validate --config .\Caddyfile
.\caddy.exe run --config .\Caddyfile           # smoke test in foreground

# Then install permanently. Easiest: NSSM, or use the official service wrapper:
# https://caddyserver.com/docs/running#windows-service
```

Configure clients (or your internal DNS server) so `moneyflow.pads.local` resolves to the server's IP. Quick test:

```powershell
# On a client machine:
Add-Content C:\Windows\System32\drivers\etc\hosts "192.168.1.42  moneyflow.pads.local"
```

(Use proper DNS once it works.)

With `tls internal`, Caddy issues a cert from its own root CA. To stop browsers warning, install the Caddy root certificate on each client:

```powershell
# On the server, find the root cert path:
caddy environ | Select-String DataDir
# Typically: %AppData%\Caddy\pki\authorities\local\root.crt
# Distribute that .crt and import into "Trusted Root Certification Authorities" on each client.
```

For a tidier setup, use your AD Certificate Services CA and replace `tls internal` with `tls <cert.pem> <key.pem>`.

---

## 6. Firewall

Only open the proxy ports, not Kestrel's:

```powershell
New-NetFirewallRule -DisplayName "Caddy HTTPS (MoneyFlow)" `
  -Direction Inbound -Action Allow `
  -Protocol TCP -LocalPort 443 -Profile Domain,Private

New-NetFirewallRule -DisplayName "Caddy HTTP redirect (MoneyFlow)" `
  -Direction Inbound -Action Allow `
  -Protocol TCP -LocalPort 80 -Profile Domain,Private
```

**Do not** open port 5000 — that would let LAN clients bypass Caddy and hit Kestrel directly.

Optionally restrict to your LAN subnet:

```powershell
Set-NetFirewallRule -DisplayName "Caddy HTTPS (MoneyFlow)" `
  -RemoteAddress 192.168.1.0/24
```

---

## 7. Backups

SQLite is a single file. A nightly Scheduled Task is enough.

Save as `C:\Apps\MoneyFlow\backup.ps1`:

```powershell
$ErrorActionPreference = 'Stop'

$source     = 'C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db'
$backupRoot = 'D:\Backups\MoneyFlow'   # ideally a different physical disk or share
$stamp      = Get-Date -Format 'yyyyMMdd-HHmm'
$destination = Join-Path $backupRoot "monthly-money-flow-$stamp.db"

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

# Use the SQLite Online Backup API via sqlite3.exe for a hot, consistent copy.
# Falls back to file copy if sqlite3.exe is unavailable.
$sqlite = (Get-Command sqlite3.exe -ErrorAction SilentlyContinue)?.Source
if ($sqlite) {
    & $sqlite $source ".backup `"$destination`""
} else {
    Copy-Item $source $destination
}

# Keep last 30 backups.
Get-ChildItem $backupRoot -Filter 'monthly-money-flow-*.db' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip 30 |
    Remove-Item -Force
```

Schedule it:

```powershell
$action = New-ScheduledTaskAction `
  -Execute 'powershell.exe' `
  -Argument '-NoProfile -ExecutionPolicy Bypass -File "C:\Apps\MoneyFlow\backup.ps1"'

$trigger = New-ScheduledTaskTrigger -Daily -At 02:30

Register-ScheduledTask -TaskName 'MoneyFlow Backup' `
  -Action $action -Trigger $trigger `
  -User 'SYSTEM' -RunLevel Highest
```

---

## 8. Update / redeploy procedure

```powershell
# 1. Build the new bits on dev box.
dotnet publish -c Release -o C:\Builds\MoneyFlow

# 2. Copy to server (e.g. via UNC).
robocopy \\dev\Builds\MoneyFlow \\server\C$\Apps\MoneyFlow\.staging /MIR

# 3. On the server: swap.
Stop-Service MoneyFlow
robocopy C:\Apps\MoneyFlow\.staging C:\Apps\MoneyFlow /MIR /XD .staging
Start-Service MoneyFlow
```

Rollback = keep the previous folder; swap back and restart.

> Do **not** delete `C:\ProgramData\PADS\MoneyFlow\` — that's where the database lives.

---

## 9. Validation checklist

| Check | Command / Action | Expected |
|---|---|---|
| Service exists | `Get-Service MoneyFlow` | Status `Running`, StartType `Automatic` |
| Survives reboot | `Restart-Computer` then check | Service auto-starts |
| Kestrel only on loopback | `netstat -an \| Select-String "127.0.0.1:5000"` | One LISTENING line |
| Not exposed on LAN | `netstat -an \| Select-String "0.0.0.0:5000"` | **No** match |
| Local HTTP works | `Invoke-WebRequest http://127.0.0.1:5000/` | 200 OK, HTML body |
| Hostname resolves | `Resolve-DnsName moneyflow.pads.local` | Returns server IP |
| HTTPS works | Open `https://moneyflow.pads.local/` in browser | Projects register loads |
| PAD endpoint reachable | `Invoke-RestMethod -Method POST -Uri "https://moneyflow.pads.local/api/imports/monthly-flow?sourceFileName=test.json" -ContentType application/json -InFile sample.json` | 200 OK with import summary |
| Contract endpoint reachable | `POST https://moneyflow.pads.local/api/imports/contracts` from PAD | 200 OK |
| DB persists across restarts | Import data, `Restart-Service MoneyFlow`, reload page | Data still visible |
| Logs | `Get-EventLog -LogName Application -Source "PADS Monthly Money Flow" -Newest 20` | App events present |

---

## 10. Troubleshooting

**Service starts then immediately stops.** Check the Application event log:
```powershell
Get-EventLog -LogName Application -Newest 20 -EntryType Error |
  Where-Object { $_.Source -like '*MoneyFlow*' -or $_.Source -eq '.NET Runtime' } |
  Format-List TimeGenerated, Source, Message
```
Usual cause: missing .NET 8 runtime on the server, or the DB folder isn't writable by the service account.

**`Access denied` on the database.** The service account doesn't have `Modify` on `C:\ProgramData\PADS\MoneyFlow`. Re-run the `icacls` grant from §4.

**PAD posts time out via Caddy.** Large uploads — increase Caddy's `request_body max_size` if needed:
```caddy
moneyflow.pads.local {
    reverse_proxy 127.0.0.1:5000
    request_body { max_size 50MB }
    tls internal
}
```

**Hostname not resolving on clients.** Either you haven't pushed the DNS record yet, or the client is using its own DNS. Use `nslookup moneyflow.pads.local`. Hosts file is a fine stopgap for a handful of users.

**Caddy cert warnings.** With `tls internal`, every client needs the Caddy root cert imported. Either distribute it (Group Policy → Computer Configuration → Trusted Root CAs) or use a real cert from your AD CA.

---

## What did NOT change

These are **unchanged** by this deployment work:

- `POST /api/imports/monthly-flow` — same path, same request shape, same response.
- `POST /api/imports/contracts` — same.
- PAD request format and Cloud Flow contracts — untouched.
- All upsert/import logic in `MonthlyFlowImportService` and `MonthlyFlowStore` — untouched.

Only the hosting model (service vs. console), the DB default path resolution, and a new production config layer were added.
