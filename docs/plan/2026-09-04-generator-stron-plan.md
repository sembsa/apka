# Generator stron dla nietechnicznych klientów — plan v0.1

Status: **decyzje zatwierdzone 2026-09-04** (sekcja 9). Nic nie jest zaimplementowane.
Kolejny krok: plan implementacji — dopiero po Waszym „tak" do tego dokumentu.

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

### Stabilność `data-cmt-id` — nic jej nie pilnuje samo z siebie

Model przepisujący stronę w piątej rundzie może zmienić `oferta-1` na `oferta-glowna`
albo scalić dwie sekcje. Wtedy komentarze przypięte do zniknionego id są osierocone,
a klient klika w podgląd, w którym jego uwagi wskazują w pustkę — i model nie zgłosi
tego jako błędu, bo po jego stronie wszystko się udało. Bramka walidacyjna jest w 5.2.

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
| `--session-id <uuid>` | **pierwszy** przebieg dla projektu — my nadajemy UUID |
| `--resume <uuid>` | **każdy kolejny** przebieg (runda komentarzy) w kontekście poprzednich; nie łączyć z `--session-id` w jednym wywołaniu |
| `--restricted` | usuwa Bash/PowerShell/REPL i WebFetch, zamyka narzędzia plikowe w katalogach roboczych, ignoruje ustawienia użytkownika/projektu/lokalne, odmawia `bypassPermissions`; `--strict-mcp-config` dorzucamy, żeby pominąć też serwery MCP |
| `--tools "Read,Write,Edit,Glob,Grep"` | zawęża zestaw wbudowanych narzędzi do pracy na plikach (to ta flaga zawęża/przywraca narzędzia w trybie `--restricted`, nie `--allowed-tools`) |
| `--permission-mode acceptEdits` | **bez tego `Write` jest cicho odrzucany, a zadanie kończy się „sukcesem"** — sprawdzone, patrz 5.1. Pod `--restricted` przepuszcza `Write`/`Edit` |
| `--permission-prompts none` | w produkcji nikt nie odpowiada na prompty — twarda odmowa zamiast czekania na hosta (domyślnie `host`) |
| `--bare` (produkcja) | pomija auto-wykrywanie `CLAUDE.md`, hooki i auto-memory; autoryzacja **wyłącznie** przez `ANTHROPIC_API_KEY` lub `apiKeyHelper`, OAuth i keychain nie są czytane |

`--restricted` odmawia `bypassPermissions` — sprawdzone: `Error: bypassPermissions not
supported in restricted mode`, exit 1, natychmiast, bez wywołania modelu.

**Katalog projektu klienta nie może leżeć wewnątrz tego repo.** Podproces startuje z
`cwd = workspaceDir` i auto-wykrywa `CLAUDE.md` — inaczej do sesji generującej stronę
fryzjera wjadą nasze zasady pracy w parze i indeks pamięci (koszt, szum, wyciek naszego
kontekstu do artefaktu klienta). `--restricted` tego nie zatrzymuje: ignoruje pliki
*settings*, nie `CLAUDE.md`. Projekty trzymamy poza drzewem repo, a w produkcji dokładamy
`--bare`.
| `--append-system-prompt` | nasze zasady: „prosta strona, jeden plik HTML + jeden CSS, bez frameworków" |
| `--model` | wybór modelu (prod: `claude-opus-5`) |

Katalog roboczy podprocesu = `workspaceDir`. Claude nie ma dostępu do sieci ani do
niczego poza katalogiem projektu.

**W v1 zostajemy przy `claude -p`. Claude Agent SDK — decyzja odłożona** (2026-09-04).

Dwie rzeczy warto rozdzielić, bo mieszają się w intuicji:

- **Samo SDK nic nie kosztuje** — to biblioteka (`@anthropic-ai/claude-agent-sdk`), nie licencja.
  Płaci się za zużycie modelu, dokładnie tak samo jak przy `claude -p`.
- **Kosztuje sposób rozliczenia w produkcji, i to niezależnie od SDK** — patrz sekcja 11.

