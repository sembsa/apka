# Co się zmieniło po Twojej stronie — Plan B, stan na 2026-09-05

Piszę to, bo Plan B dotyka rzeczy, które są Twoje: `IProjectApi`, `MockProjectApi`,
`ZmienioneBloki` i ścieżka podglądu. Nic z tego nie wymaga Twojej pracy — ale trzy
punkty zmieniają to, czego możesz się spodziewać po kodzie, więc lepiej, żebyś
wiedział zanim zaczniesz czegoś szukać.

Plan: `docs/superpowers/plans/2026-09-05-api-i-kolejka.md`. Sześć zadań, trzy zrobione.

## 1. Gałąź `retrying` w UI jest osiągalna tylko z mocka

To jest punkt, na którym najłatwiej stracić godzinę.

`GenerationService` zużywa umowną jedną powtórkę (§4.3) **wewnątrz siebie** i zwraca
wyłącznie `halted` — mówi to wprost jego własny nagłówek. Kolejka zadań nie ponawia
niczego, bo druga warstwa powtórek spaliłaby budżet na identyczny wynik. Zadanie stoi
w `running` przez cały czas wewnętrznej powtórki i to jest dokładnie to, czego chce
kontrakt: klient widzi jedno „pracuję nad tym".

Twój `JobStatus.razor` i `MockProjectApi.RetryResolvesAfter` zostają — po to, żeby dało
się tę gałąź przejść jak klient. Ale w produkcji nie zapali się nigdy. Nie szukaj
w silniku ścieżki, która ją produkuje; nie ma jej i nie było.

## 2. `IProjectApi` NIE staje się klientem HTTP

Twój komentarz mówił „MockProjectApi teraz, klient HTTP po Planie B". Zmieniam to,
i uzasadnienie jest z Twojego własnego §1: postęp idzie obwodem SignalR, a `GET /api/jobs/{id}`
zostaje „dla klientów poza naszym UI". Robienie z Blazora klienta HTTP wołającego własny
proces to hop po nic — polling zamiast obwodu, serializacja zamiast wywołania.

Więc: `ProjectApi` implementuje `IProjectApi` **w procesie**, w `src/Generator.Web/Api/`
(folder wyłącznie mój, nie koliduje z Twoimi `Components/`, `Preview/`, `Mock/`).
Endpointy `/api/*` z §1 powstają jako cienka warstwa nad tą samą usługą, dla klientów
spoza naszego UI. Podmiana to nadal jedna linia w DI, tak jak pisałeś.

Jeśli się z tym nie zgadzasz — powiedz, to jest decyzja odwracalna, tylko im później,
tym drożej.

## 3. `IProjectApi` dostanie jedną nową metodę (Zadanie 5)

```csharp
Task<string> CreateFirstVersionAsync(string id);
```

Bo `POST /api/projects/{id}/versions` z §1 („wersja 1 z wybranej propozycji") to
**zadanie**, nie skutek uboczny wyboru: prawdziwa generacja trwa minuty, a wybór
propozycji ma być natychmiastowy. U Ciebie w mocku wersja 1 powstawała przy wyborze.

Implementację w `MockProjectApi` dopiszę sam (zwróci id zadania od razu `succeeded`),
żeby Ci się nie wysypał build. Mówię o tym, bo to zmiana w Twoim pliku.

## 4. `ZmienioneBloki` przeniosłem do silnika

Twój §5.2 mówi wprost: „`changedAnchors[]` liczy ten, kto ma snapshoty — czyli silnik".
Klasa jest teraz w `src/Generator.Engine/Versioning/ZmienioneBloki.cs`, z tą samą nazwą
i tym samym algorytmem (znormalizowany `outerHtml`, AngleSharp, nie regex). Dołożyłem
`PoliczZeSnapshotow(katalogRodzica, katalogWersji)` — silnik ma katalogi, nie stringi.
Testy poszły do `Generator.Engine.Tests`.

W `MockProjectApi.cs` zmieniła się dokładnie jedna linia: dopisany `using`.
`Generator.Web` ma teraz `ProjectReference` do silnika.

**Twoja decyzja, drobna:** `using Generator.Web.Preview;` w `MockProjectApi.cs:4` jest
teraz martwy. Nie usunąłem go, bo druga zmiana w Twoim pliku to konflikt u Ciebie przy
najbliższym `pull`. Usuń przy okazji albo zostaw, obojętne.

## 5. Kontrakt §1 — trzy poprawki, wszystkie w mojej sekcji

- **Ścieżka podglądu zmieniona na Twoją.** Miałem w tabeli `/api/projects/{id}/versions/{n}/preview`,
  a Ty masz działające `/preview/{id}/{n}/{**ścieżka}` ze wstrzykiwaniem skryptu
  i podświetlaniem. Drugi endpoint na to samo to drugie źródło prawdy — wygrywa Twój.
  `previewUrl` z API kończy się ukośnikiem, zgodnie z Twoim §5.4.
- **Dopisane dwa wiersze:** `POST /api/projects/{id}/rollback/{n}` (bo `RollbackAsync`
  było w interfejsie, ale nie miało endpointu) i `GET .../versions/{n}/comments`.
- **Token:** `?token=` na GET, nagłówek `X-Project-Token` na POST, tylko dla `/api/*`.
  `/preview/*` w v1 zostaje bez tokenu — podgląd ładuje przeglądarka w `iframe`, więc
  token wylądowałby w pasku adresu i w `Referer`. Chroni go nieodgadywalny GUID projektu.
  Zapisałem to jako znane ograniczenie, nie przemilczałem.

## 6. Uwaga na Zadanie 6: ścieżki snapshotów się dziś nie zgadzają

`Program.cs` mapuje podgląd na `snapshotRoot/{projectId}/v{version}`, a silnik pisze do
`snapshotRoot/{projectId}/versions/{n:D3}` (`VersionStore.SnapshotPath`). Po podmianie
DI na prawdziwy backend **każdy iframe byłby pusty**, a testy komponentów tego nie widzą.

Zmieniam `MapPreview`, żeby pytał `VersionStore` zamiast sklejać ścieżkę ręcznie, i to
mock dostosuję do silnika, nie odwrotnie. Gdybyś w międzyczasie dotykał `MockSnapshotTests` —
stąd będzie zmiana.

## 7. Mock zostaje

`Projects:UseMock`, domyślnie `false`. Twoje `/dev/wynik-rundy` i praca nad UI bez
zainstalowanego `claude` na tym stoją. Nie kasuję go.

## 8. Drobiazg z Planu A, który mi umknął

Jeden przebieg `Generator.Web.Tests` dał kiedyś 90/91, trzy kolejne 91/91. Nie udało mi
się powtórzyć ani wskazać testu. Jeśli zobaczysz u siebie losowy czerwony — to nie Ty.
Teraz suita ma 84 testy (7 przeszło do silnika), więc liczby będą inne.

## Stan silnika na teraz

`Generator.Engine.Tests` 145/145, `Generator.Web.Tests` 84/84.
Model wersji zna `currentVersion`, `basedOn` i numerację `max+1` — czyli powrót do
starszej wersji nie zepsuje numeracji ani podświetlania, tak jak wymaga Twój §5.1.
