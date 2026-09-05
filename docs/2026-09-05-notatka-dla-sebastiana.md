# Notatka dla Sebastiana — 2026-09-05

## 32 czerwone testy silnika u mnie, 0 u Ciebie: kultura systemu

Po `pull` dostałem `Generator.Engine.Tests` **137/169**. Wszystkie 32 pady w
`GenerationServiceTests` i `PropozycjeTests`. U Ciebie zielono — i to nie przypadek.

Helpery budujące odpowiedź CLI składają JSON interpolacją:

```csharp
"total_cost_usd":{{cost}}
```

`cost` to `decimal`, a interpolacja formatuje **kulturą systemu**. Na polskich
ustawieniach regionalnych wychodzi `"total_cost_usd":0,05` — niepoprawny JSON, bramka
akceptacji nie ma czego przeczytać, `outcome.Succeeded == false`. Dowód: ten sam commit,
bez żadnej zmiany kodu, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` → **169/169**.

Poprawione: `Kwota(decimal)` z `CultureInfo.InvariantCulture` w obu plikach. Po tym
168/169 przy polskiej kulturze — ostatni to opisany niżej flaky, nie ta sprawa.

**Kod produkcyjny jest czysty** — sprawdziłem: liczby idą przez `System.Text.Json`, który
zawsze pisze niezmiennie. To defekt wyłącznie w helperach testowych. Ale wart uwagi na
przyszłość: każdy test składający JSON albo prompt z liczby przez interpolację będzie
zielony u Ciebie i czerwony u mnie.

## Drugi, osobny: `Nieudana_runda_przywraca_workdir_do_ostatniej_udanej_wersji`

Pada **tylko w pełnym przebiegu**, uruchomiony osobno przechodzi 3/3.
`UnauthorizedAccessException` z `File.Move(tmp, MetaPath, overwrite: true)` w
`ProjectStore.Save`. Wygląda na wyścig o `project.json` przy równoległych klasach
testowych (na Windows `File.Move` nie znosi otwartego uchwytu). Nie ruszam — Twój
obszar, a chcę najpierw wiedzieć, czy widzisz to u siebie.

## Do Twojej sekcji 6 kontraktu

Odpowiem osobno, na spokojnie — 6.1 (apply kasujące `open`) to zmiana, która dotyka
mojego §3.3, więc nie chcę jej kwitować jednym zdaniem między restartami serwera.

Dzięki za znalezienie mojego `Contains("/2")` — GUID na 2 raz na szesnaście to dokładnie
ten rodzaj testu, który przechodzi i niczego nie broni. Asercja była moja i była zła.

---

# Sekcja 6 rozstrzygnięta — 2026-09-05, wieczór

Przemek zdecydował. Kontrakt zaktualizowany, sekcja 6 nie jest już listą pytań tylko
listą decyzji z uzasadnieniem.

## 6.1 — osobny zapis uwag, bez rundy. ZROBIONE po obu stronach

`PUT /api/projects/{id}/versions/{n}/comments`. `apply` tylko uruchamia rundę.

Rozstrzygnął nie sam wyjątek pustej listy, ale rzecz szersza, którą zobaczyłem czytając
Twój opis: uwagi żyły **wyłącznie w pamięci obwodu** do kliknięcia „Popraw to". Klient,
który dodał pięć uwag i zamknął kartę, tracił wszystkie pięć. Przy „bez kont, link
z tokenem" przerwanie pracy i powrót później to normalny scenariusz, nie brzeg.
Wariant z `DELETE` pojedynczej uwagi kosztował tyle samo, a tego nie naprawiał.

Zrobione:

- `IProjectApi.SaveCommentsAsync` + `ProjectApi` (Twoje straże: `frozen`, `stale`,
  `MaAktywne` — ta ostatnia dlatego, że `Uzgodnij` i `ApplyResults` to dwie sekwencje
  czytaj-zmodyfikuj-zapisz na tym samym pliku, dokładnie jak opisałeś)
- trasa `PUT` w `ApiEndpoints` — **dotknąłem Twojego pliku**, jedna trasa wzorowana na
  `apply`; jeśli wolisz inaczej, przepisz
- `MockProjectApi.SaveCommentsAsync` z tymi samymi typami wyjątków
- `Editor`: `AddDraft` i `Withdraw` wołają zapis, kolizje idą przez Twoje
  `Kolizja`/`Odswiez`
- dwa testy: `Dodana_uwaga_przezywa_zamkniecie_karty`,
  `Wycofana_uwaga_znika_takze_z_serwera`. Sprawdziłem podmianą, że oba padają bez zapisu

Twoja „znana luka" (wycofanie wszystkich uwag nie dociera na dysk, bo `Apply` wraca
wcześnie) **znika sama** — zapis nie idzie już przez `Apply`. Przeklikane: uwaga
przeżywa powrót do linku, wycofana nie wraca, licznik zostaje `0 z 15`.

## 6.2 — koszt wypada, data wchodzi. CZĘŚĆ DLA CIEBIE

Koszt z listy wersji **wypada**: zdanie z 4.1 jest mocniejsze niż tabela w §1, którą sam
nazwałeś szkicem. Poprawiłem tabelę. Uzasadnienie ponad samą sprzecznością: pokazanie
klientowi kosztu rundy zmienia rozmowę z „czy strona jest dobra" na „czy warto było
wydać", a produkt stoi na tym pierwszym.

**Data powstania wersji wchodzi** — i to jest do zrobienia u Ciebie, bo `VersionMeta` nie
zna dziś czasu. Pole plus propagacja do `VersionView`. Przy powrocie do starszej wersji
(5.1) „Wersja 2 — wczoraj 14:30" to różnica między wyborem a zgadywaniem. Po Twojej
stronie zostawiam też sam kształt pola.

## 6.3 — tabela była błędna, kod miał rację

Poprawiłem tabelę: `failure {handling, attempts}`, bez `cause`. Nie było tu czego
rozstrzygać — `cause` niesie treść od modelu i z definicji nie nadaje się dla klienta.

## 6.4 — zostaje jak jest

Zgadzam się, że to odnotowanie, nie spór. `409` przy komplecie szkiców i przy istniejącej
wersji jest tego samego rodzaju co `409` przy `frozen`, a powód kosztowy jest twardy.

---

# Ostatnia rozbieżność atrapy — zamknięta

Zrobiłem to, co zostawiłeś mi do decyzji: **atrapa nie tworzy już wersji przy wyborze**.
`ChooseProposalAsync` zapisuje sam kierunek, a wersję 1 buduje `CreateFirstVersionAsync`
— tak jak `ProjectApi`.

Twoja obawa („dołożenie strażnika sprawi, że każdy wybór pod atrapą kończy się kolizją")
była trafna dla samego strażnika, ale nie dla przeniesienia: `Proposals.Choose` woła obie
metody po kolei, więc po przeniesieniu ścieżka działa, a rozbieżność znika. Przeklikane:
wybór → edytor z podglądem, licznik `0 z 15`, bez kolizji; powrót „wstecz" na
`/proposals/{id}` odsyła do edytora i nie zamawia szkiców.

Atrapa dostała też dwie Twoje straże, obie `InvalidOperationException`, bo `Proposals`
rozpoznaje odmowę po tym typie: brak wyboru i „wersja pierwsza generuje się tylko raz".
Cztery nowe testy w `MockProjectApiTests`; trzy z nich były czerwone przed zmianą.

**Ruszyłem jeden Twój test:** `ProposalsPageTests.Klient_z_gotowa_strona_wraca_do_edytora`
stał na komentarzu „atrapa tworzy tu wersję 1". Dopisałem `CreateFirstVersionAsync` —
test przechodzi teraz tę samą drogę co produkcja. Reszta to mechaniczne dopisanie tego
wywołania po `Choose` w moich plikach testowych.

169/169 silnik, 139/139 web.

**Co z tego zostaje dla Ciebie:** tylko data wersji z 6.2 (`VersionMeta` + propagacja)
i flaky `Nieudana_runda_przywraca_workdir…`.

---

# PILNE: silnik nie działa na Windows. Dwie przyczyny, jedna naprawiona

Przemek kazał przejść ścieżkę klienta na **prawdziwym** `claude`. Nie doszło do rundy —
padło na propozycjach. Wygląda na to, że **nikt nigdy nie uruchomił silnika z prawdziwym
CLI**: 175 zielonych testów silnika chodzi po `FakeClaude`, a `Projects:UseMock` omija
silnik w całości.

## A. `Process.Start` nie znajduje `claude` — NAPRAWIONE

`claude` z npm to na Windows trzy pliki: `claude` (skrypt sh), `claude.cmd`, `claude.ps1`.
`Process.Start` z `UseShellExecute = false` **nie stosuje PATHEXT**, więc szukał pliku
dokładnie „claude", trafiał w skrypt sh i kończył:

```
cause=proces nie wystartowal: An error occurred trying to start process 'claude'
```

Dodałem `ClaudeRunner.Znajdz(...)`: rozwija nazwę do pełnej ścieżki po PATH × PATHEXT,
ścieżki podanej wprost nie tyka (atrapa `dotnet` działa jak dotąd), a gdy nic nie znajdzie
oddaje oryginał, żeby komunikat został zrozumiały. Sześć testów, `Znajdz` publiczne —
spójnie z `BuildArguments`. **Ruszyłem Twój plik**, bo bez tego nie ma jak niczego zmierzyć.

## B. Wieloliniowy prompt ginie w `cmd.exe` — NIE naprawiam, to Twój rdzeń

Po poprawce A `claude` **wystartował**, pracował 275 s i zapisał `a.html`, `b.html`,
`c.html`. Zadanie i tak `failed`:

```
cause=brak JSON na stdout (exit 0):
```

Przyczyna, potwierdzona osobnym wywołaniem obok aplikacji: `.cmd` uruchamiany przez
`CreateProcess` idzie przez `cmd.exe`, a `cmd.exe` **urywa polecenie na pierwszym znaku
nowej linii**. `PromptBuilder` składa prompt przez `AppendLine`, więc:

- na stdout wychodzi **proza, nie JSON** — `--output-format json` nigdy nie dociera
- model dostaje **tylko pierwszą linię** instrukcji

Dowód (ten sam zestaw flag, prompt trzyliniowy, `claude.cmd` wołany wprost):

```
stdout: "Utworzyłem ...wielolinia.txt z trzema liniami, bo nie podałeś treści."
        198 bajtów prozy zamiast JSON-a
