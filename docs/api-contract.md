# Kontrakt API — generator stron (v0.1, w budowie)

Wspólny artefakt Sebastiana i Przemka, do domknięcia **przed pierwszą linią kodu**.
Plan: [`plan/2026-09-04-generator-stron-plan.md`](plan/2026-09-04-generator-stron-plan.md).

Stack (decyzja 1): **C#/Blazor Server**, silnik `claude -p`, wersje jako snapshoty katalogów.

| Część | Właściciel | Status |
|---|---|---|
| 1. Zasoby HTTP | Sebastian | szkic poniżej, do recenzji |
| 2. Kontrakt silnika | Sebastian | wypełniony |
| 3. Kontrakt komentarza | **Przemek** | wypełniony — dwa wymogi zwrotne do sekcji 2 i 4 planu |
| 4. Limit rund i UX przy `failed` | wspólnie | wypełniony — wymóg zwrotny do sekcji 1 (`Job.failure`) |

Model danych jest wspólny, ale nie ma tu własnej sekcji — obowiązuje ten z planu (sekcja 6).

---

## 1. Zasoby HTTP (szkic — Sebastian)

Wszystko, co uruchamia model, jest **zadaniem** (`Job`), nie żądaniem synchronicznym.
Endpoint zwraca `202` z `jobId`; postęp idzie po obwodzie SignalR (Blazor Server),
a `GET /api/jobs/{id}` zostaje dla klientów poza naszym UI.

| Metoda | Ścieżka | Zwrot |
|---|---|---|
| `POST` | `/api/projects` | `201` + `projectId`, `token` (dostęp bez konta — decyzja 3) |
| `GET` | `/api/projects/{id}` | stan projektu (w tym `frozen`), `roundsUsed`/`roundsLimit`, lista wersji, aktualna wersja |
| `POST` | `/api/projects/{id}/proposals` | `202` + `jobId` — generacja 3 propozycji |
| `POST` | `/api/projects/{id}/proposals/{proposalId}/choose` | `200` — wybór klienta |
| `POST` | `/api/projects/{id}/versions` | `202` + `jobId` — wersja 1 z wybranej propozycji |
| `POST` | `/api/projects/{id}/versions/{n}/comments/apply` | `202` + `jobId` — runda komentarzy; `409`, gdy projekt `frozen` (4.5) |
| `GET` | `/api/projects/{id}/versions` | lista wersji (numer, data, koszt) |
| `GET` | `/api/projects/{id}/versions/{n}/preview` | statyczne pliki wersji do iframe |
| `GET` | `/api/projects/{id}/versions/{n}/zip` | ZIP (decyzja 2) |
| `GET` | `/api/jobs/{id}` | status zadania + `failure {handling, cause, attempts}` (4.3) |

Status zadania: `queued` \| `running` \| `succeeded` \| `failed`. **`failed` obejmuje
przypadki, w których model „zakończył sukcesem"** — patrz sekcja 2. `cause` z `failure`
nie idzie do UI; frontend rozgałęzia się po `handling` (4.3).

`spentUsd`/`budgetUsd` (4.1) **nie wychodzą** przez żaden endpoint klienta — licznik rund
jest dla klienta, budżet tylko dla nas.

## 2. Kontrakt silnika (Sebastian — wypełniony)

```
GenerateRequest {
  workspaceDir       // katalog projektu, POZA drzewem tego repo (plan, sekcja 5)
  instruction        // brief albo zestaw komentarzy, w kolejności dodawania (3.2)
  previousAnchors[]  // lista data-cmt-id z poprzedniej wersji — wchodzi DO promptu (3.1)
  sessionId?         // brak = pierwszy przebieg (--session-id), obecny = --resume
}

GenerateResult {
  sessionId
  changedFiles[]
  summary            // tekst o wersji jako całości
  commentResults[] { // raport per komentarz, kluczowany naszym Comment.id (3.3)
    commentId
    status           // "applied" | "rejected"
    note             // zdanie PRZY komentarzu, nie w zbiorczym summary
  }
  orphanedAnchors[]  // id z previousAnchors nieobecne w tej wersji (3.4)
  totalCostUsd       // suma total_cost_usd, NIE tokeny jednego modelu
  permissionDenials[]
}
```

