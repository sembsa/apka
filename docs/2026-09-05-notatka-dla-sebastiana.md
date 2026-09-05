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