Gdy wrócimy do SDK, opcje mapują się 1:1 (`cwd`, `allowedTools`, `resume`, `systemPrompt`,
`settingSources`), więc podmiana dotyka **jednego modułu**. Dlatego interfejs powyżej
istnieje od pierwszego dnia — nie dlatego, że planujemy migrację, a dlatego, że nie chcemy
przepisywać połowy aplikacji, jeśli kiedyś będzie sens.

### 5.1 Kiedy zadanie jest udane (bramka, nie exit code)

`claude -p` sygnalizuje sukces zbyt chętnie. Zmierzone na `claude 2.1.260`, ten sam prompt,
różnica tylko w trybie uprawnień:

| | bez `--permission-mode` | z `--permission-mode acceptEdits` |
|---|---|---|
| exit code | 0 | 0 |
| `is_error` / `subtype` | `false` / `success` | `false` / `success` |
| `stop_reason` / `terminal_reason` | `end_turn` / `completed` | `end_turn` / `completed` |
| `permission_denials` | `[{"tool_name":"Write",…}]` | `[]` |
| plik na dysku | **brak** | jest |
| koszt | **$0.0128 za nic** | $0.0186 |

Zadanie uznajemy za udane tylko wtedy, gdy **wszystkie** warunki są spełnione:

1. proces wypisał JSON na stdout — przy błędzie startu (np. odmowa `bypassPermissions`)
   stdout jest **pusty**, komunikat idzie na stderr, exit 1; worker nie może parsować w ciemno
2. exit 0, `is_error: false`, `subtype: "success"`
3. `stop_reason: "end_turn"` i `terminal_reason: "completed"`
4. **`permission_denials` jest puste** — niepuste traktujemy jak błąd zadania, niezależnie
   od exit code i `is_error`
5. artefakt przeszedł walidację z 5.2

Błędna konfiguracja uprawnień pali pieniądze cicho: przebieg z tabeli wyżej kosztował
$0.0128 i nie zostawił ani jednego pliku.

### 5.2 Walidacja wersji przed pokazaniem klientowi

Po każdej generacji, przed zapisaniem wersji i pokazaniem jej klientowi:

