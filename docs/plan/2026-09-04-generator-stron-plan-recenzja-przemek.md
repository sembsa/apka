# Recenzja planu „Generator stron" v0.1 — Przemek

Dotyczy: [`2026-09-04-generator-stron-plan.md`](2026-09-04-generator-stron-plan.md)

Plan jest dobry i sekcja 5 jest rzetelna — sprawdziłem wszystkie wymienione flagi na
`claude 2.1.260` i istnieją, opisy się zgadzają. Poniżej trzy rzeczy do poprawienia
oraz pierwsze zmierzone koszty, których brakowało w sekcji 11.

---

## 1. Brak trybu uprawnień w tabeli flag (blokuje silnik)

`--restricted` **ogranicza**, ale niczego **nie autoryzuje**. Bez trybu uprawnień
`Write` jest odrzucany — a to, co się wtedy dzieje, jest gorsze niż błąd.

Zmierzone (`--restricted --tools "Read,Write,Edit,Glob,Grep" --output-format json`,
bez flagi uprawnień, katalog roboczy pusty):

```
exit code:          0
is_error:           false
subtype:            "success"
terminal_reason:    "completed"
pliki w katalogu:   żadnego
permission_denials: [{"tool_name":"Write", ...,"file_path":".../index.html"}]
```

**Zadanie kończy się „sukcesem" i nie tworzy nic.** Worker z sekcji 7 uzna je za
wykonane, klient dostanie „gotowe" i pustą stronę. Jedynym śladem jest niepuste
`permission_denials` w JSON-ie.

Do planu:

- w tabeli flag dochodzi `--permission-mode acceptEdits` — sprawdzone, pod
  `--restricted` przepuszcza `Write`/`Edit`, `permission_denials` puste, plik powstaje
- worker traktuje **niepuste `permission_denials` jako błąd zadania**, niezależnie od
  `is_error` i kodu wyjścia. To samo dotyczy `stop_reason` innego niż `end_turn`
- do rozważenia `--permission-prompts none` jako pas bezpieczeństwa: nikt nie odpowiada
  na prompty w produkcji, więc lepiej twarda odmowa niż czekanie na hosta

Nie weryfikowałem jednej rzeczy: że `--restricted` odmawia `bypassPermissions`
(tak twierdzi plan). Klasyfikator uprawnień u mnie zablokował tę próbę i nie
przepychałem się z nim — do sprawdzenia po Twojej stronie.

## 2. `workspaceDir` nie może leżeć wewnątrz tego repo

Podproces `claude -p` odpala się z `cwd = workspaceDir` i **auto-wykrywa `CLAUDE.md`**.
Jeśli katalog projektu klienta wyląduje pod naszym repo, do sesji generującej stronę
fryzjera wjadą nasze zasady pracy w parze i indeks pamięci — czyli koszt, szum
i wyciek naszego kontekstu do artefaktu klienta. `--restricted` tego nie zatrzymuje:
ignoruje pliki *settings*, nie `CLAUDE.md`.

Dwa wyjścia, nie wykluczają się:

- katalogi projektów poza drzewem repo (np. `%LOCALAPPDATA%/generator/projects/<id>`)
- `--bare` — pomija auto-wykrywanie `CLAUDE.md`, hooki i auto-memory

Poza higieną kontekstu `--bare` ma drugą właściwość: **wymusza autoryzację przez
`ANTHROPIC_API_KEY` albo `apiKeyHelper` i nigdy nie czyta OAuth ani keychaina.**
Czyli jest prawdopodobnie i tak docelowym kształtem produkcyjnym — dev na subskrypcji,
prod na kluczu API, ta sama owijka, jedna flaga różnicy.

> **Korekta po `9d1c141`.** Napisałem tu pierwotnie, że to „zdejmuje część ryzyka
> rozliczenia z sekcji 11". Za mocno — Sebastian ma rację: `--bare` daje *mechanizm*
> (klucz API), ale nie odpowiada na pytanie o *warunki*, a te są niezależne od tego,
> czy jedziemy na SDK czy na `claude -p`. Flaga załatwia „czym się uwierzytelniamy",
> nie „czy nam wolno". Warunki i tak trzeba potwierdzić u Anthropic.

Warto `--bare` przetestować wcześniej niż później, bo wyłącza też rzeczy, na których
moglibyśmy nieświadomie oprzeć silnik (hooki, auto-memory, discovery `CLAUDE.md`).

## 3. Nic nie pilnuje stabilności `data-cmt-id` między wersjami

To najpoważniejsza dziura produktowa, bo pętla komentarzy **jest** produktem.
Sekcja 4 zakłada, że `data-cmt-id` są stabilne, ale nic tego nie wymusza — model
przepisujący stronę w rundzie 5 może zmienić `oferta-1` na `oferta-glowna` albo
scalić dwie sekcje w jedną. Wtedy każdy otwarty komentarz przypięty do znikniętego
id jest osierocony i klient klika w podgląd, w którym jego uwagi wskazują w pustkę.
Model nie zgłosi tego jako błędu — po jego stronie wszystko się udało.

Potrzebny krok walidacji po każdej generacji, przed pokazaniem wersji klientowi:

