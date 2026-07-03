# Subcontractor Money Flow

[![CI](https://github.com/Lukosius99/Subcontractor-money-flow/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Lukosius99/Subcontractor-money-flow/actions/workflows/ci.yml)

Vidinė įmonės LAN programa, skirta subrangovų mėnesiniams pinigų srautams pagal projektą ir objektą peržiūrėti.

![Duomenų kelio schema](docs/subrangos-duomenu-kelias-3d/preview.png)

Interaktyvi schemos versija saugoma [docs/subrangos-duomenu-kelias-3d](docs/subrangos-duomenu-kelias-3d/index.html). Jos neviešinkite per public GitHub Pages, jei vidinių sistemų pavadinimai nėra skirti viešumai.

## Architektūra

```text
SharePoint Excel → Power Automate Cloud Flow → JSON → Power Automate Desktop
    → vietinis ASP.NET Core API → SQLite → statinė naršyklės sąsaja
```

API ir UI yra vienas ASP.NET Core procesas. Programos duomenys laikomi vietiniame SQLite faile; repozitorijoje nėra darbinės ar pradinės duomenų bazės. Išsamiau: [architektūra](docs/architecture.md) ir [importo eiga](docs/import-flow.md).

## Technologijos

- .NET 10 / ASP.NET Core Minimal API
- Entity Framework Core 10 + SQLite
- HTML, CSS ir paprastas JavaScript be frontend build žingsnio
- ExcelJS eksportui į `.xlsx` (lokali minifikuota kopija)
- PowerShell integraciniams testams ir Windows diegimui

## Vietinis paleidimas

Reikia .NET 10 SDK. Node.js naudojamas tik JavaScript sintaksės patikrai.

```powershell
git clone https://github.com/Lukosius99/Subcontractor-money-flow.git
cd Subcontractor-money-flow
$env:MONEY_FLOW_API_KEY = "dev-only-key-at-least-16-chars"
dotnet restore
dotnet run
```

`MONEY_FLOW_API_KEY` reikalingas tik `POST /api/imports/*` užklausoms — be jo programa pasileidžia, o importai grąžina 503. Atidarykite terminale parodytą `localhost` adresą. Jei `MONEY_FLOW_DB_PATH` nenurodytas, DB sukuriama programos katalogo `data/` poaplankyje (su `dotnet run` tai `bin/Debug/net10.0/data/monthly-money-flow.db`; failas ignoruojamas git). Tuščia schema sukuriama automatiškai.

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

PAD turi siųsti `X-Api-Key` antraštę į abu importo endpointus. Užklausos kūnas ribojamas iki 10 MB.

| Metodas ir kelias | Paskirtis |
|---|---|
| `POST /api/imports/monthly-flow` | Mėnesinio srauto JSON importas |
| `POST /api/imports/contracts` | Sutarčių ir projektų verčių importas |
| `GET /api/imports/monthly-flow/status` | Paskutinių importų būsena |
| `GET /api/projects` | Projektų sąrašas |
| `GET /api/projects/{code}/monthly-flow` | Projekto / objekto detalė |
| `GET /api/projects/{code}/ignored-rows` | Rankiniu būdu ignoruotos eilutės |
| `POST` / `DELETE /api/projects/...` | Patikimos LAN UI rankiniai susiejimai ir eilučių koregavimas |
| `GET /api/diagnostics/subcontractors` | Subrangovų normalizavimo diagnostika (tik Development) |
| `GET /health` | Proceso gyvumo patikra; DB netikrina |

Importo validacija, deduplikavimas ir PAD klaidų elgsena aprašyti [docs/import-flow.md](docs/import-flow.md).

## Duomenų bazė ir atsarginės kopijos

- Darbiniai `*.db`, `*-wal`, `*-shm` ir `*-journal` failai yra ignoruojami.
- Repo neturi turėti tikrų įmonės, asmens ar SharePoint eksporto duomenų.
- Prieš kopijuojant DB sustabdykite paslaugą arba naudokite SQLite backup mechanizmą; vien pagrindinio failo kopija aktyvaus WAL režimo metu gali būti nepilna.
- Atsargines kopijas laikykite už repo ribų su prieiga tik administratoriui.

## Diegimas Windows LAN serveryje

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
```

Pirmo paleidimo metu programa sukurs tuščią DB. Jei naujam serveriui reikia atkurti patikrintą išorinę DB kopiją:

```powershell
.\Deploy-MoneyFlow.ps1 -InitialDatabase "D:\SecureBackups\monthly-money-flow.db"
```

Scenarijus esamos gyvos DB neperrašo. Windows paslaugos, ugniasienės, reverse proxy ir atkūrimo gairės: [docs/deployment.md](docs/deployment.md).

## Patikros

```powershell
dotnet format --verify-no-changes
dotnet build -c Release
dotnet list PADS.MoneyFlow.Api.csproj package --vulnerable --include-transitive
Get-ChildItem wwwroot,docs -Recurse -Filter *.js | ForEach-Object { node --check $_.FullName }
Get-ChildItem tests -Filter *.ps1 | Sort-Object Name | ForEach-Object { & $_.FullName }
```

Tos pačios patikros vykdomos CI (`.github/workflows/ci.yml`). `dotnet test` šiuo metu neranda atskiro testų projekto; elgsena tikrinama PowerShell integraciniais scenarijais su izoliuotomis testinėmis DB (kiekvienas testas pats pasileidžia serverį atskirame porte ir susikuria DB `test-data/` kataloge).

## Trikčių šalinimas

Trumpi sprendimai pateikti [docs/troubleshooting.md](docs/troubleshooting.md). Diegimo aplinkoje pirmiausia tikrinkite Windows paslaugą, `http://localhost:5000/health`, 5000 prievadą ir `ProgramData` aplanko teises.

## Saugumas ir projekto būsena

Tai vidinė, autentifikacijos neturinti LAN programa. Jos negalima jungti tiesiai prie interneto. Skaitymo endpointai ir dalis UI rašymo veiksmų pasitiki tinklo riba; importus papildomai saugo API raktas. Taikykite ugniasienės / VLAN ribojimus, reverse proxy, mažiausias failų teises ir atsargines kopijas. Žr. [SECURITY.md](SECURITY.md).

Projektas yra vidinis ir aktyviai prižiūrimas pagal skyriaus poreikius. Už priežiūrą atsako vidinis projekto maintaineris; incidentus ir prieigos klausimus perduokite įmonės patvirtintu kanalu.

**Internal project — not licensed for public reuse.**