1. wyciągnij zbiór `data-cmt-id` z nowej wersji
2. porównaj ze zbiorem id, do których przypięte są **otwarte** komentarze
3. brakujące id = **zadanie naprawcze**, nie nowa wersja: dopięcie do promptu
   („zachowaj atrybuty `data-cmt-id`: …") i powtórka, z limitem prób
4. sanity check w tym samym kroku: HTML się parsuje, pliki z `changedFiles` istnieją,
   CSS jest podlinkowany. Wersja, która się nie renderuje, nie dochodzi do klienta

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

## 9. Decyzje — zatwierdzone 2026-09-04

Zgodne — zatwierdzone:

2. **Publikacja w v1: podgląd pod naszym linkiem + ZIP do pobrania.** Domeny i hosting osobno.
3. **Bez kont i logowania w v1.** Projekt = link z tokenem. Auth dokładamy, gdy generator się sprawdzi.
4. **Wyjście: statyczne HTML + CSS + minimum JS.** Bez frameworka, bez CMS.
6. **Silnik: `claude -p`, bez Claude Agent SDK na tę chwilę** (sekcja 5).

### ⚠ Do rozstrzygnięcia — sprzeczne decyzje (2026-09-04)

Sebastian i Przemek zdecydowali równolegle, przeciwnie. Nie wybieram za Was; poniżej bilans.

**1. Stack** — Sebastian: **TypeScript/Node**. Przemek: **C#/.NET**
([recenzja](2026-09-04-generator-stron-plan-recenzja-przemek.md)).

Uczciwie: **decyzja 6 (bez Agent SDK) wyjęła główny argument z rekomendacji A.** Przewagą
TS był oficjalny Agent SDK — skoro go nie używamy, zostaje tylko „jeden język front+back",
a frontend i tak jest w JS przy każdym wyborze backendu. Przeciwnie: `claude -p` odpalany
z .NET to zwykły `Process`, a C# to Wasz dom zawodowy. Argument Przemka („owijka jest
docelowa, nie przejściowa") jest konsekwencją Waszej własnej decyzji 6, nie kaprysem.
Jeśli chcecie jednego języka po obu stronach — jest też wariant C#/Blazor, wtedy podgląd
w iframe i przypinanie komentarzy idą przez JS interop.

**5. Wersjonowanie** — Sebastian: **repo git**, wersja = commit. Przemek: **kopie katalogów**.

Tu argument Przemka jest **mocniejszy niż mój pierwotny**: przy walidacji z 5.2 snapshotem
zostaje tylko wersja **zaakceptowana**, a odrzucona próba nie zostawia śmiecia w historii.
Przy git trzeba by generować na branchu i sprzątać nieudane commity. Koszt kopii katalogów:
diff „przed/po" piszemy sami — przy decyzji 4 to porównanie kilku plików tekstowych.

Podział pracy (propozycja, do potwierdzenia):

- **Sebastian:** silnik (adapter `claude -p`, parsowanie JSON, liczniki kosztu), workspace, kolejka, API
- **Przemek:** frontend — kreator wejścia, wybór propozycji, podgląd + przypinanie komentarzy
- **Wspólnie na start:** kontrakt API i model danych w `docs/api-contract.md`, żeby nie zderzać się w tych samych plikach

## 10. Poza zakresem v1

Płatności. Rejestracja domen i hosting. Narzędzia SEO. CMS i panel administracyjny.
Wielojęzyczność. Aplikacja mobilna. Sklep. Blog. Integracje z social media.
Konta zespołowe. Własne szablony klienta.

## 11. Ryzyka

**Koszt jednej iteracji — pierwsze pomiary są.** `claude-opus-5`, pusty katalog,
trywialna strona:

| Przebieg | Koszt | Czas |
|---|---|---|
| generacja v1 (`--session-id`) | $0.037 | ~7 s |
| runda komentarza (`--resume`, jedna poprawka) | $0.026 | ~9 s |
| przebieg odrzucony przez uprawnienia (bez efektu) | $0.0128 | ~8 s |

To wartości **podłogowe**, nie szacunek dla prawdziwej strony (kilka sekcji, treść, analiza
pobranego HTML-a — rząd wielkości więcej). Ale rząd „grosze za rundę, nie złotówki" jest
już widoczny. `--resume` sprawdzone end-to-end: sesja podjęta, `session_id` ten sam,
poprawka trafiła w istniejący plik.

**Licznik kosztu sumuje `total_cost_usd`, nie tokeny jednego modelu** — `modelUsage`
zawiera obok `claude-opus-5` także `claude-haiku-4-5` (wywołania pomocnicze harnessu).
Limit iteracji na projekt zostaje jako zabezpieczenie.

**Rozliczenie — to nie jest kwestia SDK.** Dokumentacja Claude Agent SDK stawia to wprost:
zewnętrznym deweloperom **nie wolno** oferować w swoich produktach loginu claude.ai ani
limitów subskrypcji bez wcześniejszej zgody Anthropic — produkt ma używać uwierzytelniania
kluczem API. Napisane jest to o Agent SDK, ale dotyczy sposobu rozliczenia produktu, więc
**tak samo obejmuje produkcję na `claude -p`**: obsługa obcych, płacących klientów z naszej
subskrypcji Claude Code nie jest drogą do przodu.

Wniosek praktyczny: prototyp i dev na własnej subskrypcji — normalne użycie narzędzia
developerskiego. Pierwszy płacący klient = klucz API i przeliczony koszt na projekt.
Rezygnacja z Agent SDK **niczego tu nie oszczędza** — oszczędza tylko tyle, ile daje
brak dodatkowej zależności. Warunki potwierdzić u Anthropic przed startem komercyjnym.

**Treść pobranej strony to niezaufane wejście.** Strona klienta (albo cudza) może zawierać
tekst, który udaje instrukcję dla modelu. Pobrany HTML traktujemy jak dane: zapisujemy do
pliku, w prompcie oznaczamy jako materiał źródłowy do analizy, nigdy jako polecenie.
`--restricted` ogranicza szkody (brak Bash, brak sieci, tylko katalog projektu).

**Jakość.** Strona ma być prosta, ale nie tania w odbiorze. Potrzebujemy zestawu
5–10 przykładowych briefów (fryzjer, hydraulik, kancelaria, food truck…) i oceny wyników,
zanim uznamy, że generator działa.
