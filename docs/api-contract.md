# Kontrakt API — generator stron (v0.1, w budowie)

Wspólny artefakt Sebastiana i Przemka, do domknięcia **przed pierwszą linią kodu**.
Plan: [`plan/2026-09-04-generator-stron-plan.md`](plan/2026-09-04-generator-stron-plan.md).

Stack (decyzja 1): **C#/Blazor Server**, silnik `claude -p`, wersje jako snapshoty katalogów.

| Część | Właściciel | Status |
|---|---|---|
| 1. Zasoby HTTP | Sebastian | szkic poniżej, do recenzji |
| 2. Kontrakt silnika | Sebastian | wypełniony |
| 3. Kontrakt komentarza | **Przemek** | wypełniony — dwa wymogi zwrotne do sekcji 2 i 4 planu |
| 4. Model danych | wspólnie | za planem, sekcja 6 |

---

## 1. Zasoby HTTP (szkic — Sebastian)

Wszystko, co uruchamia model, jest **zadaniem** (`Job`), nie żądaniem synchronicznym.
Endpoint zwraca `202` z `jobId`; postęp idzie po obwodzie SignalR (Blazor Server),
a `GET /api/jobs/{id}` zostaje dla klientów poza naszym UI.

| Metoda | Ścieżka | Zwrot |
|---|---|---|
| `POST` | `/api/projects` | `201` + `projectId`, `token` (dostęp bez konta — decyzja 3) |
| `GET` | `/api/projects/{id}` | stan projektu, lista wersji, aktualna wersja |
| `POST` | `/api/projects/{id}/proposals` | `202` + `jobId` — generacja 3 propozycji |
| `POST` | `/api/projects/{id}/proposals/{proposalId}/choose` | `200` — wybór klienta |
| `POST` | `/api/projects/{id}/versions` | `202` + `jobId` — wersja 1 z wybranej propozycji |
| `POST` | `/api/projects/{id}/versions/{n}/comments/apply` | `202` + `jobId` — runda komentarzy |
| `GET` | `/api/projects/{id}/versions` | lista wersji (numer, data, koszt) |
| `GET` | `/api/projects/{id}/versions/{n}/preview` | statyczne pliki wersji do iframe |
| `GET` | `/api/projects/{id}/versions/{n}/zip` | ZIP (decyzja 2) |
| `GET` | `/api/jobs/{id}` | status zadania |

Status zadania: `queued` \| `running` \| `succeeded` \| `failed`. **`failed` obejmuje
przypadki, w których model „zakończył sukcesem"** — patrz sekcja 2.

## 2. Kontrakt silnika (Sebastian — wypełniony)

```
GenerateRequest {
  workspaceDir   // katalog projektu, POZA drzewem tego repo (plan, sekcja 5)
  instruction    // brief albo zestaw komentarzy
  sessionId?     // brak = pierwszy przebieg (--session-id), obecny = --resume
}

GenerateResult {
  sessionId
  changedFiles[]
  summary            // tekst dla klienta: co zostało zmienione
  totalCostUsd       // suma total_cost_usd, NIE tokeny jednego modelu
  permissionDenials[]
}
```

Zadanie jest udane tylko wtedy, gdy **wszystkie** warunki są spełnione (plan, 5.1):

1. proces wypisał JSON na stdout (błąd startu = pusty stdout, komunikat na stderr, exit 1)
2. exit 0, `is_error: false`, `subtype: "success"`
3. `stop_reason: "end_turn"`, `terminal_reason: "completed"`
4. **`permissionDenials` jest puste**
5. wersja przeszła walidację z 5.2 (`data-cmt-id` + renderowalność)

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

- **`id` z przeglądarki, nie z serwera** — przy `202 + jobId` podwójne kliknięcie „Popraw to"
  jest realne. Id nadane po stronie klienta czyni ponowienie idempotentnym: ten sam
  komentarz nie wchodzi do rundy dwa razy.
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

## 4. Otwarte, poza podziałem

- limit rund na projekt (ochrona kosztu) — wartość do ustalenia po pierwszych pomiarach
- co pokazujemy klientowi przy `failed`: powtórka automatyczna czy komunikat
