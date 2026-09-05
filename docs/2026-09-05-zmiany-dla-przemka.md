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

## 8. Twój losowy czerwony test — znaleziony i naprawiony

Pisałem Ci wcześniej, że jeden przebieg `Generator.Web.Tests` dał 90/91 i nie umiałem
tego powtórzyć. Już wiem, co to było, i jest to ładny błąd.

`EditorPageTests.Polling_ustaje_gdy_zadanie_sie_konczy` asercjonował, że `src` iframe'a
zawiera ciąg `"/2"`. A `src` wygląda tak: `/preview/{guid}/1/`. **GUID zaczynający się
od cyfry `2` trafia się raz na szesnaście** — czyli w 6,25% przebiegów asercja spełniała
się natychmiast, jeszcze zanim runda się skończyła, i test czytał licznik odpytań za
wcześnie. Zmierzone na niezmienionym kodzie: 2 pady na 20 przebiegów.

Poprawka to jedna asercja — pełna ścieżka `"/preview/{id}/2/"` zamiast fragmentu `"/2"`,
dokładnie jak w bliźniaczym teście kilka linii wyżej. Po niej 0/20 i 0/15 pod obciążeniem.

Komponent był w porządku — kłamała asercja. To jedyna rzecz, jaką zmieniłem w Twoim
pliku testów; jeśli wolisz to cofnąć, powiedz.

## 9. Dwie zmiany w Twoich komponentach — obie są skutkiem podmiany atrapy

Nie chciałem Ci ich zostawiać jako „znanych problemów", bo to ja je uruchomiłem,
przepinając DI z `MockProjectApi` na prawdziwe `ProjectApi`.

**`App.razor` — wyłączyłem prerender.** `<Routes @rendermode="new InteractiveServerRenderMode(prerender: false)" />`

Prerender uruchamia `OnInitializedAsync` raz na serwerze i drugi raz w obwodzie
interaktywnym. Przy atrapie to drugie wywołanie było niewidoczne. Przy prawdziwym
silniku `Proposals` zamawia w nim **drugi komplet trzech szkiców za około pół dolara**
— przy każdym F5 i przy wejściu wprost na `/proposals/{id}`. Wejście kliknięciem ze
`Start` prerenderu nie ma, więc na najczęstszej ścieżce tego nie widać.

Wybrałem wyłączenie prerenderu zamiast strażnika `RendererInfo.IsInteractive` w każdym
komponencie: **każda strona tej aplikacji robi I/O w inicjalizacji**, więc strażnika
trzeba by pamiętać przy każdej nowej, a zapomniany kosztuje pieniądze. Nie tracimy przy
tym nic — aplikacja jest za linkiem z tokenem, więc prerender nie daje ani SEO, ani
sensownego pierwszego renderu.

Jest na to test HTTP: `GET /proposals/{id}` nie może utworzyć katalogu `proposals/`.
Testy komponentów tego nie złapią, bo bUnit renderuje od razu interaktywnie.

**`Editor.razor` — `Apply` i `Wroc` łapią wyjątki dziedzinowe.** Atrapa nie rzucała
żadnego z czterech (`ProjectFrozenException`, `StaleVersionException`,
`JobRunningException`, `KeyNotFoundException`), a prawdziwe API rzuca je normalnie.
Wyjątek z uchwytu zdarzenia Blazora **kończy obwód** — klient dostaje pasek „połączenie
zerwane" zamiast zdania o tym, co się stało. **Zamrożenie po piętnastej rundzie trafi
w każdego klienta**, więc to nie jest przypadek brzegowy.

Dołożyłem `_kolizja` i `<p role="alert" data-test="kolizja">` obok Twojego `JobStatus`,
w tej samej konwencji co `data-test="zamrozone"`. Zdania są moje i pewnie chcesz je
przepisać — kontrakt oddaje gałąź, nie treść, a treść jest Twoja.

## Stan silnika na teraz

`Generator.Engine.Tests` 166/166, `Generator.Web.Tests` 119/119. Plan B skończony:
aplikacja chodzi po prawdziwym silniku, nie po atrapie. Mock zostaje za
`Projects:UseMock` (domyślnie wyłączony).

