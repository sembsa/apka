# Generator stron dla nietechnicznych klientów — plan v0.1

Status: **propozycja do recenzji**. Sebastian + Przemek czytają i zgłaszają poprawki.
Nic nie jest zaimplementowane. Sekcja 9 to decyzje, których potrzebujemy od Was.

Klasyfikacja: **architektoniczna** (nowy projekt, brak istniejącego kodu).

---

## 1. Cel

Klient nietechniczny chce **prostą** stronę www i nie umie jej zrobić. Wchodzi na naszą stronę,
podaje adres swojej obecnej strony **albo** opisuje pomysł — i dalej **nic nie robi sam**.
Claude proponuje: treść, układ, kolory, warianty. Klient tylko **wybiera** i **komentuje**.

W edytorze klient nie poprawia niczego ręcznie. Klika w element strony, pisze
„telefon za mało widoczny", „to zdjęcie brzydkie", „chcę cennik nad opiniami" —
a Claude wprowadza zmiany i pokazuje nową wersję.

Test dla każdej funkcji w v1: *czy człowiek, który nie umie zrobić strony, potrzebuje tego
na start?* Jeśli nie — poza zakresem.

## 2. Dwa wejścia, jeden przepływ

**A. Mam stronę, jest słaba** — klient podaje URL. Nasz backend (nie Claude) pobiera stronę,
zapisuje HTML/tekst/listę zdjęć do katalogu roboczego projektu. Claude analizuje: co tam jest,
czego brakuje, co odstrasza klientów.

**B. Nie mam nic** — krótki kreator, maks. 5 pytań w prostym języku: czym się zajmujesz,
dla kogo, jak się z Tobą skontaktować, godziny/lokalizacja, zdjęcia (opcjonalnie).
Bez pytań o technologię, fonty, kolory.

Oba wejścia kończą się tym samym artefaktem: **brief** (JSON) w katalogu projektu.

## 3. Przepływ główny

```
wejście (URL | pomysł)
  -> brief
  -> Claude generuje 3 propozycje kierunku (nazwa, ton, układ, sekcje, przykładowy nagłówek)
  -> klient wybiera jedną (klik, bez pisania)
  -> Claude generuje wersję 1 (kompletna, działająca strona)
  -> PĘTLA KOMENTARZY  <-- to jest produkt
  -> publikacja / pobranie
```

Propozycje pokazujemy jako **podglądy**, nie jako opisy tekstowe — klient ma zobaczyć,
nie czytać specyfikację.

## 4. Pętla komentarzy (rdzeń produktu)

