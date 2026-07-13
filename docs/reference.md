# Techninis žinynas

Šiame faile laikomos detalės, kurių nereikia kasdieniam diegimui. Trumpa paleidimo instrukcija yra pagrindiniame [README](../README.md).

## Reikalavimai

| Įrankis | Kam reikalingas |
|---|---|
| .NET 10 SDK | Build, lokalus paleidimas ir serverio diegimas |
| Git | Projekto klonavimas ir atnaujinimas |
| Windows su administratoriaus teisėmis | Production serviso diegimas |
| Node.js | Tik lokaliai JavaScript sintaksės patikrai |

## Konfigūracija

Saugus struktūros pavyzdys yra [appsettings.example.json](../appsettings.example.json). Tikrų raktų nedėkite į Git sekamus failus.

| Nustatymas | Paskirtis |
|---|---|
| `MoneyFlow:DatabasePath` / `MONEY_FLOW_DB_PATH` | SQLite failo kelias |
| `MoneyFlow:ApiKeyFilePath` | Production API rakto failas |
| `MoneyFlow:EditPassphraseFilePath` | Production redagavimo slaptafrazės PBKDF2 hash failas |
| `MoneyFlow:BackupPassphraseFilePath` | Production backup šifravimo frazės failas |
| `MoneyFlow:ApiKey` | Raktas konfigūracijoje, production nerekomenduojama |
| `MONEY_FLOW_API_KEY` | API raktas lokaliai plėtrai |
| `MONEY_FLOW_EDIT_PASSPHRASE` | Redagavimo slaptafrazė lokaliai plėtrai / testams |
| `Kestrel:Endpoints:Http:Url` / `Kestrel__Endpoints__Http__Url` | Production klausomas HTTP adresas |
| `ASPNETCORE_URLS` | Lokalus URL, kai nėra konkretaus Kestrel endpointo |

Diegimo tinklo režimas saugomas `C:\ProgramData\PADS\MoneyFlow\Configuration\deployment-network-mode.txt`:

- `DirectLan` → Kestrel klauso `0.0.0.0:5000`, o diegimas atidaro TCP 5000 Domain/Private profiliams.
- `ReverseProxy` → Kestrel klauso tik `127.0.0.1:5000`, o TCP 5000 LAN ugniasienėje neatidaromas.

DB kelio prioritetas:

```text
MONEY_FLOW_DB_PATH → MoneyFlow:DatabasePath → programos data/ katalogas
```

API rakto prioritetas:

```text
ApiKeyFilePath → MoneyFlow:ApiKey → MONEY_FLOW_API_KEY
```

Redagavimo slaptafrazės prioritetas:

```text
EditPassphraseFilePath → MoneyFlow:EditPassphrase → MONEY_FLOW_EDIT_PASSPHRASE
```

## API prieiga

- `GET`, `HEAD` ir `OPTIONS` skaitymo užklausos vidiniame LAN yra atviros.
- Rankinio projekto redagavimo užklausoms reikia `X-Edit-Passphrase`. Web UI šią antraštę suformuoja iš redagavimo dialoge įvestos slaptafrazės.
- PAD importams, DB atkūrimui ir kitoms apsaugotoms mutacijoms reikia `X-Api-Key`.
- Be serveryje nustatyto atitinkamo rakto ar slaptafrazės apsaugota užklausa grąžina `503`.
- Trūkstamas arba neteisingas kredencialas grąžina `401`.
- Užklausos kūnas ribojamas iki 10 MB.

## Pagrindiniai endpointai

| Metodas ir kelias | Paskirtis |
|---|---|
| `POST /api/imports/monthly-flow` | Mėnesinio srauto importas |
| `POST /api/imports/contracts` | Sutarčių ir projektų verčių importas |
| `POST /api/access/edit/verify` | Redagavimo slaptafrazės patikra |
| `GET /api/imports/monthly-flow/status` | Importo būsena |
| `GET /api/projects` | Projektų sąrašas |
| `GET /api/projects/{code}/monthly-flow` | Projekto detalė |
| `GET /api/projects/{code}/ignored-rows` | Ignoruotos eilutės |
| `POST /api/maintenance/db-backup` | Vientisa pagalbinė SQLite kopija |
| `POST /api/maintenance/db-restore` | Online DB atkūrimas su rollback, serviso nestabdant |
| `GET /health` | Proceso gyvumas |
| `GET /ready` | DB pasiekiamumas ir `quick_check` |

Importo formatas ir validacija aprašyti [import-flow.md](import-flow.md).

## DB ir backup

- Gyva DB: `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db`.
- Į Git leidžiama kelti tik užšifruotą `*.mfbackup`.
- `Backup-MoneyFlowDb.bat` sukuria, patikrina ir AES-256-GCM formatu užšifruoja kopiją.
- `Restore-MoneyFlowDb.bat` prieš ir po atkūrimo tikrina DB, o nesėkmės atveju grąžina ankstesnę kopiją.
- `local-backups/` gali turėti nešifruotų duomenų ir niekada neturi būti keliamas į Git ar bendrą ZIP.

Visa procedūra pateikta [deployment.md](deployment.md).

## Patikros

```powershell
dotnet format --verify-no-changes
dotnet build -c Release
dotnet list PADS.MoneyFlow.Api.csproj package --vulnerable --include-transitive
Get-ChildItem wwwroot,docs -Recurse -Filter *.js | ForEach-Object { node --check $_.FullName }
.\Run-Tests.ps1
```

Tos pačios patikros vykdomos GitHub Actions CI.
