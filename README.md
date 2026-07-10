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

Sename serveryje dukart spustelėkite **`Backup-MoneyFlowDb.bat`**.

Sėkmingai pasibaigusi komanda užšifruotą kopiją įkelia į privatų GitHub repo. Šifravimo frazę perduokite atskiru saugiu kanalu.

### 2. Klonuokite projektą

Naujame kompiuteryje įdiekite [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) ir paleiskite:

```powershell
git clone https://github.com/Lukosius99/Subcontractor-money-flow.git
cd Subcontractor-money-flow
```

### 3. Įdiekite programą

Repo kataloge dukart spustelėkite **`Deploy-MoneyFlow.bat`**. Patvirtinkite Windows administratoriaus užklausą ir, jei prašoma, įveskite backup šifravimo frazę.

Paleidiklis pats parenka naujausią GitHub DB kopiją. Diegimo scenarijus publikuoja programą, sukuria Windows servisą, paruošia ugniasienę ir patikrina `/health` bei `/ready`.

### 4. Nustatykite raktus

Atidarykite `C:\Program Files\PADS\MoneyFlow` ir paleiskite:

1. **`Set-MoneyFlowApiKey.bat`**
2. **`Set-MoneyFlowBackupPassphrase.bat`**

Tą patį API raktą Power Automate Desktop turi siųsti `X-Api-Key` antraštėje.

### 5. Patikrinkite

- `http://localhost:5000/health` turi grąžinti `ok`.
- `http://localhost:5000/ready` turi grąžinti `ready`.
- `http://<serverio-IP>:5000` turi atsidaryti kitame LAN kompiuteryje.
- Skaitymas turi veikti be rakto, o redagavimas turi jo paprašyti.

## Atnaujinimas

Atnaujinkite repo (`git pull` arba GitHub Desktop), tada vėl dukart spustelėkite **`Deploy-MoneyFlow.bat`**.

Gyva DB saugoma `C:\ProgramData\PADS\MoneyFlow` ir atnaujinant neperrašoma. Nesėkmingo diegimo atveju scenarijus grąžina ankstesnę programos ir DB būseną.

## Kurį failą paleisti

| Veiksmas | Paleidžiamas failas | Administratoriaus teisės |
|---|---|---|
| Įdiegti arba atnaujinti | `Deploy-MoneyFlow.bat` | Taip, Windows paprašys automatiškai |
| Sukurti ir įkelti DB kopiją | `Backup-MoneyFlowDb.bat` | Ne |
| Atkurti DB | `Restore-MoneyFlowDb.bat` | Ne |
| Pakeisti API raktą | `Set-MoneyFlowApiKey.bat` | Ne |
| Pakeisti backup frazę | `Set-MoneyFlowBackupPassphrase.bat` | Ne |

Atkuriant DB servisas nestabdomas. Jis pats patikrina kopiją, sukuria rollback failą ir pakeičia SQLite turinį; kelioms sekundėms naujos DB užklausos gali gauti `503`.

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