```

Jednolinijkowy prompt przez `claude.cmd` oddaje JSON poprawnie (`total_cost_usd`
0.0344) — czyli flagi i `.cmd` są w porządku, psuje się **wieloliniowy argument**.

Skutek jest gorszy niż brak raportu: na Windows model pracuje z **innym promptem niż
napisaliśmy** — bez opisu klienta, bez wymogu `<title>`, bez zakazu `data-cmt-id`.
Twoje straże w §2 tego nie wyłapią, bo one czytają JSON, którego nie ma.

**Nie ruszam tego sam**, bo naprawa dotyka rdzenia Twojej warstwy i `FakeClaude`:
prompt musiałby iść **przez stdin** (`claude` czyta stdin — jego własne ostrzeżenie
„no stdin data received in 3s" to potwierdza) zamiast argumentem. To zmiana w
`BuildArguments`, w zamykaniu stdin i w atrapie, która dziś czyta argumenty.
Alternatywa: uruchamiać `node <cli.js>` bezpośrednio, co omija `cmd.exe`, ale zaszywa
szczegół instalacji npm.

## Zmierzone przy okazji — pierwsze realne liczby do §4.2

- **propozycje: 275 s** (jedno wywołanie modelu, `claude-opus-5`)
- koszt jednego krótkiego wywołania z pełnym zestawem flag: **$0,034**
- `ANTHROPIC_API_KEY` nie jest u mnie ustawiony, więc idzie przez subskrypcję i `--bare`
  nie leci; `total_cost_usd` nadal podaje wycenę

Kosztu propozycji **nie mamy**, bo JSON nie wrócił — dostaniemy go dopiero po naprawie B.
275 s to za to twarda liczba i jest istotna dla UI: `Proposals` czeka w pętli, a klient
patrzy w „Przygotowuję trzy propozycje…" prawie pięć minut.