`previousAnchors[]` jest po stronie silnika odpowiedzią na obowiązek z 3.1: „zachowaj
`data-cmt-id`" bez listy jest instrukcją bez przedmiotu. Pusta lista = pierwsza wersja.

Statusu **nie czytamy z prozy modelu** (3.3) — prompt rundy żąda raportu per `commentId`,
a komentarz bez wpisu w `commentResults[]` zostaje `open` i wchodzi do następnej rundy.

Zadanie jest udane tylko wtedy, gdy **wszystkie** warunki są spełnione (plan, 5.1):

1. proces wypisał JSON na stdout (błąd startu = pusty stdout, komunikat na stderr, exit 1)
2. exit 0, `is_error: false`, `subtype: "success"`
3. `stop_reason: "end_turn"`, `terminal_reason: "completed"`
4. **`permissionDenials` jest puste**
5. wersja przeszła **twardą** bramkę z 5.2 — renderowalność. Brakujące `data-cmt-id`
   to bramka **miękka**: po wyczerpaniu prób wersja jest dostarczana z `orphanedAnchors[]`
   (3.4), a nie odrzucana

Punkt 4 jest nieoczywisty i zmierzony: bez `--permission-mode acceptEdits` `Write` jest
odrzucany, a JSON i exit code wyglądają na sukces. Frontend **nigdy** nie widzi wersji,
która nie przeszła tych pięciu warunków — dostaje `failed` z powodem.

## 3. Kontrakt komentarza (Przemek — wypełniony)

Warunek przyjęty i on rządzi tą sekcją: **projektowane od strony przeglądarki.**
Ma to konkretną konsekwencję, nie tylko kolejność pisania — droga kliknięcia to
iframe → `postMessage` → shim JS → `[JSInvokable]`, czyli **payload przechodzi przez
granicę serializacji**. Kształt komentarza jest więc zdefiniowany przez to, co potrafi
wyprodukować skrypt w iframe, a typy C# idą za nim, nie odwrotnie.

### 3.1 `data-cmt-id` nadaje generator — i nie mogą to być numery porządkowe

**Generator, w prompcie. Frontend nigdy nie nadaje id.** Tylko generator wie, że dany
blok „to nadal ta sama oferta" po przepisaniu strony. Gdyby frontend dorabiał id po fakcie
(ze ścieżki DOM albo kolejności), rozsypywałyby się przy pierwszej zmianie układu — czyli
dokładnie wtedy, kiedy anchor jest potrzebny.

**Poprawka do sekcji 4 planu:** `oferta-1` z przykładu jest błędem, nie wzorem. Id oparte
na pozycji orphanuje komentarze przy **wstawieniu** elementu — dodanie nowej oferty na
początku przesuwa `oferta-1` → `oferta-2` i każdy komentarz poniżej wskazuje w cudzy blok.
To gorsze od osierocenia, bo cicho podmienia znaczenie zamiast zgłaszać brak.

Reguła: **id opisuje rolę, nie pozycję.** Powtarzalne bloki dostają sufiks pochodzący
z treści, nie z indeksu.

```
dobrze:  hero, kontakt, cennik, oferta-strzyzenie, oferta-broda, opinia-anna-k
źle:     oferta-1, sekcja-3, blok-2, div-7
format:  [a-z][a-z0-9-]{0,39}, unikalne w obrębie wersji
```

**Obowiązek odwrotny, po stronie silnika:** do promptu rundy musi wejść **lista id
z poprzedniej wersji**. „Zachowaj `data-cmt-id`" bez tej listy jest instrukcją bez
przedmiotu — model nie widzi, co miał zachować, dopóki nie przeczyta pliku, a walidacja
z 5.2 nie ma z czym porównywać.

### 3.2 Kształt komentarza

Powstaje w przeglądarce, w momencie kliknięcia:

```
Comment (frontend -> POST .../comments/apply)
{
  id         // GUID nadany w przeglądarce
  anchor     // data-cmt-id albo null
  text       // słowami klienta, bez obróbki
  viewport   // "desktop" | "mobile"
  createdAt
}
```

Trzy pola wymagają uzasadnienia:

- **`id` nadawane raz, w chwili dodania komentarza** — przy `202 + jobId` podwójne
  kliknięcie „Popraw to" jest realne, a id stabilne od momentu powstania czyni ponowienie
  idempotentnym: ten sam komentarz nie wchodzi do rundy dwa razy.

  > **Korekta 2026-09-04.** Pierwotnie było tu „GUID nadany w przeglądarce, nie z serwera".
  > To zdanie powstało, gdy żywy był jeszcze wariant TypeScript/Node, i przy **C#/Blazor
  > Server jest nieprawdziwe** — kod komponentu wykonuje się na serwerze. Własność, o którą
  > chodziło, zostaje w całości: stan obwodu jest per karta przeglądarki, więc id powstaje
  > raz na komentarz i nie zmienia się przy ponowieniu. Wymóg dla implementacji brzmi więc
  > **„raz, przy dodaniu"**, a nie „w przeglądarce" — i tak jest zrealizowany
  > w `CommentDto` (Plan C, Task 1).
- **`viewport`** — „telefon za mało widoczny" jest zależne od szerokości, przy której
  klient patrzył. Silnik nie ma jak tego odzyskać z plików, a bez tego komentarz jest
  nierozstrzygalny. To jedyna informacja o kontekście oglądania, którą przekazujemy.
