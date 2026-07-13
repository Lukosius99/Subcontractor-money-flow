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
> Programa skirta tik vidiniam tinklui. Visi LAN naudotojai gali skaityti duomenis. Rankiniam redagavimui naudojama redagavimo slaptafrazė, o PAD importams ir DB priežiūrai – atskiras API raktas. Programos nejunkite tiesiai prie interneto.

## Prieš perduodant administratoriui

Sename serveryje dukart spustelėkite **`Backup-MoneyFlowDb.bat`**. Sėkmingai pasibaigusi komanda užšifruotą naujausią DB kopiją įkelia į privatų GitHub repo.

Administratoriui atskiru saugiu kanalu perduokite:

- prieigą prie privataus repo;
- dabartinės GitHub DB kopijos šifravimo frazę.

API raktas, redagavimo slaptafrazė ir nauja backup frazė production serveryje turi būti nustatyti iš naujo ir tarpusavyje nesutapti.

## Diegimas naujame kompiuteryje

### 1. Paruoškite serverį

Įdiekite [Git for Windows](https://git-scm.com/download/win) ir [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Patikrinkite, kad serverio Git paskyra gali klonuoti ir pushinti į privatų repo.

### 2. Klonuokite projektą

```powershell
git clone https://github.com/Lukosius99/Subcontractor-money-flow.git
cd Subcontractor-money-flow
```

### 3. Įdiekite programą

Repo kataloge pasirinkite vieną failą:

| Norimas adresas | Paleidžiamas failas |
|---|---|
| `http://<IP>:5000` arba `http://<vidinis-vardas>:5000` | **`Deploy-MoneyFlow.bat`** |
| `http://<vidinis-vardas>` per tame pačiame serveryje veikiantį reverse proxy | **`Deploy-MoneyFlow-ReverseProxy.bat`** |

Patvirtinkite Windows administratoriaus užklausą ir, jei prašoma, įveskite dabartinės GitHub DB kopijos šifravimo frazę.

Paleidiklis pats parenka naujausią GitHub DB kopiją. Diegimo scenarijus publikuoja programą, sukuria Windows servisą, paruošia ugniasienę ir patikrina `/health` bei `/ready`.

### 4. Nustatykite raktus

Atidarykite `C:\Program Files\PADS\MoneyFlow` ir paleiskite:

1. **`Set-MoneyFlowApiKey.bat`**
2. **`Set-MoneyFlowEditPassphrase.bat`**
3. **`Set-MoneyFlowBackupPassphrase.bat`**

Power Automate Desktop turi siųsti API raktą `X-Api-Key` antraštėje. Redagavimo slaptafrazė skirta tik rankiniams pakeitimams Web UI ir neturi būti perduodama PAD.

Nustačius naują backup frazę, vieną kartą paleiskite repo kataloge **`Backup-MoneyFlowDb.bat`**, kad GitHub kopija būtų peršifruota nauja production fraze.

### 5. Patikrinkite

- `http://localhost:5000/health` turi grąžinti `ok`.
- `http://localhost:5000/ready` turi grąžinti `ready`.
- `DirectLan` režime `http://<serverio-IP>:5000` turi atsidaryti kitame LAN kompiuteryje.
- `ReverseProxy` režime turi atsidaryti vidinis vardas be `:5000`, o tiesioginis LAN prisijungimas prie 5000 turi būti nepasiekiamas.
- Skaitymas turi veikti be rakto, o įjungus redagavimą turi būti paprašyta redagavimo slaptafrazės.

Serverio tinklo profilis turi būti `Domain` arba administratoriaus patvirtintas `Private`. `Public` profilyje MoneyFlow ugniasienės taisyklė sąmoningai neveikia.

### Vidinis DNS ir HTTP

- Vidiniame DNS sukurkite A įrašą, pvz. `pads.lt → <serverio-IP>`. Naudokite tik įmonės valdomą vidinę DNS zoną.
- DNS pats nepanaikina `:5000`. Adresui `http://pads.lt` reikia IIS, Caddy arba Nginx, kuris TCP 80 perduoda į `http://127.0.0.1:5000`.
- Naudojant reverse proxy, paleiskite `Deploy-MoneyFlow-ReverseProxy.bat`; aplikacijos 5000 prievadas tada nebus atvertas LAN.
- HTTP leidžiamas tik patikimame vidiniame VLAN. Nekurkite internetinio NAT ar port-forward į 80/5000.

## Atnaujinimas

Atnaujinkite repo (`git pull` arba GitHub Desktop), tada vėl dukart spustelėkite **`Deploy-MoneyFlow.bat`**. Pirmo diegimo metu pasirinktas tinklo režimas išsaugomas ir per atnaujinimą nesikeičia.

Gyva DB saugoma `C:\ProgramData\PADS\MoneyFlow` ir atnaujinant neperrašoma. Nesėkmingo diegimo atveju scenarijus grąžina ankstesnę programos ir DB būseną.

## Kurį failą paleisti

| Veiksmas | Paleidžiamas failas | Administratoriaus teisės |
|---|---|---|
| Įdiegti arba atnaujinti | `Deploy-MoneyFlow.bat` | Taip, Windows paprašys automatiškai |
| Įdiegti už reverse proxy | `Deploy-MoneyFlow-ReverseProxy.bat` | Taip, Windows paprašys automatiškai |
| Sukurti ir įkelti DB kopiją | `Backup-MoneyFlowDb.bat` | Ne |
| Atkurti DB | `Restore-MoneyFlowDb.bat` | Ne |
| Pakeisti API raktą | `Set-MoneyFlowApiKey.bat` | Ne |
| Pakeisti redagavimo slaptafrazę | `Set-MoneyFlowEditPassphrase.bat` | Ne |
| Pakeisti backup frazę | `Set-MoneyFlowBackupPassphrase.bat` | Ne |

`Deploy`, `Backup` ir `Restore` failus paleiskite klonuotame repo kataloge. Tris `Set-...` failus paleiskite iš `C:\Program Files\PADS\MoneyFlow`.

Kuriant backup servisas nestabdomas; operaciją saugo API raktas. Atkuriant DB servisas taip pat nestabdomas: jis patikrina kopiją, sukuria rollback failą ir pakeičia SQLite turinį; kelioms sekundėms naujos DB užklausos gali gauti `503`.

## Prieigos modelis

| Veiksmas | Prieiga |
|---|---|
| Atidaryti UI ir skaityti duomenis | Visi vidinio LAN naudotojai |
| Rankiniu būdu redaguoti ar trinti Web UI | Reikia redagavimo slaptafrazės |
| Importuoti per PAD arba vykdyti DB priežiūrą | Reikia `X-Api-Key` |
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
$env:MONEY_FLOW_EDIT_PASSPHRASE = "dev-only-edit-passphrase"
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