Podgląd strony w iframe. Generator wstrzykuje do każdego bloku stabilny atrybut
`data-cmt-id` (np. `hero`, `oferta-1`, `kontakt`). Klik w blok w trybie komentowania
przypina komentarz do tego id. Komentarz bez klikniętego bloku = komentarz globalny
(„całość za ciemna").

Runda wygląda tak:

1. Klient dorzuca 1–N komentarzy (nie musi wysyłać po jednym)
2. Klika „Popraw to"
3. Silnik dostaje: pliki aktualnej wersji + wszystkie otwarte komentarze + brief
4. Powstaje nowa wersja; komentarze dostają status `applied` z krótkim opisem, co zrobiono
5. Klient widzi porównanie „przed / po" i może wrócić do dowolnej wersji

Każda wersja to **snapshot katalogu** projektu — nie diff w bazie. Cofnięcie się do wersji 3
to skopiowanie snapshotu 3 na wierzch. Katalog projektu może być repozytorium git
(wtedy wersja = commit) — patrz decyzja 5.

Odpowiedź na komentarz jest też **tekstowa**: „powiększyłem telefon i wrzuciłem go do nagłówka".
Klient musi wiedzieć, że został zrozumiany.

## 5. Silnik generowania — warstwa wymienna

Jeden interfejs, jedna implementacja na start:

```
generate({
  workspaceDir,     // katalog projektu — jedyne miejsce, gdzie Claude może pisać
  instruction,      // brief albo komentarze klienta
  sessionId?        // kontynuacja rozmowy w obrębie projektu
}) -> { sessionId, changedFiles[], summary, usage }
```

**Implementacja dev (v1): `claude -p` jako podproces.** Flagi sprawdzone na `claude 2.1.260`:

| Flaga | Po co |
|---|---|
| `-p` / `--print` | tryb nieinteraktywny |
| `--output-format json` | jeden JSON na wyjściu: wynik + `usage` (koszt do licznika) |
| `--output-format stream-json` + `--include-partial-messages` | wariant do pokazywania postępu na żywo |
| `--session-id <uuid>` | my nadajemy UUID sesji per projekt |
| `--resume <uuid>` | kolejna runda komentarzy w kontekście poprzednich |
| `--restricted` | usuwa Bash/PowerShell/REPL i WebFetch, zamyka narzędzia plikowe w katalogu roboczym, ignoruje ustawienia użytkownika i projektu, odmawia `bypassPermissions` |
| `--allowed-tools "Read Write Edit Glob Grep"` | tylko praca na plikach |
| `--strict-mcp-config` | żadnych serwerów MCP z maszyny |
| `--append-system-prompt` | nasze zasady: „prosta strona, jeden plik HTML + jeden CSS, bez frameworków" |
| `--model` | wybór modelu (prod: `claude-opus-5`) |

Katalog roboczy podprocesu = `workspaceDir`. Claude nie ma dostępu do sieci ani do
niczego poza katalogiem projektu.

**Implementacja prod (później): Claude Agent SDK** (`@anthropic-ai/claude-agent-sdk`,
oficjalnie Python i TypeScript). Opcje mapują się 1:1 — `cwd`, `allowedTools`, `resume`,
`systemPrompt`, `settingSources` — więc podmiana dotyka **jednego modułu**.
Dlatego interfejs powyżej istnieje od pierwszego dnia.

## 6. Model danych

| Encja | Pola |
|---|---|
| `Project` | id, kontakt klienta, źródło (`url` \| `idea`), workspaceDir, status |
| `Proposal` | id, projectId, wariant (A/B/C), brief, wybrany? |
| `Version` | id, projectId, numer, sessionId, snapshotRef, koszt |
| `Comment` | id, versionId, anchor (`data-cmt-id` albo null), treść, status (`open`/`applied`/`rejected`) |
| `Job` | id, projectId, typ, status, log, błąd |

Nic więcej w v1. Brak kont, brak zespołów, brak roli.

## 7. Praca asynchroniczna

`claude -p` trwa od ~30 sekund do kilku minut. Żadne żądanie HTTP tego nie przeżyje,
a klient musi widzieć, że coś się dzieje. Dlatego:

- kolejka zadań (`Job`), jeden worker na start, jedno zadanie na projekt jednocześnie
- status przez SSE albo polling `/api/projects/:id/jobs/:jobId`
- UI: „Claude pracuje nad Twoją stroną…" + to, co już wiadomo (nazwy kroków)
- zadanie ma twardy limit czasu i limit iteracji na projekt

To nie jest szczegół implementacyjny — bez tego produkt jest nieużywalny.

## 8. Rozważone podejścia (stack)

**A. TypeScript/Node — rekomendacja.** Oficjalny Claude Agent SDK, jeden język na front i back,
działa u Przemka na Windows bez kombinowania. Frontend (podgląd, przypinanie komentarzy)
i tak jest w JS, więc drugi język byłby dodatkowym kosztem bez zysku.

**B. C#/.NET.** Wasz dom (Soneta/enova). Ale nie ma oficjalnego Claude Agent SDK dla .NET —
zostajemy przy owijce na `claude -p` na stałe, albo schodzimy do Messages API i tracimy
gotowy harness do pracy na plikach. Sensowne, jeśli to ma trafić do firmowej infrastruktury.

**C. Python.** Oficjalny SDK jest, ale frontend zostaje w JS — czyli dwa języki, jak w B,
bez przewagi B (znajomość).

## 9. Decyzje do potwierdzenia

1. **Stack** — rekomendacja: **A (TypeScript/Node)**. Wybór B zmienia sekcję 5, nie resztę planu.
2. **Publikacja w v1** — rekomendacja: **podgląd pod naszym linkiem + ZIP do pobrania**.
   Własne domeny, hosting i certyfikaty to osobny projekt.
3. **Konta / wielu użytkowników w v1** — rekomendacja: **nie**. Projekt = link z tokenem.
   Logowanie dokładamy, gdy produkt zadziała.
4. **Format wyjściowy** — rekomendacja: **statyczne HTML + CSS + minimum JS, bez frameworka
   i bez CMS**. Prosta strona ma być prosta również w środku (i tania w kolejnej iteracji).
5. **Wersjonowanie projektu** — rekomendacja: **katalog projektu jako repo git**, wersja = commit.
   Dostajemy historię, rollback i diff „przed/po" darmo. Alternatywa: kopie katalogów.

Oraz podział pracy (do ustalenia, propozycja):

- **Sebastian:** silnik (adapter `claude -p`, parsowanie JSON, liczniki kosztu), workspace, kolejka, API
- **Przemek:** frontend — kreator wejścia, wybór propozycji, podgląd + przypinanie komentarzy
- **Wspólnie na start:** kontrakt API i model danych w `docs/api-contract.md`, żeby nie zderzać się w tych samych plikach

## 10. Poza zakresem v1

Płatności. Rejestracja domen i hosting. Narzędzia SEO. CMS i panel administracyjny.
Wielojęzyczność. Aplikacja mobilna. Sklep. Blog. Integracje z social media.
Konta zespołowe. Własne szablony klienta.

## 11. Ryzyka

**Koszt jednej iteracji.** Nie znamy go, dopóki nie zmierzymy — `--output-format json`
zwraca `usage`, więc od pierwszego dnia logujemy koszt każdego zadania i pokazujemy sobie
średnią na projekt. Limit iteracji na projekt chroni przed niespodzianką.

**Rozliczenie i licencja.** Dev na Waszej subskrypcji Claude Code to jedno; produkt
komercyjny obsługujący obcych klientów najpewniej musi jechać na kluczu API i osobnym
rozliczeniu. **Do sprawdzenia w warunkach Anthropic przed pierwszym płacącym klientem** —
nie zgaduję.

**Treść pobranej strony to niezaufane wejście.** Strona klienta (albo cudza) może zawierać
tekst, który udaje instrukcję dla modelu. Pobrany HTML traktujemy jak dane: zapisujemy do
pliku, w prompcie oznaczamy jako materiał źródłowy do analizy, nigdy jako polecenie.
`--restricted` ogranicza szkody (brak Bash, brak sieci, tylko katalog projektu).

**Jakość.** Strona ma być prosta, ale nie tania w odbiorze. Potrzebujemy zestawu
5–10 przykładowych briefów (fryzjer, hydraulik, kancelaria, food truck…) i oceny wyników,
zanim uznamy, że generator działa.
