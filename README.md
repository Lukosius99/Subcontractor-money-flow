<div align="center">

# PADS MoneyFlow

**Subrangovų pinigų srautų stebėjimo sistema vidiniam įmonės tinklui**

`Windows Server` · `.NET 10` · `SQLite`

[**Atsisiųsti naujausią ZIP**](https://github.com/Lukosius99/Subcontractor-money-flow/archive/refs/heads/main.zip)

</div>

> [!IMPORTANT]
> Programa skirta tik vidiniam įmonės tinklui. Neatidarykite jos į internetą.

## Diegimas

### Reikalavimas

Serveryje turi būti įdiegtas **[.NET 10 SDK x64](https://dotnet.microsoft.com/download/dotnet/10.0)**.

`Git` ir kitos programavimo priemonės diegimui nereikalingos.

### 1. Atsisiųskite programą

1. GitHub puslapyje spauskite **Code → Download ZIP** arba naudokite viršuje esančią nuorodą.
2. Išarchyvuokite atsisiųstą failą, pavyzdžiui, į:

```text
C:\PADS\Subcontractor-money-flow
```

3. Patikrinkite, kad išarchyvuotame kataloge yra failas **`Deploy-MoneyFlow.bat`**.

### 2. Paleiskite diegimą

Pasirinkite diegimo būdą:

| Programos adresas | Paleidžiamas failas |
|---|---|
| `http://<serverio-IP>:5000` | **`Deploy-MoneyFlow.bat`** |
| Adresas per IIS, Caddy arba Nginx | **`Deploy-MoneyFlow-ReverseProxy.bat`** |

Tada:

1. Dukart spustelėkite pasirinktą `.bat` failą.
2. Windows administratoriaus užklausoje spauskite **Yes**.
3. Jei rodoma Windows apsauga, pasirinkite **More info → Run anyway**.
4. Jei prašoma, įveskite dabartinės duomenų bazės backup šifravimo frazę. Vedami simboliai ekrane nebus rodomi.
5. Diegimas baigtas, kai parodomas pranešimas **DIEGIMAS BAIGTAS SĖKMINGAI**.

Diegimo scenarijus automatiškai:

- paruošia programos `Release` versiją;
- atkuria pridėtą duomenų bazės kopiją, jei diegiama pirmą kartą;
- įdiegia ir paleidžia automatiškai startuojantį Windows servisą;
- išsaugo programą kataloge `C:\Program Files\PADS\MoneyFlow`;
- išsaugo duomenis kataloge `C:\ProgramData\PADS\MoneyFlow`.

### 3. Nustatykite prieigos reikšmes

Atidarykite katalogą:

```text
C:\Program Files\PADS\MoneyFlow
```

Paeiliui paleiskite:

| Failas | Paskirtis | Minimalus ilgis |
|---|---|---:|
| `Set-MoneyFlowApiKey.bat` | PAD / Power Automate užklausų API raktas | 16 simbolių |
| `Set-MoneyFlowEditPassphrase.bat` | Rankinio redagavimo slaptafrazė | 16 simbolių |
| `Set-MoneyFlowBackupPassphrase.bat` | Duomenų bazės backup šifravimo frazė | 20 simbolių |

Naudokite tris skirtingas reikšmes. API raktą įrašykite į PAD arba Power Automate užklausos `X-Api-Key` antraštę.

### 4. Patikrinkite veikimą

Serveryje atidarykite:

```text
http://localhost:5000/health
http://localhost:5000/ready
```

Rezultatai turi būti:

```text
ok
ready
```

Tada kitame vidinio tinklo kompiuteryje atidarykite diegimo metu pasirinktą programos adresą.

## Programos atnaujinimas

1. Atsisiųskite naujausią ZIP versiją.
2. Išarchyvuokite ją į serverį.
3. Dar kartą paleiskite tą patį diegimo `.bat` failą.

Esama duomenų bazė neperrašoma, nes ji saugoma atskirai kataloge `C:\ProgramData\PADS\MoneyFlow`.

> [!NOTE]
> ZIP kataloge nėra `.git` duomenų, todėl `Backup-MoneyFlowDb.bat` negali automatiškai išsiųsti kopijos į GitHub. Vietinei kopijai sukurti paleiskite `Backup-MoneyFlowDb.bat -SkipGitHub` arba naudokite administratoriaus patvirtintą atsarginių kopijų sprendimą.
