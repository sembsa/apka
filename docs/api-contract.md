# Kontrakt API — generator stron (v0.1, w budowie)

Wspólny artefakt Sebastiana i Przemka, do domknięcia **przed pierwszą linią kodu**.
Plan: [`plan/2026-09-04-generator-stron-plan.md`](plan/2026-09-04-generator-stron-plan.md).

Stack (decyzja 1): **C#/Blazor Server**, silnik `claude -p`, wersje jako snapshoty katalogów.

| Część | Właściciel | Status |
|---|---|---|
| 1. Zasoby HTTP | Sebastian | szkic poniżej, do recenzji |
| 2. Kontrakt silnika | Sebastian | wypełniony |
| 3. Kontrakt komentarza | **Przemek** | **do wypełnienia — od strony przeglądarki** |
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

## 3. Kontrakt komentarza (Przemek — do wypełnienia)

Przemek postawił warunek i jest on przyjęty: **ta część jest projektowana od strony
przeglądarki, nie od strony modelu .NET.** Jeśli `data-cmt-id` i status komentarza
powstaną w C# i dopiero potem przejdą przez interop, warstwa mostka się zemści.

Czego silnik potrzebuje od tej części, żeby zamknąć swoją stronę (pytania, nie propozycje):

1. **Format `data-cmt-id`** — kto go nadaje: generator w prompcie, czy frontend po
   wygenerowaniu? Silnik musi wiedzieć, żeby wpisać wymóg do promptu i sprawdzić go w 5.2.
2. **Kształt komentarza wysyłanego do rundy** — jakie pola dojdą do `instruction`
   (`anchor`, treść, kolejność, czy komentarz globalny ma osobny znacznik).
3. **Cykl życia statusu** — kto zmienia `open` → `applied`: silnik po rundzie, czy frontend
   po pokazaniu? Co ze komentarzem, którego model nie zrealizował.
4. **Osierocony komentarz** — czego frontend oczekuje, gdy 5.2 wykryje brakujące id:
   `failed` z listą osieroconych, czy wersja z ostrzeżeniem.

## 4. Otwarte, poza podziałem

- limit rund na projekt (ochrona kosztu) — wartość do ustalenia po pierwszych pomiarach
- co pokazujemy klientowi przy `failed`: powtórka automatyczna czy komunikat
