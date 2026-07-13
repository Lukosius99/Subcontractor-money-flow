<h1 align="center">PADS · diegimo instrukcija</h1>

<p align="center">MoneyFlow diegimas naujame Windows serveryje su dabartine GitHub duomenų baze.</p>

> [!IMPORTANT]
> Programa skirta tik vidiniam įmonės tinklui. Neatidarykite jos į internetą.

## 1. Pasiruoškite reikalingas reikšmes

Prieš pradėdami turėkite:

- prieigą prie privataus GitHub repo;
- dabartinės GitHub DB kopijos šifravimo frazę;
- naują bent 16 simbolių API raktą;
- naują bent 16 simbolių redagavimo slaptafrazę;
- naują bent 20 simbolių backup šifravimo frazę;
- serveriui skirtą vidinį IP adresą ir, jei naudojamas, DNS vardą, pvz. `pads.lt`.

API raktas, redagavimo slaptafrazė ir backup frazė turi būti skirtingi.

## 2. Paruoškite Windows serverį

1. Prisijunkite prie serverio paskyra, turinčia vietinio administratoriaus teises.
2. Įdiekite [Git for Windows](https://git-scm.com/download/win). Diegimo lange galite palikti numatytuosius pasirinkimus.
3. Įdiekite [.NET 10 SDK x64](https://dotnet.microsoft.com/download/dotnet/10.0).
4. Patikrinkite serverio tinklo profilį:
   - atidarykite **Settings → Network & internet → Ethernet**;
   - serveris turi būti įmonės `Domain` tinkle arba administratoriaus patvirtintame `Private` tinkle;
   - `Public` profilyje programos LAN ugniasienės taisyklė neveiks.
5. Atidarykite **Windows Terminal** arba **PowerShell** ir paeiliui įveskite:

```powershell
git --version
dotnet --version
```

Abi komandos turi parodyti įdiegtas versijas.

## 3. Atsisiųskite projektą

1. File Explorer sukurkite katalogą `C:\PADS` ir jį atidarykite.
2. Paspauskite File Explorer adreso juostą, įrašykite `powershell` ir paspauskite **Enter**.
3. Atsidariusiame lange įveskite:

```powershell
git clone https://github.com/Lukosius99/Subcontractor-money-flow.git
cd .\Subcontractor-money-flow
git config user.name "PADS Production"
git config user.email "pads@jusu-imone.lt"
```

4. Jei atsidaro GitHub prisijungimo langas, prisijunkite paskyra, turinčia šio repo skaitymo ir rašymo teises.
5. Vietoje `pads@jusu-imone.lt` galite įrašyti administratoriaus arba serveriui skirtą Git el. paštą.
6. Uždarykite PowerShell langą ir File Explorer atidarykite:

```text
C:\PADS\Subcontractor-money-flow
```

## 4. Paleiskite diegimą

Pasirinkite tik vieną diegimo variantą:

| Galutinis programos adresas | Dukart spustelėkite |
|---|---|
| `http://<serverio-IP>:5000` | `Deploy-MoneyFlow.bat` |
| `http://pads.lt` per IIS, Caddy arba Nginx | `Deploy-MoneyFlow-ReverseProxy.bat` |

Tada:

1. Windows administratoriaus užklausoje paspauskite **Yes**.
2. Jei rodoma Windows apsauga, paspauskite **More info → Run anyway**.
3. Kai prašoma **backup šifravimo frazės**, įveskite dabartinės GitHub DB kopijos frazę ir paspauskite **Enter**. Vedami simboliai ekrane nebus rodomi.
4. Palaukite, kol atsiras žalias pranešimas **DIEGIMAS BAIGTAS SĖKMINGAI**.
5. Paspauskite **Enter**, kad uždarytumėte langą.

Diegimo failas pats paima `db-backups\monthly-money-flow-latest.mfbackup`, atkuria DB, įdiegia Windows servisą ir patikrina programos veikimą.

## 5. Nustatykite prieigos reikšmes

1. File Explorer adreso juostoje įveskite:

```text
C:\Program Files\PADS\MoneyFlow
```

2. Dukart spustelėkite **`Set-MoneyFlowApiKey.bat`**.
3. Įveskite naują bent 16 simbolių API raktą ir paspauskite **Enter**. Simboliai nebus rodomi.
4. Uždarykite langą, kai matote **[GERAI]**.
5. Dukart spustelėkite **`Set-MoneyFlowEditPassphrase.bat`**.
6. Įveskite naują bent 16 simbolių redagavimo slaptafrazę ir paspauskite **Enter**.
7. Uždarykite langą, kai matote **[GERAI]**.
8. Dukart spustelėkite **`Set-MoneyFlowBackupPassphrase.bat`**.
9. Įveskite naują bent 20 simbolių backup šifravimo frazę ir paspauskite **Enter**.
10. Uždarykite langą, kai matote **[GERAI]**.

Naudojimas:

- API raktą įrašykite į PAD / Power Automate `X-Api-Key` antraštę;
- redagavimo slaptafrazę įveda Web UI naudotojas paspaudęs **Redaguoti**;
- backup frazę saugokite slaptažodžių saugykloje.

## 6. Sukurkite pirmą production backup

1. Grįžkite į `C:\PADS\Subcontractor-money-flow`.
2. Dukart spustelėkite **`Backup-MoneyFlowDb.bat`**.
3. Jei prašoma, įveskite 5 žingsnyje nustatytą API raktą arba backup frazę ir paspauskite **Enter**.
4. Palaukite pranešimo **[GERAI] Šifruota kopija išsiųsta į GitHub**.
5. Paspauskite **Enter**, kad uždarytumėte langą.

Šis veiksmas peršifruoja dabartinę DB nauja production backup fraze ir įkelia ją į GitHub.

## 7. Priskirkite vidinį adresą

### Jei pasirinkote `Deploy-MoneyFlow.bat`

1. Priskirkite serveriui pastovų vidinį IP adresą.
2. Kitame vidinio tinklo kompiuteryje atidarykite:

```text
http://<serverio-IP>:5000
```

Jei sukuriate DNS A įrašą `pads.lt → <serverio-IP>`, adresas bus `http://pads.lt:5000`.

### Jei pasirinkote `Deploy-MoneyFlow-ReverseProxy.bat`

1. Vidiniame DNS sukurkite A įrašą:

```text
pads.lt → <serverio-IP>
```

2. Tame pačiame serveryje sukonfigūruokite IIS, Caddy arba Nginx:

```text
Klausomas adresas: http://pads.lt:80
Perduoti į:       http://127.0.0.1:5000
```

3. Windows ugniasienėje leiskite TCP 80 tik iš įmonės vidinių tinklų.
4. Neatidarykite TCP 5000 į LAN ir nekurkite internetinio NAT ar port-forward.
5. Kitame vidinio tinklo kompiuteryje atidarykite:

```text
http://pads.lt
```

## 8. Patikrinkite diegimą

Serveryje naršyklėje atidarykite:

```text
http://localhost:5000/health
http://localhost:5000/ready
```

Pirmas adresas turi parodyti `ok`, antras – `ready`.

Tada kitame vidinio tinklo kompiuteryje patikrinkite:

1. Atsidaro pasirinktas programos adresas.
2. Matomi dabartinės DB projektai ir duomenys.
3. Duomenis galima skaityti neįvedus rakto.
4. Projekte paspaudus **Redaguoti** prašoma redagavimo slaptafrazės.
5. Įvedus teisingą redagavimo slaptafrazę galima atlikti rankinį pakeitimą.
6. PAD importo užklausa su `X-Api-Key` priimama.

Diegimas baigtas, kai visi šeši patikrinimai sėkmingi.
