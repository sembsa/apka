# Co dalej — plan dla Przemka i Sebastiana

Data: 2026-09-06. Pisane po zamknieciu kanalu promptu (stdin), sesji panelu
i historii wersji w panelu. Stan: **187 silnik + 222 web zielono**.

Punktem odniesienia jest lista dziesieciu dlugow z
`docs/superpowers/plans/2026-09-05-api-i-kolejka.md`. **Lista jest nieaktualna** —
sprawdzone dzis w kodzie, nie z pamieci:

| Dlug | Stan |
|---|---|
| 1. Uwagi zyja tylko w obwodzie Blazora | **ZAMKNIETY** — `PUT .../comments` + `Editor.Zapisz()` |
| 2. Wycofanie wszystkich uwag nie dociera na dysk | **ZAMKNIETY** — tym samym zapisem |
| 9. Osierocony `work.staging-<guid>` | **ZAMKNIETY** — `SafeReplace` sprzata staging w obu sciezkach ratunkowych |
| 6. Tryb „mam strone" niczego nie pobiera | **W TOKU — Sebastian** |
| 3, 4, 5, 7, 8, 10 | otwarte |

Ktos powinien te tabelke przeniesc do tamtego pliku, zeby nie bylo dwoch prawd.

---

## Rzecz, ktora zmienia kolejnosc wszystkiego

Zapadla decyzja, ze **aplikacja bedzie wystawiona publicznie**, a panel ma byc
dostepny dla nas dwoch z dowolnego urzadzenia
(`docs/plan/2026-09-06-panel-publiczny-logowanie-plan.md`).

To nie jest osobny temat obok dlugow. To jest **zmiana wagi** kilku z nich:

- **Dlug 4 (kolejka w pamieci)** przestaje byc niedogodnoscia. Dzis restart gubi
  zadanie w locie na naszej maszynie. Po wystawieniu KAZDY DEPLOY gubi zadanie
  klienta — czyli **place zaplacone modelowi i klient patrzacy na wiszacy ekran**.
- **Dlug 7 (`SnapshotDir` bezwzgledny)** blokuje PRZENIESIENIE istniejacych
  projektow, ale nie blokuje swiezego startu. Roznica jest praktyczna: jesli
  wystawiamy sie z pusta baza projektow, mozna to zostawic. Jesli chcemy zabrac
  dotychczasowe projekty — trzeba zrobic PRZED przeprowadzka.
- **Dlug 8 (`/preview` bez tokenu)** ze „swiadomego, chronionego GUID-em" robi sie
  „strona klienta w publicznym internecie, a adres wycieka przez historie
  przegladarki i naglowek Referer".
- **Dlug 6 (pobieranie strony)** — patrz odpowiedz w kanale: pobrana tresc trafia
  do promptu (material niezaufany), a samo pobieranie z serwera to SSRF.

---

## Podzial: kto co bierze i dlaczego

Podzial idzie **wzdluz kontekstu, nie wzdluz trudnosci**. Kazdy bierze to, w czym
juz siedzi, zeby nie czytac cudzego kodu od zera i nie wchodzic sobie w pliki.

### SEBASTIAN — sciezka klienta i silnik

1. **Dlug 6: pobieranie strony w trybie „mam strone".** Zaczete.
   Warunki brzegowe uzgodnione w kanale: tresc jako CYTAT w ramce, z HTML-a tekst
   i struktura (bez `<script>`, komentarzy, atrybutow), test ze strona probujaca
   przejac prompt, oraz limity pobierania (schematy, adresy prywatne po DNS
   i po kazdym przekierowaniu, rozmiar, timeout, liczba przekierowan).

2. **Dlug 3: raport uwag po powrocie pokazuje porzucona galaz.**
   Z pozostalych to **jedyny dlug, przy ktorym klient czyta nieprawde** — „zrobilismy
   X" o zmianie, ktorej na ogladanej stronie nie ma. Wymaga zmiany modelu: uwaga
   musi wiedziec, ktora runda ja rozliczyla. Sebastian pisal `CommentStore`
   i `GenerationService`, wiec ma tam caly kontekst.

3. **Dlug 10: `Kolizja` lapie kazdy `InvalidOperationException`.** Male, ale kazdy
   blad programistyczny tej klasy pokazuje sie klientowi jako „Twoja strona jest juz
   gotowa". Domyka sie razem z 3, bo obie dotycza tego, co klient CZYTA.

### PRZEMEK — wystawienie i panel

1. **Dlug 4: trwalosc kolejki.** Najwiekszy pojedynczy powod, dla ktorego nie mozna
   dzis wystawic sie publicznie bez wstydu. Minimum: zadanie przezywa restart albo
   po restarcie uczciwie mowi „to zadanie przepadlo, oto co z budzetem" — zamiast
   wisiec. Do rozstrzygniecia, ktore z dwojga; to jest decyzja, nie szczegol.

2. **Logowanie do panelu (haslo + TOTP).** Plan gotowy i zatwierdzony, nic nie jest
   w kodzie. Kolejnosc wewnetrzna: Base32 i TOTP na wektorach RFC → hasla (PBKDF2)
   → limit prob → wymiana `Admin:Token` na liste kont → polecenie zakladania konta
   w `Generator.Cli`.

3. **Decyzja o dlugu 7 i 8 PRZED przeprowadzka.** Obie sa decyzjami produktowymi,
   nie robota: czy zabieramy istniejace projekty (7) i czy `/preview` zostaje
   publiczne (8). Nie zaczynam ich, dopoki nie zapadna.

### WSPOLNIE — do rozstrzygniecia zanim ktokolwiek zacznie wdrazac

Trzy pytania, na ktore nie ma dzis odpowiedzi w zadnym dokumencie, a wszystkie
wplywaja na kod:

1. **Gdzie to stoi?** Maszyna wirtualna, kontener, usluga zarzadzana. Od tego zalezy,
   czy `projekty/` to zwykly katalog, czy wolumen — i co znaczy „restart".
2. **Kopie zapasowe `projekty/`.** Po wystawieniu publiczny serwer jest JEDYNA kopia
   danych klientow. Dzis nie ma zadnej.
3. **Ile rownoleglych zadan?** Dlug 4 mowi „jeden konsument na wszystkie projekty".
   Przy dwoch klientach naraz drugi czeka kilka minut, nie wiedzac o tym.

---

## Czego NIE robimy teraz

- **Flaky w testach.** Poprawka jest (`47ea276`, `1464c4f`), pomiar x25 nalezy do
  pierwszej maszyny Przemka i poczeka, az sie na niej znajdzie. Nie blokuje niczego.
- **Konta klientow.** Decyzja 3 stoi: bez kont, token w linku. Dlug 8 domyka sie
  z kontami, ale konta to osobny produkt, nie krok.
- **Panel: limit prob klucza, akcje w panelu, rozbicie kosztow na wiecej wymiarow.**
  Limit prob wchodzi razem z logowaniem, reszta czeka.

## Kamien milowy, po ktorym warto sie zatrzymac

**„Pierwszy prawdziwy klient na publicznym adresie."** Zeby to bylo uczciwe
zdanie, musza byc spelnione naraz: dlug 4 (zadanie przezywa deploy albo mowi
prawde), logowanie do panelu, rozstrzygniete 7 i 8, kopie zapasowe.
Dlug 6 nie jest warunkiem — bez niego dziala polowa wejscia, ale ta polowa dziala.