- **`anchor: null` = komentarz globalny.** Bez osobnego znacznika i bez osobnego typu —
  brak anchora *jest* znaczeniem („całość za ciemna"). Jeden kształt, mniej gałęzi
  po obu stronach mostka.

**Kolejność ma znaczenie i jest kolejnością dodawania przez klienta.** Silnik przekazuje
komentarze do `instruction` w tej kolejności. Sprzeczne komentarze w jednej rundzie
(„cennik nad opiniami", potem „opinie nad cennikiem") rozstrzyga **ostatni** — i musi to
trafić do odpowiedzi tekstowej, bo klient inaczej uzna, że pierwszy został zignorowany.

Czego świadomie **nie** wysyłamy: migawki treści klikniętego bloku. Rozważałem ją jako
zabezpieczenie przed ponownym użyciem id, ale nie umiem powiedzieć, co silnik miałby
z niezgodnością zrobić — a pole, na które nikt nie reaguje, jest tylko kosztem po obu
stronach serializacji. Do dopisania, jeśli zobaczymy realne przypadki recyklingu id.

### 3.3 Status zmienia silnik, na podstawie raportu — nigdy z prozy modelu

Frontend tworzy komentarz w statusie `open` i umie go **wycofać** (usunąć, dopóki jest
`open`). Poza tym **nie dotyka statusu**. `open` → `applied` / `rejected` ustawia silnik.

Kluczowe: nie z tekstu podsumowania. Parsowanie prozy modelu pod status działa do
pierwszego przeformułowania i potem cicho gnije.

**Wymagane uzupełnienie sekcji 2 (Sebastian):** prompt rundy musi żądać od modelu
odpowiedzi **per komentarz, kluczowanej naszym `Comment.id`**, a `GenerateResult`
potrzebuje pola, które to przeniesie — dziś ma tylko `summary`:

```
GenerateResult {
  ...
  commentResults[] {
    commentId
    status   // "applied" | "rejected"
    note     // zdanie dla klienta: co zrobiono albo dlaczego nie
  }
}
```

`rejected` to normalny wynik, nie awaria — „nie mam zdjęcia, którym mógłbym to zastąpić"
jest uczciwą odpowiedzią i klient musi ją zobaczyć. Komentarz bez wpisu w
`commentResults` zostaje `open` i wchodzi do następnej rundy; nie zgadujemy za model.

Wynika z tego wymóg planu, sekcja 4 („klient musi wiedzieć, że został zrozumiany"):
`note` jest tekstem **przy komentarzu**, nie zbiorczym podsumowaniem wersji.

**Doprecyzowanie po przejściu ścieżki klienta (decyzja Przemka, 2026-09-04):** raport
`applied` + `note` musi **trafić klientowi przed oczy**, a nie tylko istnieć w danych.
Uwagi są zakotwiczone w wersji, więc po udanej rundzie klient patrzy już na następną i
lista uwag bieżącej wersji jest pusta — bez osobnej sekcji cały raport przepadałby, choć
silnik uczciwie go wystawia. Frontend czyta więc uwagi **wersji poprzedniej** i pokazuje
te ze statusem `applied` (`Editor`, sekcja „Co zrobiliśmy z Twoimi uwagami”). Czytamy z
API, nie z pamięci komponentu — inaczej pierwszy F5 kasuje klientowi potwierdzenie.

### 3.4 Osierocony komentarz — anchor żyje w obrębie wersji

Większość problemu rozwiązuje model danych z sekcji 6 planu: **`Comment` należy do
`Version`**. Anchor jest więc adresem w obrębie swojej wersji, a nie globalnym.
Historia renderuje się na tej wersji, na której komentarz powstał, więc późniejsza
zmiana id **nie psuje przeszłości** — to nie jest przypadek do obsługi.

Zostaje jeden realny: **5.2 wyczerpała limit prób** i nowa wersja nadal nie ma id,
do których przypięte były komentarze tej rundy.

Decyzja: **wersja jest dostarczana, nie odrzucana** — z listą na poziomie wersji:

```
Version {
  ...
  orphanedAnchors[]   // id obecne w poprzedniej wersji, nieobecne w tej
}
```

Nowa wersja bywa lepsza od poprzedniej, a wina za zmianę id leży po stronie modelu —
odrzucenie karałoby klienta za nasz problem. Ale **nigdy nie wyciszamy tego cicho**:
niepusta `orphanedAnchors[]` jest widoczna w UI.

Świadomie **nie** dodaję czwartego statusu komentarza w rodzaju „applied, ale
niepotwierdzone". Stan, którego nikt nie umie użyć do decyzji, to koszt bez zysku —
`orphanedAnchors[]` na wersji plus komunikat wystarczają, a `open`/`applied`/`rejected`
z sekcji 6 zostaje bez zmian.

Dokładne brzmienie komunikatu zależy od otwartej kwestii z sekcji 4 („co pokazujemy
klientowi przy `failed`") i tam ją zostawiam — to nie jest decyzja tej sekcji.

## 4. Limit rund i UX przy `failed` (wspólnie)

### 4.1 Dwa liczniki, bo mierzą różne rzeczy

Jeden licznik nie wystarczy: runda z jednym komentarzem i runda z dziesięcioma kosztują
różnie, a próba wyrażenia ochrony kosztu w rundach albo dusi klienta, albo nas nie chroni.

| Licznik | Czego pilnuje | Kto go widzi |
|---|---|---|
| **rundy** (`roundsUsed` / `roundsLimit`) | zrozumiałości dla klienta („zostały 3 poprawki") | klient |
| **budżet** (`spentUsd` / `budgetUsd`) | realnego kosztu projektu | tylko my |

**Asymetria, która jest tu sensem, nie szczegółem:** powtórki po `failed` i ponowienia
z 5.2 planu **zużywają budżet, ale nie zużywają rund klienta**. Pieniądze wydaliśmy naprawdę,
więc muszą być widoczne w budżecie — ale awaria po naszej stronie nie może zabierać
klientowi poprawek, na które umówiliśmy się z nim.

Projekt zatrzymuje **pierwszy** wyczerpany licznik. Jeśli budżet pada przed rundami,
to znak, że limit rund jest ustawiony za wysoko względem realnego kosztu — nie że klient
zrobił coś źle.

### 4.2 Wartości startowe — ekstrapolacja, nie pomiar

Uczciwie: mamy dwa pomiary i oba z **jednolinijkowej** strony (`claude-opus-5`, sekcja 11
planu) — generacja **$0,037**, runda komentarza **$0,026**. To wartości podłogowe.

Arytmetyka, którą z nich robię, jest jawnie zgadywana: prawdziwa strona to kilka sekcji
z treścią, przy każdej rundzie czytana od nowa — rzędu 6–8 tys. tokenów wejścia zamiast
kilkudziesięciu. Przyjmuję mnożnik **×5–10**, co daje **~$0,15–0,25 za rundę** i drożej
za wersję pierwszą (analiza pobranej strony): **~$0,50–1,00**.

Stąd wartości na start, do przestawienia:

```
roundsLimit  = 15        // widoczne dla klienta
budgetUsd    = 8.00      // twardy, z zapasem na powtórki i zły przypadek
```

**Trigger przestawienia (to on zastępuje zgadywanie):** po pierwszych **5 realnych
projektach** porównać medianę `total_cost_usd` na projekt — plan loguje ją od pierwszego
dnia. Jeśli mediana wychodzi poniżej ~$2, `roundsLimit` idzie w górę. Jeśli którykolwiek
projekt trafia w budżet przed piętnastą rundą, to `budgetUsd` jest ustawiony źle,
a nie klient zbyt gadatliwy.

Tańszy model na rundy komentarzy to osobna dźwignia i należy do ryzyka kosztowego
z sekcji 11 planu, nie do tego kontraktu.

### 4.3 `failed` — nie „powtórka albo komunikat", tylko dwie gałęzie

Pytanie było postawione jako alternatywa, ale rozstrzyga je przyczyna: **powtórka pomaga
tylko przy awarii nieokreślonej.** Ponowienie błędu deterministycznego to spalenie
pieniędzy na identyczny wynik.

Frontend nie powinien tego diagnozować, więc `Job` nie oddaje mu diagnozy, tylko
**gałąź, w którą ma pójść** — wymóg zwrotny do sekcji 1:

```
Job.failure {
  handling   // "retrying" | "halted"   <- po tym rozgałęzia się UI
  cause      // szczegół dla naszych logów, NIE do UI
  attempts
}
```

Podział `handling` po zachowaniu, nie po winie — bo brak klucza API pod `--bare`
i niepuste `permission_denials` to dwie różne przyczyny o **identycznym** zachowaniu
wobec klienta, a diagnozy grupowane po źródle wyprodukowałyby `switch`, który trzy
warianty mapuje na dwa komunikaty:

| Sytuacja (z pięciu warunków sekcji 2) | `handling` | Dlaczego |
|---|---|---|
| brak JSON na stdout, `exit != 0` | `retrying` | zwykle przejściowe (start procesu, zasoby) |
| `stop_reason` inny niż `end_turn` (limit, odmowa) | `retrying` | wynik modelu bywa inny przy powtórce |
| niepuste `permission_denials` | `halted` | nasza konfiguracja — powtórka da to samo |
| brak klucza / autoryzacja pod `--bare` | `halted` | to samo: deterministyczne, do naprawy u nas |
| wersja nie renderuje się po wyczerpaniu prób (twarda bramka 5.2) | `halted` | model zawiódł dwa razy na tym samym; trzecia próba to spalony budżet |

Powtórki są **niewidoczne dla klienta** (widzi jedno „pracuję nad tym"), ale widoczne
w budżecie z 4.1 i w `attempts`. Limit: **jedna** automatyczna powtórka, potem `halted`.

Wyczerpanie ponowień z **miękkiej** bramki 5.2 to nie ten przypadek — tam wersja jest
dostarczana z `orphanedAnchors[]` (sekcja 3.4), więc nie trafia tu w ogóle. Wiersz wyżej
dotyczy wyłącznie bramki **twardej** (renderowalność), rozdzielonej po tej sekcji.

Treść komunikatów pisze frontend — zgodnie z zasadą z sekcji 3 kontrakt oddaje gałąź,
nie gotowe zdania. Sens obu: *nic nie przepadło*, a przy `halted` dodatkowo, że problem
jest u nas i nie ma sensu klikać ponownie.

### 4.4 Gwarancja: nieudana runda nie zabiera klientowi niczego

Trzy mechanizmy dają to razem, więc zapisuję to jako **regułę kontraktu**, a nie jako
przypadkową konsekwencję — żeby późniejsza zmiana musiała ją świadomie złamać, a nie
rozmyć po cichu:

1. **poprzednia wersja zostaje nietknięta i oglądalna** — wynika ze snapshotów (decyzja 5):
   nowa wersja powstaje obok, nie na miejscu
2. **komentarze zostają `open`** — status zmienia tylko silnik i tylko z `commentResults[]`
   (sekcja 3.3), a nieudana runda nie produkuje tego raportu
3. **licznik rund się nie rusza** — asymetria z 4.1

Razem: nieudana runda jest z punktu widzenia klienta **brakiem zdarzenia**. Może kliknąć
„Popraw to" ponownie z tymi samymi komentarzami i nic nie musi odtwarzać.

### 4.5 Wyczerpanie limitu — zamrożenie, nie odcięcie

Projekt przechodzi w `frozen`. Wtedy:

- **podgląd i ZIP działają dalej** — klient nie traci dostępu do tego, co już ma
- ostatnia dobra wersja zostaje wersją bieżącą
- nowe rundy są odrzucane (`409`), komentarze można dodawać, ale nie stosować
- **nic nie jest usuwane**

Co dalej dzieje się z takim projektem, to pytanie o płatności, a te są poza zakresem v1
(sekcja 10 planu) — nie projektuję tu ścieżki dosprzedaży.

## 5. Historia wersji: powrót i podświetlanie zmian (Przemek)

Dwie decyzje produktowe podjęte przez Przemka 2026-09-04, obie mające konsekwencje dla
silnika — dlatego są tutaj, nie tylko w kodzie frontendu.

### 5.1 Powrót do wersji przestawia wskaźnik, nie kasuje historii

Powrót do starszej wersji **nie tworzy kopii** — ustawia `project.currentVersion` na
wybrany numer. Wersje nowsze zostają w historii jako porzucone; nic nie jest usuwane
(spójne z 4.5: zamrażamy, nie odcinamy).

Wynika z tego twardy wymóg, który łatwo przeoczyć:

> **`currentVersion` ≠ „ostatnia wersja".** Każde miejsce liczące „najnowszą" jako
> ostatni element listy staje się błędem w chwili pierwszego powrotu. Podgląd, ZIP
> i wersja, do której trafiają nowe uwagi, idą **za `currentVersion`**.

Kolejna runda po powrocie tworzy wersję o numerze `max(number) + 1` — nie
`liczba wersji + 1`, bo po powrocie te dwie liczby się rozjeżdżają i doszłoby do
kolizji numerów.

Powrót **nie zużywa rundy** — nie uruchamia modelu, więc nie ma za co pobierać opłaty
(logika z 4.4). Wolno go wykonać także przy `frozen`: zamrożenie blokuje *nowe rundy*,
a nie oglądanie tego, co klient już ma. Nie wolno go wykonać, gdy zadanie jest w toku —
kończąca się runda przestawiłaby wskaźnik i cicho unieważniła powrót.

### 5.2 Każda wersja zna swojego rodzica (`basedOn`)

```
Version {
  number
  basedOn      // numer wersji, Z KTOREJ ta powstala; null dla pierwszej
  changedAnchors[]   // data-cmt-id blokow roznych od rodzica
  orphanedAnchors[]
}
```

`basedOn` **nie jest** `number - 1`. Po powrocie do v1 kolejna runda tworzy v3, której
rodzicem jest **v1, nie v2**. Bez tego pola porównanie „co się zmieniło" dawałoby
poprawne wyniki w każdym przebiegu liniowym — czyli w każdym, jaki zwykle testujemy —
i cicho kłamałoby dokładnie tam, gdzie powrót do wersji ma sens.

### 5.3 Zmiany pokazujemy podświetleniem bloków, nie diffem kodu

Odbiorcą tego ekranu jest fryzjer, nie programista, więc diff linii HTML jest
bezużyteczny. Klient dostaje **swoją stronę z obramowanymi blokami**, które zmieniły się
względem rodzica. Porównujemy per `data-cmt-id`.

**Wymagane uzupełnienie sekcji 2 (Sebastian):** `changedAnchors[]` liczy ten, kto ma
snapshoty — czyli silnik, przy tworzeniu wersji. Frontend ma tylko to pokazać;
gdyby liczył sam, musiałby ściągać dwie pełne wersje strony do przeglądarki.

Porównanie idzie po **znormalizowanym `outerHtml`** bloku (zwinięte białe znaki,
uporządkowane atrybuty), nie po samym tekście. Powód jest konkretny: prośba „powiększ
telefon" zmienia `style`, a **tekst zostaje identyczny** — porównanie tekstu zgłosiłoby
„nic się nie zmieniło" w najczęstszym przypadku.

> **Znane ograniczenie, świadomie przyjęte:** gdy model przy okazji przepisze
> formatowanie bloku, którego nie dotyczyła żadna uwaga, blok podświetli się mimo braku
> zmiany widocznej dla klienta. To cena wybranego wariantu (podświetlanie zamiast dwóch
> podglądów obok siebie). Fałszywy alarm jest tu tańszy niż przeoczona zmiana: klient
> widzi obramowanie i sprawdza, zamiast przegapić coś, o co nie prosił.