Pełny przebieg klienta przez przeglądarkę kosztował **$1,03** (propozycje $0,50,
wersja 1 $0,24, dwie rundy $0,17 i $0,12). Mieści się w widełkach z §4.2 kontraktu —
tym razem ekstrapolacja nie była 4× za wysoka jak w Planie A, więc §4.2 nie wymaga
przestawienia.
Model wersji zna `currentVersion`, `basedOn` i numerację `max+1` — czyli powrót do
starszej wersji nie zepsuje numeracji ani podświetlania, tak jak wymaga Twój §5.1.

---

# Recenzja końcowa — co ruszyłem u Ciebie, 2026-09-05 (wieczór)

Siedem znalezisk z recenzji końcowej. Trzy z nich siedzą w Twoim obszarze i dwa
z nich przepisały Twoje testy, więc nie chcę, żebyś to zobaczył jutro przy `pull`
bez słowa wyjaśnienia.

## A. `MockProjectApi` — atrapa odmawia teraz tak jak prawdziwe API

Atrapa rzucała `ArgumentOutOfRangeException` (rollback do nieistniejącej wersji)
i goły `InvalidOperationException` (frozen) tam, gdzie `ProjectApi` rzuca
`KeyNotFoundException` i `ProjectFrozenException`. `Editor.Kolizja` rozpoznaje odmowę
**po typie**, więc pod atrapą te same kliknięcia **zrywały obwód**, choć pod prawdziwym
API kończą się zdaniem. Nigdy nie rzucała też `JobRunningException` ani
`StaleVersionException` — czyli dwóch z czterech gałęzi nie dało się ani przeklikać,
ani dotknąć testem.

Co się zmieniło:

- `RollbackAsync` → `KeyNotFoundException`; `ApplyCommentsAsync` (frozen) → `ProjectFrozenException`
- `ApplyCommentsAsync` rzuca `StaleVersionException`, gdy `version != CurrentVersion`
  (oraz gdy wersji nie ma wcale) — §5.1
- `Choose`, `Rollback`, `Apply`, `RequestProposals`, `CreateFirstVersion` rzucają
  `JobRunningException`, gdy projekt ma trwające zadanie
- identyfikatory szkiców `p-1/p-2/p-3` → **`a/b/c`**, jak w silniku (`SketchIds`)
  i w białej liście `ProjectApi`. Wybór przeklikany na atrapie był dotąd wyborem,
  który prawdziwe API odrzuca jako nieistniejący.
- `GetProposalsAsync` oddaje **pustą listę, dopóki szkiców nie zamówiono**. Bez tego
  atrapa pokazuje trzy propozycje projektowi, który o nie nigdy nie poprosił, a nowa
  gałąź `Proposals` („pokaż, co jest" kontra „zamów komplet") jest nieosiągalna.

**Rezerwacja projektu w atrapie żyje krócej niż w `JobQueue`**: od wejścia do wyjścia
z metody, bo atrapowe „zadanie" to samo wywołanie. Trzymanie jej dłużej zamykałoby
projekt na zawsze przy `RetryResolvesAfter = null` — czyli w stanie, którego potrzebują
Twoje testy przycisku. Dwuklik i druga karta odtwarzają się poprawnie.

**Czego NIE ruszyłem, świadomie:** `ChooseProposalAsync` w atrapie **nadal tworzy
wersję 1**. Prawdziwe `ProjectApi` już tego nie robi (to osobne zadanie
`CreateFirstVersionAsync`), ale dołożenie tam strażnika `Versions.Count > 0`
z prawdziwego API sprawiłoby, że **każdy** wybór pod atrapą kończy się kolizją zamiast
przejściem do edytora. Przeniesienie tworzenia wersji do `CreateFirstVersionAsync` to
refaktor Twojego pliku dotykający ~15 testów — to jest ostatnia znana rozbieżność atrapy
i zostawiam ją Tobie do decyzji.

## B. Twoje testy — co i dlaczego

