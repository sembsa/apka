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
