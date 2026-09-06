# Sesja panelu administratora — projekt

Data: 2026-09-06. Zatwierdzone przez Przemka.

## Problem

`/panel` jest bezsesyjny. Lista projektow istnieje wylacznie jako odpowiedz na POST
z kluczem, wiec:

- **F5 to „wyslac formularz ponownie?"** — przegladarka pyta, bo ostatnim zadaniem byl POST.
- **Kazde wyjscie z panelu jest jednokierunkowe.** Klikniecie w *podglad* albo *edytor*
  opuszcza strone, a powrot (Wstecz) laduje na stronie POST-a — czyli znowu prompt
  o ponowne wyslanie albo pusty formularz. Klucz trzeba wklejac od nowa.

To jedyna rzecz w panelu, o ktora uzytkownik obija sie CODZIENNIE. Reszta pomyslow
(rozbicie kosztow po wersjach, limit prob klucza, akcje w panelu) jest odlozona
swiadomie — dopoki samo wejscie boli, cala reszta to kosmetyka.

## Rozwiazanie

### 1. `SesjePanelu` (`src/Generator.Web/Panel/SesjePanelu.cs`)

Jedyne miejsce, ktore wie, czym jest wazna sesja.

| Metoda | Znaczenie |
|---|---|
| `string Utworz()` | 256-bitowy identyfikator (`RandomNumberGenerator`, Base64Url), zapamietany z terminem waznosci. Przy okazji sprzata przeterminowane wpisy. |
| `bool Wazna(string? id)` | Tak/nie; przy „tak" PRZESUWA termin (waznosc liczona od ostatniego uzycia, nie od utworzenia). |
| `void Zakoncz(string? id)` | Kasuje sesje po stronie serwera. |

Stan: `ConcurrentDictionary` w pamieci procesu. Restart = wylogowanie, i to jest
wlasciwe zachowanie przy uruchomieniu lokalnym.

Czas przez `TimeProvider`. Test wygasniecia NIE MOZE spac — to dokladnie ten rodzaj
testu zaleznego od maszyny, na ktorym obie strony repo juz raz oberwaly.

Zycie sesji: **12 h bezczynnosci**. Decyzja „localhost, dlugie wejscie": panel nie
ma prawa wylogowac czlowieka w srodku pracy.

### 2. Ciasteczko

Nazwa `panel`. Wartosc to SAM IDENTYFIKATOR — `Admin:Token` nigdy nie opuszcza serwera,
wiec w przegladarce nie lezy nic, czego nie da sie uniewaznic.

- `HttpOnly` — skrypt strony nie ma po co go czytac.
- `SameSite=Strict` — cudza strona nie wywola panelu w tle.
- `Path=/panel` — nie jedzie przy zadaniach klienta o `/preview` ani `/editor`.
- Bez `Expires` — ciastko sesyjne, ginie z zamknieciem przegladarki. 12 h to drugi,
  serwerowy limit.
- `Secure = zadanie.IsHttps` — na `http://localhost` wylaczone (inaczej przegladarka
  wyrzuca ciastko po cichu i panel „nie dziala bez powodu"), pod HTTPS wlaczone samo.

### 3. Trasy

Wszystkie trzy zostaja pod tym samym `if (KluczPanelu.Ustawiony(...))` — bez klucza
nie ma zadnej, tak jak dzis.

- `GET /panel` — ciastko wazne → lista; nie → formularz.
- `POST /panel` — dobry klucz → sesja, ciastko, **303 See Other na `GET /panel`**.
  To jest wlasciwa naprawa F5: po przekierowaniu w pasku adresu stoi GET, wiec
  odswiezenie nie ma czego wyslac ponownie. Zly klucz zostaje jak dzis: 401
  z formularzem, bez przekierowania.
- `POST /panel/wyloguj` — kasuje sesje PO STRONIE SERWERA (samo skasowanie ciastka
  niczego nie zamyka komus, kto je juz skopiowal), kasuje ciastko, 303 na `/panel`.

### 4. Widok

- Przycisk „Wyloguj" w naglowku, widoczny tylko na liscie.
- `podglad` i `edytor` z `target="_blank" rel="noopener"` — panel zostaje tam, gdzie byl.

## Testy

**`SesjePaneluTests`** — sama logika, z podstawionym `TimeProvider`:
swieza sesja wazna; po 12 h bezczynnosci nie; uzycie w miedzyczasie przedluza;
`Zakoncz` uniewaznia; zmyslony i pusty identyfikator nigdy nie sa wazne;
dwie sesje maja rozne identyfikatory.

**`PanelEndpointTests`** (rozszerzenie) — pelna sciezka HTTP:
POST z dobrym kluczem → 303 + `Set-Cookie`; GET z ciastkiem → lista; GET ze zmyslonym
ciastkiem → formularz; po wylogowaniu kolejny GET → formularz; ciastko ma `HttpOnly`
i `SameSite=Strict`; bez `Admin:Token` wszystkie trzy trasy → 404.

## Poza zakresem

Limit prob klucza, log wejsc, rozbicie kosztow po wersjach, akcje w panelu
(ZIP, odmrozenie, podbicie limitow). Kazde z nich to osobny temat.
