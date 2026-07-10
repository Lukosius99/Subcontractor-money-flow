<h1 align="center">PADS · Subrangos pinigų srautai</h1>

<p align="center">
  Vidinė Windows LAN programa projektų, objektų ir subrangovų pinigų srautams peržiūrėti.
</p>

<p align="center">
  <a href="https://github.com/Lukosius99/Subcontractor-money-flow/actions/workflows/ci.yml"><img alt="CI būsena" src="https://github.com/Lukosius99/Subcontractor-money-flow/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="Windows LAN" src="https://img.shields.io/badge/Windows-LAN-0078D4?logo=windows&logoColor=white">
  <img alt="SQLite" src="https://img.shields.io/badge/DB-SQLite-0F80CC?logo=sqlite&logoColor=white">
</p>

> [!IMPORTANT]
> Programa skirta tik vidiniam tinklui. Visi LAN naudotojai gali skaityti duomenis, tačiau kiekvienam duomenų pakeitimui reikia bendro API rakto. Programos nejunkite tiesiai prie interneto.

## Diegimas naujame kompiuteryje

### 1. Paruoškite naujausią DB kopiją

Sename serveryje paleiskite:

```powershell
.\Backup-MoneyFlowDb.bat
```

Sėkmingai pasibaigusi komanda užšifruotą kopiją įkelia į privatų GitHub repo. Šifravimo frazę perduokite atskiru saugiu kanalu.

### 2. Klonuokite projektą

Naujame kompiuteryje įdiekite [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) ir paleiskite:

```powershell
git clone https://github.com/Lukosius99/Subcontractor-money-flow.git
cd Subcontractor-money-flow
```

### 3. Įdiekite programą

Atidarykite **PowerShell kaip administratorius**:

```powershell
$env:MONEY_FLOW_BACKUP_PASSPHRASE = "<atskiru kanalu gauta frazė>"
.\Deploy-MoneyFlow.ps1 -InitialDatabase ".\db-backups\monthly-money-flow-latest.mfbackup"
```

Diegimo scenarijus pats publikuoja programą, sukuria Windows servisą, paruošia ugniasienę ir patikrina `/health` bei `/ready`.

### 4. Nustatykite raktus

```powershell
& "C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowApiKey.ps1"
& "C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowBackupPassphrase.ps1"
```

Tą patį API raktą Power Automate Desktop turi siųsti `X-Api-Key` antraštėje.

### 5. Patikrinkite

- `http://localhost:5000/health` turi grąžinti `ok`.
- `http://localhost:5000/ready` turi grąžinti `ready`.
- `http://<serverio-IP>:5000` turi atsidaryti kitame LAN kompiuteryje.
- Skaitymas turi veikti be rakto, o redagavimas turi jo paprašyti.

## Atnaujinimas

```powershell
git pull
powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
```

Gyva DB saugoma `C:\ProgramData\PADS\MoneyFlow` ir atnaujinant neperrašoma. Nesėkmingo diegimo atveju scenarijus grąžina ankstesnę programos ir DB būseną.

## Prieigos modelis

| Veiksmas | Prieiga |
|---|---|
| Atidaryti UI ir skaityti duomenis | Visi vidinio LAN naudotojai |
| Importuoti, redaguoti ar trinti | Reikia `X-Api-Key` |
| Pasiekti iš interneto | Draudžiama |

## Duomenų kelias

```text
SharePoint / verslo sistemos → Power Automate → PAD → lokali API → SQLite → Web UI
```

<details>
<summary><strong>Atverti interaktyvų duomenų žemėlapį</strong></summary>

Žemėlapį lokaliai atidarykite iš [docs/subrangos-duomenu-kelias-3d/index.html](docs/subrangos-duomenu-kelias-3d/index.html). Techninės detalės pagal nutylėjimą paslėptos.

</details>

## Kūrėjui

```powershell
$env:MONEY_FLOW_API_KEY = "dev-only-key-at-least-16-chars"
dotnet run
```

Visos patikros:

```powershell
.\Run-Tests.ps1
```

## Dokumentacija

| Dokumentas | Kada jo reikia |
|---|---|
| [Diegimas ir DB atkūrimas](docs/deployment.md) | Serverio diegimui, backup ir rollback |
| [Importo eiga](docs/import-flow.md) | PAD, JSON ir importo klaidoms |
| [Architektūra](docs/architecture.md) | Sistemos ir repo struktūrai suprasti |
| [Techninis žinynas](docs/reference.md) | Konfigūracijai, API ir testams |
| [Trikčių šalinimas](docs/troubleshooting.md) | Kai servisas ar importas neveikia |
| [Saugumas](SECURITY.md) | LAN, rakto ir incidentų taisyklėms |

---

<p align="center"><sub>Vidinis projektas · neskirtas viešam naudojimui</sub></p>
