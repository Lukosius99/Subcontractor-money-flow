# Subcontractor Money Flow

[![CI](https://github.com/Lukosius99/Subcontractor-money-flow/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Lukosius99/Subcontractor-money-flow/actions/workflows/ci.yml)

Vidinė įmonės LAN programa, skirta subrangovų mėnesiniams pinigų srautams pagal projektą ir objektą peržiūrėti.

![Duomenų kelio schema](docs/subrangos-duomenu-kelias-3d/preview.png)

Interaktyvi schemos versija saugoma [docs/subrangos-duomenu-kelias-3d](docs/subrangos-duomenu-kelias-3d/index.html). Jos neviešinkite per public GitHub Pages, jei vidinių sistemų pavadinimai nėra skirti viešumai.

## Kaip veikia

```text
SharePoint Excel → Power Automate Cloud Flow → JSON → Power Automate Desktop
    → vietinis ASP.NET Core API → SQLite → statinė naršyklės sąsaja
```

API ir UI yra vienas ASP.NET Core procesas: jis priima PAD importus, saugo duomenis vietiniame SQLite faile ir pateikia naršyklės sąsają. Repozitorijoje nėra nešifruotos darbinės DB; `db-backups/` gali būti tik šifruotas `.mfbackup` failas. Išsamiau: [architektūra](docs/architecture.md) ir [importo eiga](docs/import-flow.md).

## Technologijos

- .NET 10 / ASP.NET Core Minimal API
- Entity Framework Core 10 + SQLite
- HTML, CSS ir paprastas JavaScript be frontend build žingsnio
- ExcelJS eksportui į `.xlsx` (lokali minifikuota kopija)
- PowerShell integraciniams testams ir Windows diegimui

## Reikalavimai

| Įrankis | Kam reikalingas | Patikra |
|---|---|---|
| .NET 10 SDK | Visada — build, paleidimui ir diegimui | `dotnet --version` → `10.x` |
| Git | Repozitorijai klonuoti / atnaujinti | `git --version` |
| Node.js | Tik JavaScript sintaksės patikroms (nebūtina) | `node --version` |
| Windows + administratoriaus teisės | Tik gamybos diegimui į serverį | — |

---

## 1. Vietinis paleidimas (plėtrai ir peržiūrai)

**1 žingsnis.** Klonuokite repozitoriją:

```powershell
git clone https://github.com/Lukosius99/Subcontractor-money-flow.git
cd Subcontractor-money-flow
```

**2 žingsnis.** (Nebūtina) Nustatykite importo API raktą. Jis reikalingas tik `POST /api/imports/*` užklausoms — be jo programa pasileidžia ir UI veikia, tik importai grąžina 503:

```powershell
$env:MONEY_FLOW_API_KEY = "dev-only-key-at-least-16-chars"
```

**3 žingsnis.** Paleiskite programą:

```powershell
dotnet run
```

**4 žingsnis.** Atidarykite naršyklėje terminale parodytą `localhost` adresą.

**5 žingsnis.** Pirmo paleidimo metu automatiškai sukuriama tuščia DB (jei `MONEY_FLOW_DB_PATH` nenurodytas — programos katalogo `data/` poaplankyje, su `dotnet run` tai `bin/Debug/net10.0/data/monthly-money-flow.db`; failas ignoruojamas git). Projektų sąrašas bus tuščias, kol neatliktas pirmas importas — importo formatas aprašytas [docs/import-flow.md](docs/import-flow.md), o veikiančio pavyzdžio galima pasižiūrėti bet kuriame `tests/*.ps1` scenarijuje.

---

## 2. Diegimas gamybos serveryje (Windows LAN)

**1 žingsnis.** Serveryje įdiekite .NET 10 SDK ir klonuokite repozitoriją. ZIP netinka, jei backup turi būti siunčiamas į GitHub. Backup serverio Git paskyrai iš anksto nustatykite `user.name`, `user.email` ir push teisę į privatų repo.

**2 žingsnis.** Administratoriaus PowerShell lange, repozitorijos kataloge, paleiskite diegimo skriptą:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
```

Skriptas automatiškai:

- publikuoja programą į `C:\Program Files\PADS\MoneyFlow`;
- sukuria ir paleidžia Windows paslaugą `MoneyFlow` (portas 5000);
- paruošia duomenų aplanką `C:\ProgramData\PADS\MoneyFlow` (esamos gyvos DB **niekada neperrašo**);
- sukuria ugniasienės taisyklę TCP 5000 (tik Domain ir Private profiliai);
- pabaigoje patikrina `http://localhost:5000/health`.

Jei naujam serveriui reikia pradėti nuo šifruotos GitHub DB kopijos:

```powershell
$env:MONEY_FLOW_BACKUP_PASSPHRASE = "<atskiru kanalu gauta frazė>"
.\Deploy-MoneyFlow.ps1 -InitialDatabase ".\db-backups\monthly-money-flow-latest.mfbackup"
```

**3 žingsnis.** Nustatykite PAD importo API raktą ir atskirą backup šifravimo frazę (paslaugos perkrauti nereikia):

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowApiKey.ps1"
powershell -ExecutionPolicy Bypass -File "C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowBackupPassphrase.ps1"
```

Tą patį raktą Power Automate Desktop turi siųsti `X-Api-Key` antraštėje.

**4 žingsnis.** Patikrinkite, kad viskas veikia:

- `http://localhost:5000/health` atsako `ok`, o `http://localhost:5000/ready` — `ready`;
- naršyklėje atsidaro `http://<serverio-IP>:5000` projektų sąrašas;
- po pirmo PAD importo `GET /api/imports/monthly-flow/status` rodo importo būseną.

**5 žingsnis.** (Tinklo administratorius) Vidinis DNS vardas, reverse proxy ir prieigos ribojimai — žr. [docs/deployment.md](docs/deployment.md).

### Atnaujinimas

```powershell
git pull
powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
```

Skriptas sustabdo paslaugą, pakeičia programos failus, palieka gyvą DB ir vėl paleidžia paslaugą. Naršyklės naują UI versiją pasiima automatiškai.

---

## Konfigūracija

Saugus struktūros pavyzdys yra [appsettings.example.json](appsettings.example.json). Tikrų raktų į `appsettings*.json`, `.env`, testų duomenis ar dokumentaciją nedėkite.

| Nustatymas | Paskirtis |
|---|---|
| `MoneyFlow:DatabasePath` / `MONEY_FLOW_DB_PATH` | SQLite failo kelias |
| `MoneyFlow:ApiKeyFilePath` | PAD importo API rakto failas (gamybos būdas) |
| `MoneyFlow:ApiKey` | Raktas tiesiogiai konfigūracijoje (nerekomenduojama gamyboje) |
| `MONEY_FLOW_API_KEY` | Rakto aplinkos kintamasis — patogiausias lokaliai plėtrai |
| `Kestrel:Endpoints:Http:Url` / `ASPNETCORE_URLS` | Klausomas HTTP adresas |

API rakto šaltinių prioritetas: failas (`ApiKeyFilePath`) → `MoneyFlow:ApiKey` → `MONEY_FLOW_API_KEY`.

Gamyboje diegimo scenarijus naudoja `C:\ProgramData\PADS\MoneyFlow` duomenims ir `C:\Program Files\PADS\MoneyFlow` programai.

## Importai ir pagrindiniai endpointai

PAD turi siųsti `X-Api-Key` antraštę į abu importo endpointus; ta pati antraštė saugo ir `POST /api/maintenance/*`. Užklausos kūnas ribojamas iki 10 MB.

| Metodas ir kelias | Paskirtis |
|---|---|
| `POST /api/imports/monthly-flow` | Mėnesinio srauto JSON importas |
| `POST /api/imports/contracts` | Sutarčių ir projektų verčių importas |
| `GET /api/imports/monthly-flow/status` | Paskutinių importų būsena |
| `POST /api/maintenance/db-backup` | Vientisa DB kopija (`VACUUM INTO`) į `Backups` katalogą |
| `GET /api/projects` | Projektų sąrašas |
| `GET /api/projects/{code}/monthly-flow` | Projekto / objekto detalė |
| `GET /api/projects/{code}/ignored-rows` | Rankiniu būdu ignoruotos eilutės |
| `POST` / `DELETE /api/projects/...` | Patikimos LAN UI rankiniai susiejimai ir eilučių koregavimas |
| `GET /api/diagnostics/subcontractors` | Subrangovų normalizavimo diagnostika (tik Development) |
| `GET /health` | Proceso gyvumo patikra; DB netikrina |
| `GET /ready` | SQLite pasiekiamumo ir `quick_check` patikra diegimui / restore |

Importo validacija, deduplikavimas ir PAD klaidų elgsena aprašyti [docs/import-flow.md](docs/import-flow.md).

## Duomenų bazė ir atsarginės kopijos

- Gyva DB yra `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db` — į repo ji nepatenka ir išgyvena atnaujinimus.
- **`Backup-MoneyFlowDb.bat`** — per API ir SQLite `VACUUM INTO` sukuria bei patikrina vietinę kopiją, tada prieš Git commit ją AES-256-GCM formatu užšifruoja į `db-backups/monthly-money-flow-latest.mfbackup`. Git/push ar šifravimo klaida grąžina nesėkmės kodą. Jei HTTP neatsako, tiesioginė kopija leidžiama tik patvirtinus, kad servisas sustabdytas, nėra sidecar failų ir DB neužrakinta.
- **`Restore-MoneyFlowDb.bat`** — iššifruoja pasirinktą kopiją, prieš keitimą atlieka `PRAGMA integrity_check`, po keitimo tikrina `/ready`, o nesėkmės atveju automatiškai grąžina `pre-restore-*` DB.
- Darbiniai `*.db`, `*-wal`, `*-shm` ir `*-journal` failai ignoruojami. Git leidžiamas tik šifruotas `*.mfbackup`.
- Kitų tikrų įmonės ar SharePoint eksporto duomenų į repo nedėti.

Atsarginių kopijų ir atkūrimo procedūra žingsnis po žingsnio: [docs/deployment.md](docs/deployment.md).

## Patikros

```powershell
dotnet format --verify-no-changes
dotnet build -c Release
dotnet list PADS.MoneyFlow.Api.csproj package --vulnerable --include-transitive
Get-ChildItem wwwroot,docs -Recurse -Filter *.js | ForEach-Object { node --check $_.FullName }
.\Run-Tests.ps1
```

Tos pačios patikros vykdomos CI (`.github/workflows/ci.yml`). `Run-Tests.ps1` kiekvieną PowerShell integracinį scenarijų paleidžia atskirame procese, todėl aplinkos kintamieji ir numatytos HTTP antraštės tarp testų nepersiduoda.

## Trikčių šalinimas

Trumpi sprendimai pateikti [docs/troubleshooting.md](docs/troubleshooting.md). Diegimo aplinkoje pirmiausia tikrinkite Windows paslaugą (`Get-Service MoneyFlow`), `http://localhost:5000/health`, 5000 prievadą ir `ProgramData` aplanko teises.

## Saugumas ir projekto būsena

Tai vidinė, autentifikacijos neturinti LAN programa. Jos negalima jungti tiesiai prie interneto. Skaitymo endpointai ir dalis UI rašymo veiksmų pasitiki tinklo riba; importus papildomai saugo API raktas. Taikykite ugniasienės / VLAN ribojimus, reverse proxy, mažiausias failų teises ir atsargines kopijas. Žr. [SECURITY.md](SECURITY.md).

Projektas yra vidinis ir aktyviai prižiūrimas pagal skyriaus poreikius. Už priežiūrą atsako vidinis projekto maintaineris; incidentus ir prieigos klausimus perduokite įmonės patvirtintu kanalu.

**Internal project — not licensed for public reuse.**