1. wyciągnij zbiór `data-cmt-id` z nowej wersji
2. porównaj ze zbiorem id, do których przypięte są **otwarte** komentarze
3. brakujące id = zadanie do naprawy, nie nowa wersja — dopięcie do promptu
   („zachowaj atrybuty `data-cmt-id`: …") i powtórka, z limitem prób
4. przy okazji, w tym samym kroku: sanity check strony (HTML się parsuje, pliki
   z `changedFiles` istnieją, CSS podlinkowany) — wersja, która się nie renderuje,
   nie powinna dojść do klienta

Zasada ogólna, wynikająca z punktów 1 i 3: **`claude -p` sygnalizuje sukces zbyt
chętnie.** Silnik potrzebuje własnej bramki akceptacji wersji, a nie wiary w exit code.

---

## Pierwsze zmierzone koszty (do sekcji 11)

Model `claude-opus-5`, katalog roboczy pusty, trywialna strona (jeden plik, jedna linia):

| Przebieg | Koszt | Czas |
|---|---|---|
| generacja v1 (`--session-id`) | **$0.037** | ~7 s |
| runda komentarza (`--resume`, jedna poprawka) | **$0.026** | ~9 s |

Trzy rzeczy, które z tego wynikają:

- **`--resume` działa tak, jak zakłada plan.** Sesja została podjęta, `session_id`
  wrócił ten sam, poprawka trafiła w istniejący plik. Mechanizm z sekcji 5 jest sprawdzony
  end-to-end, nie tylko w `--help`.
- **To są wartości podłogowe, nie szacunek.** Prawdziwa strona to kilka sekcji, treść,
  analiza pobranego HTML-a — spodziewam się rzędu wielkości więcej. Ale rząd
  „grosze za rundę, nie złotówki" jest już widoczny.
- **W koszcie zadania siedzi więcej niż jeden model.** `modelUsage` pokazał obok
  `claude-opus-5` także `claude-haiku-4-5` (wywołania pomocnicze harnessu). Licznik
  kosztu musi sumować `total_cost_usd`, a nie liczyć tokenów jednego modelu.

## Sekcja 9 — moje stanowisko

- **2. Publikacja (podgląd + ZIP):** zgoda, bez uwag. Domeny i certyfikaty to osobny projekt.
- **3. Bez kont w v1 (link z tokenem):** zgoda. Token w linku wystarcza, dopóki nie ma
  czego chronić poza jedną stroną.
- **4. Statyczne HTML+CSS, bez frameworka:** zgoda, i to jest też warunek punktu 3
  powyżej — im prostsze wyjście, tym łatwiej sprawdzić, że wersja jest poprawna.
- **1. Stack: TypeScript/Node** — zgoda, przyjmuję rekomendację i zapis z sekcji 9.
- **5. Wersjonowanie — nie zgadzam się z git, proponuję snapshoty katalogów.**
  Jedyny punkt sporny w całym planie, uzasadnienie niżej. Do rozstrzygnięcia z Sebastianem.

### Decyzja 5: snapshoty zamiast repo git per projekt

Snapshoty składają się z punktem 3 tej recenzji, a git nie:

- generacja idzie do katalogu roboczego, **przechodzi walidację (`data-cmt-id` + render),
  i dopiero zaakceptowana wersja staje się snapshotem**. Odrzucona próba nie zostawia
  po sobie śladu, bo historia to katalogi, które sami tworzymy
- przy repo git nieudane generacje trzeba albo trzymać na branchu i sprzątać, albo
  commitować dopiero po walidacji — czyli i tak potrzebujemy katalogu roboczego poza
  historią. Git dokłada wtedy warstwę, nie zdejmuje jej
- dochodzi drobiazg operacyjny: podproces `claude -p` pisze **do tego samego katalogu**,
  w którym byłoby repo. Nie chcę zgadywać, co robi model, który znajdzie `.git`
  w katalogu roboczym, kiedy ma zwykłe narzędzia plikowe i instrukcję „popraw stronę"

Koszt do przyjęcia świadomie: diff „przed/po" trzeba zrobić samemu. Przy decyzji 4
(statyczne HTML+CSS, kilka plików) to porównanie plików tekstowych po nazwie, nie
obsługa drzewa — a `Version.snapshotRef` z sekcji 6 działa jednakowo w obu wariantach.

Jeśli Sebastian ma inne argumenty za gitem, ustępuję — to nie jest kwestia, na której
warto blokować start. Ale bramka akceptacji wersji z punktu 3 musi zostać niezależnie
od tego, który wariant wygra.

### Konkret pod Node, niezależny od decyzji

Proces `claude -p` **czeka 3 sekundy na stdin**, jeśli go nie dostanie
(`Warning: no stdin data received in 3s, proceeding without it` — złapane w testach).
Przy `child_process.spawn` trzeba ustawić `stdio: ['ignore', 'pipe', 'pipe']`, inaczej
doklejamy 3 sekundy do **każdego** zadania w kolejce. Prompt idzie argumentem, nie stdinem.

Podział pracy z propozycji przyjmuję (frontend: kreator, wybór propozycji, podgląd
i przypinanie komentarzy). Kontrakt API robimy wspólnie przed pierwszą linią kodu —
punkt 3 tej recenzji dotyczy właśnie granicy między silnikiem a frontendem, więc
`data-cmt-id` i status komentarza muszą wejść do `docs/api-contract.md`.
