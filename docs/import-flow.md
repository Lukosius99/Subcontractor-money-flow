# Importo eiga

## Mėnesinis srautas

1. Office Script perskaito mėnesinio Excel lapus ir grąžina JSON (palaikomos 1.3 ir 1.4 schemos).
2. Cloud Flow išsaugo naujausią JSON SharePoint vietoje.
3. PAD parsisiunčia laikiną failą ir siunčia `POST /api/imports/monthly-flow?sourceFileName=...`. Failo vardą taip pat galima perduoti `X-Source-File-Name` antrašte arba siųsti patį failą kaip `multipart/form-data` (`file` laukas).
4. API patikrina objektą, laikotarpį, eilučių laukus ir skaitines reikšmes.
5. Viso turinio hash apsaugo nuo identiško pakartotinio importo; loginės eilutės atnaujinamos pagal stabilų `RowKey`.
6. Sėkmingą HTTP 200 gavęs PAD gali ištrinti laikiną JSON. Klaidos atveju failą ir klaidos informaciją reikia palikti operatoriaus diagnostikai už repo ribų.

1.4 schemoje kiekvienai eilutei būtinas `sourceSheet`. Netinkamos eilutės praleidžiamos su įspėjimu, o netinkamas visas dokumentas grąžina 400.

## Sutarčių ir projektų verčių srautas

PAD siunčia `POST /api/imports/contracts` su bent vienu masyvu: `rows` arba `projectValueRows`. Sutarties eilutei būtini projekto kodas, objekto numeris, subrangovas ir sutartinė suma; projekto vertės eilutei — projekto kodas, objekto numeris ir vertė. Jei visos atsiųstos eilutės atmetamos validacijos, API grąžina 400 su `imported: false` ir `warnings` sąrašu — PAD tokį atsakymą turi traktuoti kaip klaidą.

Importas yra naujausio snapshot pobūdžio: sistemoje pažymima, kurie projektai / kontraktai buvo matyti naujausiame importe. Neaktyvius pažymi tik importas, kuriame yra sutarčių `rows` — payload'as vien su `projectValueRows` laikomas daliniu ir esamų projektų aktyvumo nekeičia. Prieš keičiant šią logiką būtina paleisti `contract-snapshot.ps1`.

## Autorizacija ir ribos

- Abu `POST /api/imports/*` endpointai reikalauja tikslios `X-Api-Key` reikšmės.
- Jei raktas nesukonfigūruotas, importas grąžina 503; jei blogas — 401.
- Kestrel atmeta didesnį nei 10 MB kūną.
- Importo kodas nelogina viso JSON ar API rakto.
- JSON eksportai gali turėti įmonės ir asmens duomenų: nelaikykite jų repo, `wwwroot`, dokumentacijoje ar CI artefaktuose.

## Operatorius

Po importo patikrinkite `GET /api/imports/monthly-flow/status` ir UI sumas. Jei srautas kartojasi su klaida, išsaugokite tik minimalų, nuasmenintą diagnostikos pavyzdį.