- **`MockProjectApiTests`** (4 testy) i **`MockSnapshotTests`** (3 testy): dołożone
  `await api.ChooseProposalAsync(p.Id, "a")` przed rundą. Powód: atrapa egzekwuje teraz
  §5.1, a runda na projekcie bez wersji bieżącej to `StaleVersionException`. To ta sama
  reguła, którą `ProjectApi` ma od Zadania 5 — testy po prostu jej dotąd nie dotykały.
- **`MockProjectApiTests.Wyczerpanie_rund_zamraza_projekt_a_apply_odmawia`**:
  `Assert.ThrowsAsync<InvalidOperationException>` → `ProjectFrozenException`. Typ bazowy
  przechodził też dla wyjątku, który `Editor.Kolizja` odrzuca — czyli test przechodził
  przy zachowaniu, które w przeglądarce zrywa obwód.
- **`MockSnapshotTests`, `RollbackTests`, `HistoriaWersjiTests`, `EditorPageTests`**:
  `ChooseProposalAsync(..., "p-1")` → `"a"`. Zmiana mechaniczna, wynika z A.
- **`ProposalsPageTests.Propozycje_pokazuja_sie_dopiero_po_skonczonym_zadaniu`**:
  `Assert.DoesNotContain(GetProposalsAsync, …)` → `Assert.Equal(1, …Count(…))`. Strona
  pyta teraz o szkice **przed** zamówieniem (sonda „czy jest co pokazać"), więc jedno
  wywołanie jest oczekiwane; drugie — odbiór wyniku — nadal nie ma prawa paść, dopóki
  zadanie trwa. Właściwość testu została, zmieniła się tylko jej miara.

Wszystko zielone: `Generator.Engine.Tests` 169/169, `Generator.Web.Tests` 131/131.
Jeśli któraś z tych zmian Ci nie pasuje — mów, cofnę.

## C. `Proposals.razor` — to samo, co zrobiliśmy w `Editor.razor`

`OnInitializedAsync` wołał `RequestProposalsAsync` bezwarunkowo, a `Choose` nie miał
żadnego `try/catch`. Klient wracający strzałką „wstecz" z edytora płacił ~$0,50 za nowy
komplet szkiców — a `RunProposalsAsync` zaczyna od skasowania `proposals/` i wyzerowania
`ChosenProposal`, więc powrót **wycierał mu wybór**. Druga karta w trakcie generacji
dostawała `JobRunningException` prosto z inicjalizacji i traciła obwód.

Dołożone `_kolizja`, `Kolizja(...)`, `Odswiez(...)` i `<p role="alert" data-test="kolizja">`
— ta sama konwencja co w `Editor`. **Jedna różnica, o której powinieneś wiedzieć:**
`Proposals.Kolizja` to `ex is InvalidOperationException or KeyNotFoundException`, czyli
szerzej niż cztery typy w `Editor`. Tutaj docierają też strażnicy rzucający **gołym**
`InvalidOperationException` (drugi komplet szkiców, druga wersja pierwsza), a trzy typy
dziedzinowe i tak po nim dziedziczą. To ta sama granica, którą `ApiEndpoints.Zamien`
mapuje na 409. Zdania są moje — przepisz je, jeśli chcesz.

Strażnik „nie zamawiaj drugi raz" siedzi w **`ProjectApi.RequestProposalsAsync`**, nie
w komponencie: `POST /api/projects/{id}/proposals` jest w §1, więc komponent nie jest
jedyną drogą. Warunkiem jest **komplet** trzech szkiców, nie „cokolwiek na dysku" —
zadanie, które zapisało 2 z 3, zostawia projekt zdolny do ponownego zamówienia.

## D. `CommentStore.Upsert` → `Uzgodnij` (zmiana nazwy, dotyka silnika)

`Editor.Withdraw` usuwał wycofaną uwagę tylko z pamięci obwodu, a `Upsert` umiał
wyłącznie dopisywać. Uwaga wycofana po nieudanej rundzie zostawała `open`
w `comments.json` **na zawsze**: wracała przy każdym `Reload`, odblokowywała „Popraw to"
i wchodziła do następnej rundy — klient płacił rundę za rzecz, którą sam odwołał.

Przychodząca lista jest teraz **pełną listą uwag `open`** danej wersji; te spoza niej
znikają. `applied`/`rejected` są własnością silnika (§4.4.2) i zostają nietknięte.

**To zmienia semantykę endpointu z §1** — `POST .../comments/apply` teraz kasuje uwagi
`open` nieobecne w ciele żądania, a kontrakt o tym nie mówi. Kontraktu nie ruszałem
sam: to zdanie do dopisania w §1 albo §3.3 i chcę je uzgodnić z Tobą, nie zadekretować.

**Znana luka:** gdy klient wycofa **wszystkie** uwagi, `Editor.Apply` wraca wcześnie
(`DoWyslania().Count == 0`) i `Uzgodnij` nie zostaje wołany w ogóle — plik zachowuje je
do następnego „Popraw to". Domknięcie wymaga endpointu zapisującego samą listę uwag bez
uruchamiania rundy, czyli tej samej decyzji co trwałość szkiców uwag.

## E. Drobne, ale zauważysz

- **`/api/jobs/{id}` wymaga tokenu** (§1 mówił to od początku, trasa jako jedyna go nie
  sprawdzała). `JobQueue` prowadzi mapę `jobId → projectId`. **Pod `Projects:UseMock`
  ta trasa oddaje teraz 404 na wszystko**, bo atrapa nie dotyka `JobQueue` — nasze UI
  tego nie widzi (woła `IProjectApi` w procesie), ale klient HTTP pod atrapą przestał
  działać. Jeśli tego używasz, powiedz — do zamknięcia przez mapę w `IProjectApi`.
- **Domyślny `Projects:Root` to `<ContentRoot>/projekty`**, nie `Path.GetTempPath()`.
  Jest jawny wpis w `appsettings.json` i `src/Generator.Web/projekty/` w `.gitignore`.
  Jeśli miałeś gdzieś projekty w `/tmp/generator-stron`, po tej zmianie aplikacja ich
  nie zobaczy — przenieś albo ustaw `Projects:Root`.
- **`ProjectApi` i `JobQueue` mają logger.** `Job.failure.cause` (§4.3: „szczegół dla
  naszych logów, NIE do UI") ginął — logów nie było w ogóle. Do klienta nadal nie idzie.


## F. Ostatnia zmiana w `Proposals.razor` — powrót do edytora

Recenzja końcowa znalazła ślepy zaułek i był realny: klient, który ma już gotową stronę
i wraca strzałką „wstecz" na `/proposals/{id}`, widział wybór kierunku (szkice nadal leżą
na dysku). Kliknięcie po cichu nadpisywało `ChosenProposal`, `CreateFirstVersionAsync`
odmawiała, a klient zostawał z czerwonym zdaniem i **bez żadnej drogi powrotu** — nie ma
nawigacji w `MainLayout`, nie ma linku na tej stronie. Miał tylko F5.

Teraz strona najpierw pyta `GetAsync` i przy `Versions.Count > 0` przekierowuje do
edytora, nie zamawiając niczego.

**To jest dokładnie ten scenariusz, który atrapa zielenila.** Jej `ChooseProposalAsync`
i `CreateFirstVersionAsync` nie mają straży, więc ten sam przepływ kończył się w edytorze
i test przechodził. Recenzent odtworzył to dwoma jednorazowymi testami: pod wierną atrapą
— czerwone zdanie i brak `/editor/` w URL-u; pod `MockProjectApi` — zielono.

Dołożyłem dwa testy komponentu, oba z Twoim `Szpieg`iem. Drugi (`Niepelny_komplet_...`)
napisałem najpierw źle: nie zasiałem szkiców w atrapie, więc pierwszy odczyt oddawał zero,
oba warunki były fałszywe i test przechodził niezależnie od tego, który z nich jest
w kodzie. Mutacja to pokazała.

Kontrakt: dopisałem §6.4 — `POST .../proposals` odmawia teraz 409 przy komplecie szkiców
i przy projekcie z wersją. Oznaczyłem jako **odnotowane**, nie sporne, bo 409 z powodu
`frozen` na tej trasie i tak wynikał z §4.5.
