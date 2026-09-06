# Panel przy publicznym wystawieniu — logowanie dwuskladnikowe (PLAN, NIE ZROBIONE)

Data: 2026-09-06. Zatwierdzony kierunek, **implementacja odlozona** — Przemek
poprosil o zapisanie do planow. Nic z tego dokumentu nie jest jeszcze w kodzie.

## Po co

Dzis cala ochrona panelu to JEDEN wspolny sekret w konfiguracji (`Admin:Token`).
Wystarcza, dopoki aplikacja stoi na localhoscie. Przy publicznym adresie ten model
sie rozpada:

- sekret jest wspolny, wiec nie wiadomo, KTO wszedl, i nie da sie odebrac dostepu
  jednej osobie
- trzeba go wpisywac na telefonie, wiec ladnie w notatkach i na komunikatorach
- nie ma zadnego limitu prob zgadywania

Cel: dostep maja **tylko Przemek i Sebastian**, z **dowolnego urzadzenia**.

## Wybrana droga

Wlasne konta: **haslo + kod TOTP**, bez zewnetrznego dostawcy tozsamosci.

Decyzja Przemka po porownaniu z logowaniem Microsoft 365 i brama typu Cloudflare
Access. **Swiadomy koszt: ryzyko kryptograficzne jest wtedy NASZE, a nie dostawcy.**
Projekt jest tak ulozony, zeby ta powierzchnia byla jak najmniejsza i w calosci
pokryta testami z oficjalnych wektorow RFC.

Druga decyzja: kod TOTP przy **kazdym** wejsciu (sesja 12 h, wiec w praktyce raz
dziennie na urzadzenie). Bez „zaufanego urzadzenia na 30 dni" — to byloby drugie,
dlugozyjace ciastko, czyli drugi sekret do skradzenia i osobna lista urzadzen
do uniewazniania.

## 1. Konfiguracja zamiast wspolnego tokenu

```json
"Admin": { "Konta": [
  { "Login": "przemek",   "HasloHash": "pbkdf2$210000$<sol>$<skrot>", "TotpSekret": "<base32>" },
  { "Login": "sebastian", "HasloHash": "...",                          "TotpSekret": "..." }
] }
```

- Pusta lista → **trasy nie ma** (404), dokladnie jak dzis przy pustym `Admin:Token`.
- Start aplikacji **wywala sie z czytelnym bledem**, jesli w konfiguracji dalej
  siedzi `Admin:Token`. Po cichu zignorowany stary klucz to najgorszy wynik
  migracji: panel wygladalby na dzialajacy, a ochrona bylaby martwa.
- Hasla nigdy jawnie. PBKDF2 z runtime'u (`Rfc2898DeriveBytes.Pbkdf2`, SHA-256,
  210 000 iteracji, sol 16 B). Prymityw od platformy; my definiujemy tylko format.

## 2. Logowanie

Jeden formularz: login, haslo, kod.

- **Zawsze ten sam komunikat** („Nie ten login, haslo albo kod"), a przy nieznanym
  loginie i tak wykonujemy pozorne sprawdzenie hasla — inaczej czas odpowiedzi
  zdradza, ktore loginy istnieja.
- TOTP: RFC 6238, HMAC-SHA1, 6 cyfr, krok 30 s, tolerancja ±1 krok na rozjazd zegarow.
- **Ochrona przed powtorzeniem**: zuzyty krok czasowy zapamietany, ten sam kod nie
  zadziala drugi raz. Bez tego kod podejrzany przez ramie dziala jeszcze pol minuty
  i drugi skladnik przestaje byc drugim skladnikiem.
- Porownania w stalym czasie, jak dzis w `KluczPanelu.Zgadza`.

Base32 (format aplikacji uwierzytelniajacych) i samo TOTP to okolo czterdziestu
linii. **Powod, dla ktorego nie bierzemy biblioteki:** obie rzeczy maja oficjalne
wektory testowe (RFC 4648 i RFC 6238 dodatek B), wiec implementacje weryfikuja
CUDZE liczby, nie nasze.

## 3. Zgadywanie hasla

Licznik nieudanych prob per login i per adres IP, w pamieci, na `TimeProvider`
(test nie moze spac). Piec nieudanych w 15 minut zamyka login na 15 minut.
Zablokowany login dostaje **ten sam komunikat** co zly kod — „to konto istnieje
i jest zablokowane" tez jest informacja.

## 4. Sesja — rozszerzenie tego, co juz jest

`SesjePanelu` zostaje; sesja zapamietuje dodatkowo **kto**. Naglowek pokazuje
„zalogowany jako X", a wpisy w logu (wejscie, nieudana proba, blokada, wylogowanie
— z loginem, IP i czasem) przestaja byc anonimowe. To cala roznica miedzy „ktos
ogladal dane klientow" a „wiadomo kto".

Ciastko bez zmian, `Secure` wlaczy sie samo pod HTTPS (`zadanie.IsHttps`).
**Restart aplikacji wylogowuje wszystkich** — przy publicznym wdrozeniu znaczy to,
ze kazdy deploy wylogowuje. Swiadomie: uniewaznianie ma byc natychmiastowe i nie
chcemy drugiego magazynu stanu dla dwoch osob.

## 5. Zakladanie konta

Polecenie w `Generator.Cli`: pyta o haslo, losuje 160-bitowy sekret TOTP, wypisuje
gotowy fragment konfiguracji i identyfikator `otpauth://` do wklejenia w aplikacje
uwierzytelniajaca.

**W aplikacji webowej nie ma zadnego ekranu rejestracji** — konto zaklada sie przy
konsoli, wiec ta powierzchnia w ogole nie istnieje publicznie.

## 6. Czego ten plan NIE zalatwia

Publiczne wystawienie otwiera rzeczy poza panelem. Wypisane, zeby nie zostalo
wrazenie, ze po tej zmianie jest bezpiecznie:

- **`/preview/{id}/{n}/` i `/pobierz/{id}/{n}` sa bez tokenu, z zalozenia**
  (kontrakt 1). Publicznie znaczy to: kto zna identyfikator projektu, ten widzi
  strone klienta i pobierze paczke. GUID jest nieodgadywalny, ale wycieka przez
  historie przegladarki i naglowek `Referer`. Osobna decyzja produktowa.
- Sekrety produkcyjne musza isc zmiennymi srodowiskowymi hostingu, nie plikiem w repo.
- Kopie zapasowe `projekty/` — publiczny serwer to jedyna kopia danych klientow.
- `UseHsts()` i `UseHttpsRedirection()` juz sa; trasy `/dev/*` juz siedza
  za `IsDevelopment()`. Tego nie trzeba dokladac.

## Stan wyjsciowy w chwili pisania planu

Panel ma sesje (commit `0191602`): wejscie kluczem, POST/Redirect/GET, ciastko
z identyfikatorem sesji (nigdy z tokenem), wylogowanie kasujace sesje po stronie
serwera, 12 h od ostatniego uzycia. Ten plan wymienia tylko SPOSOB WPUSZCZANIA —
cala reszta zostaje.
