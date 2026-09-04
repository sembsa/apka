# Frontend — pętla komentarzy (Plan C z 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Blazor Server, w którym klient widzi swoją stronę w iframe, klika w blok, pisze „telefon za mało widoczny", odpala rundę i widzi wynik — łącznie z uczciwym zachowaniem, gdy runda się nie uda.

**Architecture:** `Generator.Web` to aplikacja Blazor Server. Podgląd wersji serwuje własny endpoint `/_preview/...`, który **dokleja skrypt zbierający kliknięcia dopiero przy serwowaniu** — snapshot na dysku i ZIP klienta zostają czyste. Kliknięcie w iframe wraca do C# przez `postMessage` → shim JS → `[JSInvokable]`, nigdy przez bezpośredni dostęp do DOM iframe'a. Backend (Plan B) nie musi istnieć: wszystko za `IGeneratorApi`, a testy podstawiają `FakeGeneratorApi` z nagranymi odpowiedziami — ten sam wzorzec, co `Generator.FakeClaude` w Planie A.

**Tech Stack:** .NET 10 (SDK 10.0.301 na maszynie Przemka, 10.0.101 u Sebastiana — patrz Global Constraints), C#, Blazor Server (`InteractiveServer`), xUnit + **bUnit 2.9.0** do testów komponentów, `node:test` (wbudowany w Node 24) do testów skryptu w iframe. Zero npm, zero bundlera.

**Spec:**
- [`docs/plan/2026-09-04-generator-stron-plan.md`](../../plan/2026-09-04-generator-stron-plan.md) — sekcje 3, 4, 7
- [`docs/api-contract.md`](../../api-contract.md) — §1 (zasoby, których konsumentem jest ten plan), §3 (komentarz), §4 (liczniki i `failed`)
- [`docs/superpowers/plans/2026-09-04-silnik-generatora.md`](2026-09-04-silnik-generatora.md) — Plan A, silnik

**Zakres:** Plan C to **pętla komentarzy — rdzeń produktu** (plan, sekcja 4): podgląd, przypinanie, runda, UX przy `failed`, liczniki. **Kreator wejścia i wybór z 3 propozycji (plan, sekcje 2 i 3) to Plan D** — są niezależne, mniej ryzykowne i nie blokują pętli. Sekcja „Po wykonaniu planu" w Planie A wymienia tylko B i C; ta linia jest tam do uzupełnienia.

## Global Constraints

Dotyczą każdego zadania w tym planie.

- **Brak `global.json` z zapiętym patchem SDK.** Jeśli powstanie, wyłącznie z `"rollForward": "latestPatch"`. Na dwóch maszynach są dwa różne patche .NET 10 (10.0.301 i 10.0.101) — pin złamie build jednej ze stron.
- **bUnit 2.x, nie 1.x.** API różni się od wszystkich tutoriali: klasa bazowa testu to **`BunitContext`** (nie `TestContext`), renderowanie to **`Render<T>()`** (nie `RenderComponent<T>()`). Sprawdzone na `bunit 2.9.0` + `net10.0`.
- **Zero npm i zero bundlera.** Skrypt iframe'a to jeden plik `.js` serwowany statycznie; jego testy to `node --test` (wbudowane w Node 24), a DOM w testach zastępują ręczne obiekty-atrapy.
- **NuGet: tylko `bunit` i `xunit`, tylko w projekcie testowym.** `Generator.Web` bez pakietów zewnętrznych (analogicznie do zasady Planu A).
- **Skrypt zbierający kliknięcia NIGDY nie trafia do snapshotu.** Doklejany jest w locie przez `/_preview`. Klient dostaje w ZIP-ie stronę bez naszego kodu (plan, decyzja 4).
- **Komunikacja z iframe wyłącznie przez `postMessage`,** z weryfikacją `event.origin` i kształtu wiadomości. Nawet gdy podgląd jest same-origin — treść strony jest generowana przez model na podstawie danych klienta i traktujemy ją jak niezaufaną.
- **`cause` z `Job.failure` nie może dotrzeć do UI** (kontrakt §1). W tym planie typ `JobFailure` **nie ma pola `cause`** — nie da się go pokazać, bo go nie ma.
- **Status komentarza ustawia silnik**, frontend tworzy tylko `open` i umie wycofać (kontrakt §3.3). Żaden komponent nie zapisuje `applied`.
- **Nieudana runda nie rusza: podglądu, komentarzy ani licznika rund** (kontrakt §4.4). Ma to jeden test z trzema asercjami — Task 6.

### Decyzje podjęte w tym planie (nie ma ich w specyfikacji)

1. **`Comment.id` powstaje w obwodzie Blazora, nie w JS.** Kontrakt §3.2 mówi „GUID nadany w przeglądarce", ale ta linia powstała, gdy żywy był jeszcze TypeScript/Node. W Blazor Server kod C# komponentu wykonuje się **na serwerze**, więc dosłowne „w przeglądarce" jest przy tym stacku nieprawdą. Sens pola zostaje: id powstaje **raz, w chwili dodania komentarza**, i jest ponawiane bez zmiany — a stan obwodu jest per karta przeglądarki, więc idempotencja przy podwójnym „Popraw to" działa tak samo. **Kontrakt §3.2 wymaga korekty tego zdania** (osobny commit, nie ten plan).
2. **`viewport` bierzemy z przełącznika hosta, nie z iframe'a.** Payload `postMessage` to wyłącznie `{ anchor }`. Skrypt nie musi znać szerokości — to host wie, czy klient patrzy na wariant `desktop` czy `mobile`, bo host tym przełącznikiem sterował. Pole z §3.2 zostaje wypełnione, tylko nie przez iframe.
3. **Treść komentarza wpisuje się w panelu Blazora, nie w iframe.** Iframe raportuje wyłącznie *co kliknięto*. Dzięki temu przez granicę serializacji przechodzi jeden krótki string, a pole tekstowe jest nasze — stylowane, walidowane i dostępne z klawiatury.
4. **Podgląd serwuje `Generator.Web`, nie API.** Kontrakt §1 wymienia `GET /api/projects/{id}/versions/{n}/preview` po stronie Sebastiana; przy Blazor Server aplikacja webowa *jest* hostem, więc podgląd i wstrzyknięcie skryptu siedzą tutaj. Endpoint z §1 zostaje dla klientów poza naszym UI.

---

## Struktura plików

| Plik | Odpowiedzialność |
|---|---|
| `global.json` | **nie powstaje** — patrz Global Constraints |
| `src/Generator.Web/Program.cs` | host Blazora, rejestracja `IGeneratorApi`, mapowanie `/_preview` |
| `src/Generator.Web/Api/IGeneratorApi.cs` | granica do Planu B: projekt, runda, zadanie |
| `src/Generator.Web/Api/ApiModel.cs` | `ProjectView`, `VersionView`, `JobView`, `JobFailure`, `CommentDraft` |
| `src/Generator.Web/Api/FakeGeneratorApi.cs` | nagrane odpowiedzi: sukces, `retrying`, `halted`, `orphanedAnchors`, `frozen` |
| `src/Generator.Web/Preview/PreviewInjector.cs` | doklejenie `<script>` do HTML-a przed `</body>` |
| `src/Generator.Web/Preview/PreviewEndpoint.cs` | serwowanie plików snapshotu + wstrzyknięcie |
| `src/Generator.Web/wwwroot/js/cmt-picker.js` | w iframe: podświetlanie bloku, `resolveAnchor`, `postMessage` |
| `src/Generator.Web/wwwroot/js/cmt-host.js` | u hosta: nasłuch `message`, walidacja, `DotNet.invokeMethodAsync` |
| `src/Generator.Web/Components/CommentBridge.cs` | `[JSInvokable]`, `DotNetObjectReference`, nadanie `Comment.id` |
| `src/Generator.Web/Components/PreviewPane.razor` | iframe, przełącznik `desktop`/`mobile`, tryb komentowania |
| `src/Generator.Web/Components/CommentPanel.razor` | lista komentarzy, pole tekstowe, wycofanie, „Popraw to" |
| `src/Generator.Web/Components/RoundStatus.razor` | postęp zadania, gałęzie `retrying`/`halted`, liczniki, `frozen` |
| `tests/Generator.Web.Tests/*.cs` | testy komponentów (bUnit) i klas czystych (xUnit) |
| `tests/js/cmt-picker.test.mjs` | testy skryptu iframe'a (`node --test`) |

---

### Task 1: Solucja, projekt Blazor Server i uprząż testowa

**Files:**
- Create: `Generator.sln`, `src/Generator.Web/*` (szablon), `tests/Generator.Web.Tests/Generator.Web.Tests.csproj`
- Create: `tests/Generator.Web.Tests/HarnessTests.cs`
- Create: `Directory.Build.props`

**Interfaces:**
- Produces: solucja z projektem webowym i testowym; potwierdzona uprząż bUnit 2.x, na której stoją Taski 5–6.

- [ ] **Step 1: Utwórz solucję i projekty**

```bash
cd <repo>
dotnet new sln -n Generator
dotnet new blazor -o src/Generator.Web --interactivity Server --framework net10.0 --empty
dotnet new xunit -o tests/Generator.Web.Tests --framework net10.0
dotnet sln add src/Generator.Web tests/Generator.Web.Tests
dotnet add tests/Generator.Web.Tests reference src/Generator.Web
dotnet add tests/Generator.Web.Tests package bunit --version 2.9.0
```

- [ ] **Step 2: Dodaj `Directory.Build.props`**

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <!-- TreatWarningsAsErrors tylko w projekcie webowym. W testowym ostrzezenia
       analizatorow xUnit blokowalyby przebieg bez zysku (tak samo jak w Planie A). -->
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Generator.Web'">
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Napisz failujący test uprzęży**

`tests/Generator.Web.Tests/HarnessTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components;
using Xunit;

namespace Generator.Web.Tests;

// Sprawdza wylacznie to, ze bUnit 2.x renderuje NASZ komponent w tym projekcie.
// Bez tego testy z Taskow 5-6 failowalyby z powodu uprzezy, nie logiki.
public class HarnessTests : BunitContext
{
    [Fact]
    public void Uprzaz_renderuje_komponent()
    {
        var cut = Render<LicznikRund>(p => p
            .Add(x => x.Uzyte, 12)
            .Add(x => x.Limit, 15));

        cut.Find("p.rundy").MarkupMatches("""<p class="rundy" diff:ignoreAttributes>zostały 3 poprawki</p>""");
    }
}
```

- [ ] **Step 4: Uruchom test i potwierdź, że failuje**

Run: `dotnet test tests/Generator.Web.Tests --filter Uprzaz_renderuje_komponent`
Expected: FAIL — kompilacja, `LicznikRund` nie istnieje (`CS0246`).

- [ ] **Step 5: Napisz minimalny komponent**

`src/Generator.Web/Components/LicznikRund.razor`:

```razor
@* Najmniejszy komponent produkcyjny: liczba rund widoczna dla klienta (kontrakt 4.1).
   Budzet w USD tu nie wchodzi i nie wejdzie - klient go nie widzi. *@
<p class="rundy">zostały @(Limit - Uzyte) poprawki</p>

@code {
    [Parameter] public int Uzyte { get; set; }
    [Parameter] public int Limit { get; set; }
}
```

- [ ] **Step 6: Uruchom test i potwierdź, że przechodzi**

Run: `dotnet test tests/Generator.Web.Tests --filter Uprzaz_renderuje_komponent`
Expected: PASS, 1 test.

- [ ] **Step 7: Commit**

```bash
git add Generator.sln Directory.Build.props src/Generator.Web tests/Generator.Web.Tests
git commit -m "feat(web): solucja Blazor Server i uprzaz bUnit 2.x"
```

---

### Task 2: `IGeneratorApi` i `FakeGeneratorApi`

To jest granica, dzięki której Plan C nie czeka na Plan B. Nagrane odpowiedzi pokrywają
przypadki, na których wywraca się UI: `retrying`, `halted`, `orphanedAnchors`, `frozen`.

**Files:**
- Create: `src/Generator.Web/Api/ApiModel.cs`
- Create: `src/Generator.Web/Api/IGeneratorApi.cs`
- Create: `src/Generator.Web/Api/FakeGeneratorApi.cs`
- Test: `tests/Generator.Web.Tests/FakeGeneratorApiTests.cs`

**Interfaces:**
- Consumes: nic (pierwsza warstwa domenowa).
- Produces:
  - `record CommentDraft(string Id, string? Anchor, string Text, string Viewport, DateTimeOffset CreatedAt)` — kształt z kontraktu §3.2
  - `enum JobState { Queued, Running, Succeeded, Failed }`
  - `enum FailureHandling { Retrying, Halted }`
  - `record JobFailure(FailureHandling Handling, int Attempts)` — **bez `cause`**
  - `record JobView(string Id, JobState State, JobFailure? Failure)`
  - `record VersionView(int Number, DateTimeOffset CreatedAt, IReadOnlyList<string> OrphanedAnchors)`
  - `record ProjectView(string Id, bool Frozen, int RoundsUsed, int RoundsLimit, int CurrentVersion, IReadOnlyList<VersionView> Versions)`
  - `interface IGeneratorApi` z `GetProjectAsync`, `StartRoundAsync`, `GetJobAsync`
  - `class FakeGeneratorApi : IGeneratorApi` z `static FakeGeneratorApi Sukces()`, `Retrying()`, `Halted()`, `ZOsieroconymi(params string[])`, `Zamrozony()`

- [ ] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/FakeGeneratorApiTests.cs`:

```csharp
using Generator.Web.Api;
using Xunit;

namespace Generator.Web.Tests;

public class FakeGeneratorApiTests
{
    private static readonly CommentDraft K =
        new("k-1", "hero", "telefon za mało widoczny", "mobile", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Sukces_konczy_zadanie_i_podbija_licznik_rund()
    {
        var api = FakeGeneratorApi.Sukces();
        var jobId = await api.StartRoundAsync("p-1", 3, [K]);
        var job = await api.GetJobAsync(jobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Null(job.Failure);
        Assert.Equal(1, (await api.GetProjectAsync("p-1")).RoundsUsed);
    }

    [Fact]
    public async Task Halted_nie_podbija_licznika_rund()
    {
        // kontrakt 4.1: awaria u nas nie zabiera klientowi poprawki
        var api = FakeGeneratorApi.Halted();
        var jobId = await api.StartRoundAsync("p-1", 3, [K]);
        var job = await api.GetJobAsync(jobId);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(FailureHandling.Halted, job.Failure!.Handling);
        Assert.Equal(0, (await api.GetProjectAsync("p-1")).RoundsUsed);
    }

    [Fact]
    public async Task Retrying_raportuje_liczbe_prob()
    {
        var api = FakeGeneratorApi.Retrying();
        var job = await api.GetJobAsync(await api.StartRoundAsync("p-1", 3, [K]));

        Assert.Equal(FailureHandling.Retrying, job.Failure!.Handling);
        Assert.Equal(1, job.Failure.Attempts);
    }

    [Fact]
    public async Task Osierocone_anchory_wracaja_przy_wersji()
    {
        // kontrakt 3.4: wersja jest dostarczana, nie odrzucana
        var api = FakeGeneratorApi.ZOsieroconymi("cennik");
        var jobId = await api.StartRoundAsync("p-1", 3, [K]);

        Assert.Equal(JobState.Succeeded, (await api.GetJobAsync(jobId)).State);
        var p = await api.GetProjectAsync("p-1");
        Assert.Equal(["cennik"], p.Versions[^1].OrphanedAnchors);
    }

    [Fact]
    public async Task Zamrozony_projekt_odrzuca_runde()
    {
        // kontrakt 4.5: 409, komentarze wolno dodawac, stosowac nie
        var api = FakeGeneratorApi.Zamrozony();
        Assert.True((await api.GetProjectAsync("p-1")).Frozen);
        await Assert.ThrowsAsync<ProjectFrozenException>(() => api.StartRoundAsync("p-1", 3, [K]));
    }
}
```

- [ ] **Step 2: Uruchom testy i potwierdź, że failują**

Run: `dotnet test tests/Generator.Web.Tests --filter FakeGeneratorApiTests`
Expected: FAIL — kompilacja, `Generator.Web.Api` nie istnieje.

- [ ] **Step 3: Napisz model**

`src/Generator.Web/Api/ApiModel.cs`:

```csharp
namespace Generator.Web.Api;

/// <summary>Kształt z kontraktu 3.2. Id nadaje obwod przy dodaniu komentarza (decyzja 1 planu).</summary>
public record CommentDraft(
    string Id,
    string? Anchor,          // null = komentarz globalny (3.2), bez osobnego typu
    string Text,
    string Viewport,         // "desktop" | "mobile", z przelacznika hosta (decyzja 2 planu)
    DateTimeOffset CreatedAt);

public enum JobState { Queued, Running, Succeeded, Failed }

/// <summary>Kontrakt 4.3 — galaz, w ktora idzie UI. Nie diagnoza.</summary>
public enum FailureHandling { Retrying, Halted }

/// <summary>
/// Swiadomie BEZ pola cause: kontrakt §1 zabrania pokazywania go klientowi,
/// a czego nie ma w typie, tego sie nie pokaze przez pomylke.
/// </summary>
public record JobFailure(FailureHandling Handling, int Attempts);

public record JobView(string Id, JobState State, JobFailure? Failure);

public record VersionView(int Number, DateTimeOffset CreatedAt, IReadOnlyList<string> OrphanedAnchors);

public record ProjectView(
    string Id,
    bool Frozen,
    int RoundsUsed,
    int RoundsLimit,
    int CurrentVersion,
    IReadOnlyList<VersionView> Versions);

public sealed class ProjectFrozenException(string projectId)
    : Exception($"Projekt {projectId} jest zamrozony (kontrakt 4.5)");
```

- [ ] **Step 4: Napisz interfejs**

`src/Generator.Web/Api/IGeneratorApi.cs`:

```csharp
namespace Generator.Web.Api;

public interface IGeneratorApi
{
    Task<ProjectView> GetProjectAsync(string projectId);

    /// <summary>Kontrakt §1: 202 + jobId. Rzuca ProjectFrozenException przy 409 (4.5).</summary>
    Task<string> StartRoundAsync(string projectId, int version, IReadOnlyList<CommentDraft> comments);

    Task<JobView> GetJobAsync(string jobId);
}
```

- [ ] **Step 5: Napisz atrapę**

`src/Generator.Web/Api/FakeGeneratorApi.cs`:

```csharp
namespace Generator.Web.Api;

/// <summary>
/// Nagrane odpowiedzi zamiast Planu B. Fabryki nazywaja przypadek, ktory odtwarzaja,
/// zeby test czytalo sie jako zdanie: "Halted nie podbija licznika rund".
/// </summary>
public sealed class FakeGeneratorApi : IGeneratorApi
{
    private readonly JobState _state;
    private readonly JobFailure? _failure;
    private readonly IReadOnlyList<string> _orphaned;
    private ProjectView _project;

    private FakeGeneratorApi(JobState state, JobFailure? failure, IReadOnlyList<string> orphaned, bool frozen)
    {
        _state = state;
        _failure = failure;
        _orphaned = orphaned;
        _project = new ProjectView("p-1", frozen, RoundsUsed: 0, RoundsLimit: 15, CurrentVersion: 3,
            Versions: [new VersionView(3, DateTimeOffset.UnixEpoch, [])]);
    }

    public static FakeGeneratorApi Sukces() => new(JobState.Succeeded, null, [], false);

    public static FakeGeneratorApi Retrying() =>
        new(JobState.Failed, new JobFailure(FailureHandling.Retrying, Attempts: 1), [], false);

    public static FakeGeneratorApi Halted() =>
        new(JobState.Failed, new JobFailure(FailureHandling.Halted, Attempts: 1), [], false);

    public static FakeGeneratorApi ZOsieroconymi(params string[] anchors) =>
        new(JobState.Succeeded, null, anchors, false);

    public static FakeGeneratorApi Zamrozony() => new(JobState.Succeeded, null, [], frozen: true);

    public IReadOnlyList<CommentDraft> OstatnieKomentarze { get; private set; } = [];

    public Task<ProjectView> GetProjectAsync(string projectId) => Task.FromResult(_project);

    public Task<string> StartRoundAsync(string projectId, int version, IReadOnlyList<CommentDraft> comments)
    {
        if (_project.Frozen) throw new ProjectFrozenException(projectId);
        OstatnieKomentarze = comments;

        if (_state == JobState.Succeeded)
        {
            var nowa = new VersionView(_project.CurrentVersion + 1, DateTimeOffset.UnixEpoch, _orphaned);
            _project = _project with
            {
                RoundsUsed = _project.RoundsUsed + 1,
                CurrentVersion = nowa.Number,
                Versions = [.. _project.Versions, nowa],
            };
        }
        // Nieudana runda nie rusza licznika ani wersji (kontrakt 4.1 i 4.4).
        return Task.FromResult("j-1");
    }

    public Task<JobView> GetJobAsync(string jobId) => Task.FromResult(new JobView(jobId, _state, _failure));
}
```

- [ ] **Step 6: Uruchom testy i potwierdź, że przechodzą**

Run: `dotnet test tests/Generator.Web.Tests --filter FakeGeneratorApiTests`
Expected: PASS, 5 testów.

- [ ] **Step 7: Commit**

```bash
git add src/Generator.Web/Api tests/Generator.Web.Tests/FakeGeneratorApiTests.cs
git commit -m "feat(web): IGeneratorApi i atrapa z nagranymi przypadkami failed"
```

---

### Task 3: `PreviewInjector` i endpoint podglądu

**Files:**
- Create: `src/Generator.Web/Preview/PreviewInjector.cs`
- Create: `src/Generator.Web/Preview/PreviewEndpoint.cs`
- Modify: `src/Generator.Web/Program.cs`
- Test: `tests/Generator.Web.Tests/PreviewInjectorTests.cs`

**Interfaces:**
- Consumes: nic.
- Produces:
  - `static class PreviewInjector` z `static string Inject(string html, string scriptUrl)`
  - `static class PreviewEndpoint` z `static void MapPreview(this WebApplication app, Func<string,int,string> snapshotDir)`

- [ ] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/PreviewInjectorTests.cs`:

```csharp
using Generator.Web.Preview;
using Xunit;

namespace Generator.Web.Tests;

public class PreviewInjectorTests
{
    private const string Url = "/js/cmt-picker.js";

    [Fact]
    public void Wstrzykuje_przed_zamknieciem_body()
    {
        var html = "<html><body><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>";
        var wynik = PreviewInjector.Inject(html, Url);

        Assert.Contains($"<script src=\"{Url}\"></script></body>", wynik);
        Assert.Contains("data-cmt-id=\"hero\"", wynik);   // tresc klienta nietknieta
    }

    [Fact]
    public void Bez_body_doklejamy_na_koniec()
    {
        // Model moze wyprodukowac HTML bez </body>. Bramka twarda z 5.2 tego nie zlapie,
        // bo wymaga tylko <html i </html>. Podglad musi dzialac tak samo.
        var wynik = PreviewInjector.Inject("<html><p>bez body</p></html>", Url);
        Assert.EndsWith($"<script src=\"{Url}\"></script>", wynik);
    }

    [Fact]
    public void Wstrzykuje_dokladnie_raz_przy_wielu_body()
    {
        var html = "<html><body>a</body><body>b</body></html>";
        var wynik = PreviewInjector.Inject(html, Url);
        Assert.Equal(1, Wystapienia(wynik, "cmt-picker.js"));
    }

    [Fact]
    public void Nie_modyfikuje_pliku_ktory_nie_jest_html()
    {
        const string css = "body { color: #222 }";
        Assert.Equal(css, PreviewInjector.Inject(css, Url));
    }

    private static int Wystapienia(string s, string igla)
    {
        var n = 0;
        for (var i = s.IndexOf(igla, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(igla, i + igla.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
```

- [ ] **Step 2: Uruchom testy i potwierdź, że failują**

Run: `dotnet test tests/Generator.Web.Tests --filter PreviewInjectorTests`
Expected: FAIL — `PreviewInjector` nie istnieje.

- [ ] **Step 3: Napisz injector**

`src/Generator.Web/Preview/PreviewInjector.cs`:

```csharp
namespace Generator.Web.Preview;

/// <summary>
/// Dokleja skrypt zbierajacy klikniecia PRZY SERWOWANIU podgladu.
/// Snapshot na dysku i ZIP klienta zostaja czyste (plan, decyzja 4).
/// </summary>
public static class PreviewInjector
{
    public static string Inject(string html, string scriptUrl)
    {
        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase)) return html;

        var tag = $"<script src=\"{scriptUrl}\"></script>";
        var i = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? html.Insert(i, tag) : html + tag;
    }
}
```

- [ ] **Step 4: Uruchom testy i potwierdź, że przechodzą**

Run: `dotnet test tests/Generator.Web.Tests --filter PreviewInjectorTests`
Expected: PASS, 4 testy.

- [ ] **Step 5: Zamapuj endpoint podglądu**

`src/Generator.Web/Preview/PreviewEndpoint.cs`:

```csharp
namespace Generator.Web.Preview;

public static class PreviewEndpoint
{
    /// <param name="snapshotDir">(projectId, numerWersji) -> katalog snapshotu na dysku</param>
    public static void MapPreview(this WebApplication app, Func<string, int, string> snapshotDir)
    {
        app.MapGet("/_preview/{projectId}/{version:int}/{**sciezka}",
            async (string projectId, int version, string? sciezka, HttpContext ctx) =>
        {
            var katalog = Path.GetFullPath(snapshotDir(projectId, version));
            var plik = Path.GetFullPath(Path.Combine(katalog, string.IsNullOrEmpty(sciezka) ? "index.html" : sciezka));

            // Wyjscie poza katalog snapshotu: sciezka klienta nie moze czytac naszego dysku.
            if (!plik.StartsWith(katalog + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(plik))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (plik.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(
                    PreviewInjector.Inject(await File.ReadAllTextAsync(plik), "/js/cmt-picker.js"));
                return;
            }

            ctx.Response.ContentType = plik.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                ? "text/css" : "application/octet-stream";
            await ctx.Response.SendFileAsync(plik);
        });
    }
}
```

W `src/Generator.Web/Program.cs` przed `app.Run();` dodaj:

```csharp
app.MapPreview((projectId, version) =>
    Path.Combine(builder.Configuration["Projects:Root"] ?? Path.GetTempPath(), projectId, $"v{version}"));
```

- [ ] **Step 6: Zbuduj i potwierdź, że projekt się kompiluje**

Run: `dotnet build src/Generator.Web`
Expected: Build succeeded, 0 Warning(s) (`TreatWarningsAsErrors`).

- [ ] **Step 7: Commit**

```bash
git add src/Generator.Web/Preview src/Generator.Web/Program.cs tests/Generator.Web.Tests/PreviewInjectorTests.cs
git commit -m "feat(web): podglad snapshotu ze wstrzykiwanym skryptem, snapshot nietkniety"
```

---

### Task 4: `cmt-picker.js` — rozpoznanie klikniętego bloku

**Files:**
- Create: `src/Generator.Web/wwwroot/js/cmt-picker.js`
- Test: `tests/js/cmt-picker.test.mjs`

**Interfaces:**
- Consumes: nic.
- Produces: eksporty `resolveAnchor(el)` i `buildMessage(anchor)`; skrypt wysyła `{ type: "cmt:pick", anchor }`.

- [ ] **Step 1: Napisz failujące testy**

`tests/js/cmt-picker.test.mjs`:

```javascript
import { test } from "node:test";
import assert from "node:assert/strict";
import { resolveAnchor, buildMessage } from "../../src/Generator.Web/wwwroot/js/cmt-picker.js";

// DOM zastepujemy recznymi atrapami - zero jsdom, zero npm.
const node = (id, parent = null) => ({
  getAttribute: (a) => (a === "data-cmt-id" ? id : null),
  parentElement: parent,
});

test("bierze najblizszy anchor w gore drzewa", () => {
  const sekcja = node("oferta-strzyzenie");
  const klik = node(null, node(null, sekcja));
  assert.equal(resolveAnchor(klik), "oferta-strzyzenie");
});

test("zagniezdzony anchor wygrywa z rodzicem", () => {
  const wewnetrzny = node("cennik", node("hero"));
  assert.equal(resolveAnchor(wewnetrzny), "cennik");
});

test("brak anchora to komentarz globalny", () => {
  assert.equal(resolveAnchor(node(null, node(null, null))), null);
});

test("id niezgodne z formatem 3.1 traktujemy jak brak", () => {
  // Regex silnika (Plan A, decyzja 3) odrzuca takie id i liczy jako brakujace.
  // Frontend musi odrzucac je tak samo, inaczej przypielibysmy komentarz do id,
  // ktorego walidacja i tak nie uzna.
  assert.equal(resolveAnchor(node("Oferta_1")), null);
});

test("wiadomosc ma typ i anchor, nic wiecej", () => {
  // viewport NIE idzie z iframe - host wie z przelacznika (decyzja 2 planu)
  assert.deepEqual(buildMessage("hero"), { type: "cmt:pick", anchor: "hero" });
  assert.deepEqual(buildMessage(null), { type: "cmt:pick", anchor: null });
});
```

- [ ] **Step 2: Uruchom testy i potwierdź, że failują**

Run: `node --test tests/js/cmt-picker.test.mjs`
Expected: FAIL — `Cannot find module .../cmt-picker.js`

- [ ] **Step 3: Napisz skrypt**

`src/Generator.Web/wwwroot/js/cmt-picker.js`:

```javascript
// Wstrzykiwany do podgladu przez /_preview (nigdy do snapshotu klienta).
// Zadanie: powiedziec hostowi, w KTORY blok kliknieto. Nic wiecej - tresc
// komentarza wpisuje sie w panelu Blazora (decyzja 3 planu).

const FORMAT = /^[a-z][a-z0-9-]{0,39}$/;   // kontrakt 3.1, ten sam format co regex silnika

export function resolveAnchor(el) {
  for (let n = el; n; n = n.parentElement) {
    const id = n.getAttribute?.("data-cmt-id");
    if (id && FORMAT.test(id)) return id;
  }
  return null;
}

export function buildMessage(anchor) {
  return { type: "cmt:pick", anchor };
}

// Ponizsze uruchamia sie tylko w przegladarce; przy imporcie w node:test
// `document` nie istnieje, wiec blok jest pomijany.
if (typeof document !== "undefined") {
  let tryb = false;

  window.addEventListener("message", (e) => {
    if (e.data?.type === "cmt:mode") {
      tryb = !!e.data.on;
      document.body.classList.toggle("cmt-tryb", tryb);
    }
  });

  document.addEventListener("click", (e) => {
    if (!tryb) return;
    e.preventDefault();
    e.stopPropagation();
    parent.postMessage(buildMessage(resolveAnchor(e.target)), "*");
  }, true);
}
```

- [ ] **Step 4: Uruchom testy i potwierdź, że przechodzą**

Run: `node --test tests/js/cmt-picker.test.mjs`
Expected: `pass 5`, `fail 0`

- [ ] **Step 5: Commit**

```bash
git add src/Generator.Web/wwwroot/js/cmt-picker.js tests/js/cmt-picker.test.mjs
git commit -m "feat(web): skrypt iframe rozpoznaje kliknięty blok, testy na node:test"
```

---

### Task 5: `CommentBridge` i `CommentPanel`

**Files:**
- Create: `src/Generator.Web/wwwroot/js/cmt-host.js`
- Create: `src/Generator.Web/Components/CommentBridge.cs`
- Create: `src/Generator.Web/Components/CommentPanel.razor`
- Test: `tests/Generator.Web.Tests/CommentPanelTests.cs`

**Interfaces:**
- Consumes: `CommentDraft` (Task 2), wiadomość `cmt:pick` (Task 4).
- Produces:
  - `sealed class CommentBridge : IDisposable` z `[JSInvokable] Task OnPick(string? anchor)` i `event Action? Changed`
  - `CommentPanel` z parametrami `Viewport` (string) i `OnApply` (`EventCallback<IReadOnlyList<CommentDraft>>`)
  - `CommentBridge.Dodaj(string? anchor, string text, string viewport)`, `.Wycofaj(string id)`, `.Komentarze`

- [ ] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/CommentPanelTests.cs`:

```csharp
using Bunit;
using Generator.Web.Api;
using Generator.Web.Components;
using Xunit;

namespace Generator.Web.Tests;

public class CommentPanelTests : BunitContext
{
    [Fact]
    public void Komentarz_dostaje_id_raz_i_nie_zmienia_go()
    {
        // kontrakt 3.2: id stabilne od chwili dodania - na tym stoi idempotencja ponowienia
        var b = new CommentBridge();
        b.Dodaj("hero", "telefon za mało widoczny", "mobile");
        var pierwsze = b.Komentarze[0].Id;

        b.Dodaj("cennik", "za drogo wygląda", "desktop");

        Assert.Equal(pierwsze, b.Komentarze[0].Id);
        Assert.NotEqual(b.Komentarze[0].Id, b.Komentarze[1].Id);
    }

    [Fact]
    public void Kolejnosc_to_kolejnosc_dodawania()
    {
        // kontrakt 3.2: sprzeczne komentarze rozstrzyga ostatni, wiec kolejnosc jest znaczaca
        var b = new CommentBridge();
        b.Dodaj(null, "cennik nad opiniami", "desktop");
        b.Dodaj(null, "opinie nad cennikiem", "desktop");

        Assert.Equal(["cennik nad opiniami", "opinie nad cennikiem"], b.Komentarze.Select(k => k.Text));
    }

    [Fact]
    public void Klik_bez_anchora_to_komentarz_globalny()
    {
        var b = new CommentBridge();
        b.Dodaj(null, "całość za ciemna", "desktop");

        Assert.Null(b.Komentarze[0].Anchor);
    }

    [Fact]
    public void Viewport_bierze_sie_z_przelacznika_hosta()
    {
        // decyzja 2 planu: iframe nie raportuje szerokosci, host ja zna
        var b = new CommentBridge();
        b.Dodaj("hero", "telefon za mało widoczny", "mobile");

        Assert.Equal("mobile", b.Komentarze[0].Viewport);
    }

    [Fact]
    public void Wycofanie_usuwa_komentarz()
    {
        var b = new CommentBridge();
        b.Dodaj("hero", "do wycofania", "desktop");
        b.Wycofaj(b.Komentarze[0].Id);

        Assert.Empty(b.Komentarze);
    }

    [Fact]
    public void Panel_pokazuje_dodane_komentarze_i_oddaje_je_przy_zatwierdzeniu()
    {
        IReadOnlyList<CommentDraft>? oddane = null;
        var b = new CommentBridge();
        b.Dodaj("hero", "telefon za mało widoczny", "mobile");

        var cut = Render<CommentPanel>(p => p
            .Add(x => x.Bridge, b)
            .Add(x => x.OnApply, (IReadOnlyList<CommentDraft> k) => oddane = k));

        Assert.Contains("telefon za mało widoczny", cut.Find("li.cmt").TextContent);
        cut.Find("button.popraw").Click();

        Assert.NotNull(oddane);
        Assert.Single(oddane!);
        Assert.Equal("hero", oddane![0].Anchor);
    }

    [Fact]
    public void Pusta_lista_nie_pozwala_odpalic_rundy()
    {
        var cut = Render<CommentPanel>(p => p.Add(x => x.Bridge, new CommentBridge()));
        Assert.True(cut.Find("button.popraw").HasAttribute("disabled"));
    }
}
```

- [ ] **Step 2: Uruchom testy i potwierdź, że failują**

Run: `dotnet test tests/Generator.Web.Tests --filter CommentPanelTests`
Expected: FAIL — `CommentBridge` i `CommentPanel` nie istnieją.

- [ ] **Step 3: Napisz mostek**

`src/Generator.Web/Components/CommentBridge.cs`:

```csharp
using Generator.Web.Api;
using Microsoft.JSInterop;

namespace Generator.Web.Components;

/// <summary>
/// Odbiera "cmt:pick" z iframe'a i trzyma komentarze rundy.
/// Id nadaje TUTAJ, raz, w chwili dodania (decyzja 1 planu): obwod jest per karta
/// przegladarki, wiec podwojne "Popraw to" nie wprowadzi komentarza dwa razy.
/// Statusu nie ustawia - to robi silnik z commentResults[] (kontrakt 3.3).
/// </summary>
public sealed class CommentBridge : IDisposable
{
    private readonly List<CommentDraft> _komentarze = [];

    public IReadOnlyList<CommentDraft> Komentarze => _komentarze;

    /// <summary>Anchor ostatniego klikniecia; null = klient nie wskazal bloku.</summary>
    public string? OczekujacyAnchor { get; private set; }

    public bool MaOczekujacyKlik { get; private set; }

    public event Action? Changed;

    [JSInvokable]
    public Task OnPick(string? anchor)
    {
        OczekujacyAnchor = anchor;
        MaOczekujacyKlik = true;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void Dodaj(string? anchor, string text, string viewport)
    {
        _komentarze.Add(new CommentDraft(
            Id: Guid.NewGuid().ToString(),
            Anchor: anchor,
            Text: text,
            Viewport: viewport,
            CreatedAt: DateTimeOffset.UtcNow));

        OczekujacyAnchor = null;
        MaOczekujacyKlik = false;
        Changed?.Invoke();
    }

    public void Wycofaj(string id)
    {
        _komentarze.RemoveAll(k => k.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Po udanej rundzie silnik zamknal komentarze — czyscimy liste rundy.</summary>
    public void Wyczysc()
    {
        _komentarze.Clear();
        Changed?.Invoke();
    }

    public void Dispose() => Changed = null;
}
```

- [ ] **Step 4: Napisz panel**

`src/Generator.Web/Components/CommentPanel.razor`:

```razor
@using Generator.Web.Api
@implements IDisposable

<ul class="cmt-lista">
    @foreach (var k in Bridge.Komentarze)
    {
        <li class="cmt" @key="k.Id">
            <span class="cmt-anchor">@(k.Anchor ?? "cała strona")</span>
            <span class="cmt-text">@k.Text</span>
            <button class="wycofaj" @onclick="() => Bridge.Wycofaj(k.Id)">wycofaj</button>
        </li>
    }
</ul>

@if (Bridge.MaOczekujacyKlik)
{
    <div class="cmt-nowy">
        <p>@(Bridge.OczekujacyAnchor is null ? "Komentarz do całej strony" : $"Blok: {Bridge.OczekujacyAnchor}")</p>
        <textarea class="cmt-wpis" @bind="_wpis" @bind:event="oninput"
                  placeholder="Napisz, co jest nie tak"></textarea>
        <button class="dodaj" disabled="@string.IsNullOrWhiteSpace(_wpis)" @onclick="Dodaj">dodaj</button>
    </div>
}

<button class="popraw" disabled="@(Bridge.Komentarze.Count == 0)" @onclick="Zatwierdz">Popraw to</button>

@code {
    private string _wpis = "";

    [Parameter, EditorRequired] public CommentBridge Bridge { get; set; } = default!;
    [Parameter] public string Viewport { get; set; } = "desktop";
    [Parameter] public EventCallback<IReadOnlyList<CommentDraft>> OnApply { get; set; }

    protected override void OnInitialized() => Bridge.Changed += StateHasChanged;

    private void Dodaj()
    {
        // Viewport z przelacznika hosta, nie z iframe'a (decyzja 2 planu).
        Bridge.Dodaj(Bridge.OczekujacyAnchor, _wpis.Trim(), Viewport);
        _wpis = "";
    }

    private Task Zatwierdz() => OnApply.InvokeAsync(Bridge.Komentarze);

    public void Dispose() => Bridge.Changed -= StateHasChanged;
}
```

- [ ] **Step 5: Napisz shim hosta**

`src/Generator.Web/wwwroot/js/cmt-host.js`:

```javascript
// Mostek iframe -> Blazor. Iframe traktujemy jak niezaufany, mimo ze jest same-origin:
// jego tresc wygenerowal model z danych klienta.
window.cmtHost = {
  start(dotNetRef, iframeId) {
    const iframe = document.getElementById(iframeId);

    const onMessage = (e) => {
      if (e.source !== iframe.contentWindow) return;          // tylko nasz iframe
      if (e.origin !== window.location.origin) return;        // tylko nasze pochodzenie
      const d = e.data;
      if (!d || d.type !== "cmt:pick") return;                // tylko znany kształt
      const anchor = typeof d.anchor === "string" ? d.anchor : null;
      dotNetRef.invokeMethodAsync("OnPick", anchor);
    };

    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  },

  setMode(iframeId, on) {
    const iframe = document.getElementById(iframeId);
    iframe.contentWindow.postMessage({ type: "cmt:mode", on }, window.location.origin);
  },
};
```

- [ ] **Step 6: Uruchom testy i potwierdź, że przechodzą**

Run: `dotnet test tests/Generator.Web.Tests --filter CommentPanelTests`
Expected: PASS, 7 testów.

- [ ] **Step 7: Commit**

```bash
git add src/Generator.Web/Components src/Generator.Web/wwwroot/js/cmt-host.js tests/Generator.Web.Tests/CommentPanelTests.cs
git commit -m "feat(web): mostek postMessage -> JSInvokable i panel komentarzy"
```

---

### Task 6: Runda, UX przy `failed` i gwarancja z 4.4

**Files:**
- Create: `src/Generator.Web/Components/RoundStatus.razor`
- Create: `src/Generator.Web/Components/PreviewPane.razor`
- Test: `tests/Generator.Web.Tests/RoundStatusTests.cs`

**Interfaces:**
- Consumes: `IGeneratorApi`, `FakeGeneratorApi`, `JobView`, `JobFailure` (Task 2); `CommentBridge`, `CommentPanel` (Task 5).
- Produces: `RoundStatus` z parametrami `Api`, `Bridge`, `ProjectId`; `PreviewPane` z parametrami `ProjectId`, `Version`, `Viewport`, `TrybKomentowania`.

- [ ] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/RoundStatusTests.cs`:

```csharp
using Bunit;
using Generator.Web.Api;
using Generator.Web.Components;
using Xunit;

namespace Generator.Web.Tests;

public class RoundStatusTests : BunitContext
{
    private static CommentBridge ZKomentarzami(int ile)
    {
        var b = new CommentBridge();
        for (var i = 0; i < ile; i++) b.Dodaj("hero", $"uwaga {i}", "desktop");
        return b;
    }

    private IRenderedComponent<RoundStatus> Wyrenderuj(IGeneratorApi api, CommentBridge b) =>
        Render<RoundStatus>(p => p
            .Add(x => x.Api, api)
            .Add(x => x.Bridge, b)
            .Add(x => x.ProjectId, "p-1"));

    [Fact]
    public async Task Udana_runda_pokazuje_nowa_wersje_i_czysci_komentarze()
    {
        var b = ZKomentarzami(2);
        var cut = Wyrenderuj(FakeGeneratorApi.Sukces(), b);

        await cut.Find("button.popraw").ClickAsync(new());

        Assert.Contains("wersja 4", cut.Find(".wersja").TextContent);
        Assert.Empty(b.Komentarze);
    }

    /// <summary>
    /// Kontrakt 4.4 — gwarancja, nie konsekwencja. Trzy mechanizmy skladaja sie na to,
    /// ze nieudana runda jest dla klienta BRAKIEM ZDARZENIA. Jeden test, trzy asercje:
    /// gdy ktos kiedys zdecyduje, ze "powtorka zjada runde dla ograniczenia kosztu",
    /// ma zlamac wprost te regule, a nie rozmyc ja po cichu.
    /// </summary>
    [Fact]
    public async Task Nieudana_runda_nie_zabiera_klientowi_niczego()
    {
        var b = ZKomentarzami(2);
        var cut = Wyrenderuj(FakeGeneratorApi.Halted(), b);

        await cut.Find("button.popraw").ClickAsync(new());

        Assert.Contains("wersja 3", cut.Find(".wersja").TextContent);        // 1. podglad nietkniety
        Assert.Equal(2, b.Komentarze.Count);                                 // 2. komentarze zostaja open
        Assert.Contains("zostały 15 poprawki", cut.Find("p.rundy").TextContent); // 3. licznik sie nie ruszyl
    }

    [Fact]
    public async Task Halted_mowi_ze_problem_jest_u_nas_i_nie_kaze_klikac()
    {
        var cut = Wyrenderuj(FakeGeneratorApi.Halted(), ZKomentarzami(1));
        await cut.Find("button.popraw").ClickAsync(new());

        var box = cut.Find(".awaria");
        Assert.Contains("u nas", box.TextContent);
        Assert.Empty(cut.FindAll(".awaria button.ponow"));   // ponawianie nie ma sensu
    }

    [Fact]
    public async Task Retrying_mowi_ze_probuje_dalej()
    {
        var cut = Wyrenderuj(FakeGeneratorApi.Retrying(), ZKomentarzami(1));
        await cut.Find("button.popraw").ClickAsync(new());

        Assert.Contains("próbuję", cut.Find(".probuje").TextContent);
        Assert.Empty(cut.FindAll(".awaria"));
    }

    [Fact]
    public async Task Cause_nigdy_nie_trafia_do_UI()
    {
        // kontrakt §1. JobFailure nie ma pola cause, ten test pilnuje, ze nie wroci.
        var cut = Wyrenderuj(FakeGeneratorApi.Halted(), ZKomentarzami(1));
        await cut.Find("button.popraw").ClickAsync(new());

        Assert.DoesNotContain("permission_denials", cut.Markup);
        Assert.DoesNotContain("exit", cut.Markup);
    }

    [Fact]
    public async Task Osierocone_anchory_sa_widoczne_a_wersja_dostarczona()
    {
        // kontrakt 3.4: nie wyciszamy tego cicho
        var cut = Wyrenderuj(FakeGeneratorApi.ZOsieroconymi("cennik"), ZKomentarzami(1));
        await cut.Find("button.popraw").ClickAsync(new());

        Assert.Contains("wersja 4", cut.Find(".wersja").TextContent);
        Assert.Contains("cennik", cut.Find(".osierocone").TextContent);
    }

    [Fact]
    public async Task Zamrozony_projekt_nie_odpala_rundy()
    {
        var cut = Wyrenderuj(FakeGeneratorApi.Zamrozony(), ZKomentarzami(1));
        await cut.Find("button.popraw").ClickAsync(new());

        Assert.Contains("Wyczerpał", cut.Find(".zamrozony").TextContent);
        Assert.Empty(cut.FindAll(".awaria"));
    }

    [Fact]
    public async Task Budzet_nie_pojawia_sie_nigdzie_w_UI()
    {
        // kontrakt 4.1: licznik rund jest dla klienta, budzet tylko dla nas
        var cut = Wyrenderuj(FakeGeneratorApi.Sukces(), ZKomentarzami(1));
        await cut.Find("button.popraw").ClickAsync(new());

        Assert.DoesNotContain("USD", cut.Markup);
        Assert.DoesNotContain("$", cut.Markup);
    }
}
```

- [ ] **Step 2: Uruchom testy i potwierdź, że failują**

Run: `dotnet test tests/Generator.Web.Tests --filter RoundStatusTests`
Expected: FAIL — `RoundStatus` nie istnieje.

- [ ] **Step 3: Napisz `RoundStatus`**

`src/Generator.Web/Components/RoundStatus.razor`:

```razor
@using Generator.Web.Api

<p class="wersja">wersja @_projekt?.CurrentVersion</p>
<LicznikRund Uzyte="@(_projekt?.RoundsUsed ?? 0)" Limit="@(_projekt?.RoundsLimit ?? 0)" />

@if (_osierocone.Count > 0)
{
    <p class="osierocone">Nie udało mi się zachować powiązania z: @string.Join(", ", _osierocone)</p>
}

@if (_pracuje)
{
    <p class="pracuje">Claude pracuje nad Twoją stroną…</p>
}

@switch (_awaria?.Handling)
{
    case FailureHandling.Retrying:
        <p class="probuje">Nie udało się od razu — próbuję jeszcze raz. Nic nie przepadło.</p>
        break;
    case FailureHandling.Halted:
        <p class="awaria">Coś nie działa u nas. Twoja strona i uwagi są bezpieczne — zajmiemy się tym.</p>
        break;
}

@if (_zamrozony)
{
    <p class="zamrozony">Wyczerpał się zapas poprawek w tym projekcie. Strona i pobieranie działają dalej.</p>
}

<CommentPanel Bridge="Bridge" Viewport="@Viewport" OnApply="Popraw" />

@code {
    private ProjectView? _projekt;
    private JobFailure? _awaria;
    private IReadOnlyList<string> _osierocone = [];
    private bool _pracuje;
    private bool _zamrozony;

    [Parameter, EditorRequired] public IGeneratorApi Api { get; set; } = default!;
    [Parameter, EditorRequired] public CommentBridge Bridge { get; set; } = default!;
    [Parameter, EditorRequired] public string ProjectId { get; set; } = default!;
    [Parameter] public string Viewport { get; set; } = "desktop";

    protected override async Task OnInitializedAsync() => _projekt = await Api.GetProjectAsync(ProjectId);

    private async Task Popraw(IReadOnlyList<CommentDraft> komentarze)
    {
        _awaria = null;
        _osierocone = [];
        _zamrozony = false;
        _pracuje = true;

        string jobId;
        try
        {
            jobId = await Api.StartRoundAsync(ProjectId, _projekt!.CurrentVersion, komentarze);
        }
        catch (ProjectFrozenException)
        {
            // 409 z kontraktu 4.5 to nie awaria - komentarze zostaja, nic nie przepadlo.
            _pracuje = false;
            _zamrozony = true;
            return;
        }

        var job = await Api.GetJobAsync(jobId);
        _pracuje = false;
        _projekt = await Api.GetProjectAsync(ProjectId);

        if (job.State == JobState.Failed)
        {
            // Komentarzy NIE czyscimy i licznika NIE ruszamy (kontrakt 4.4).
            _awaria = job.Failure;
            return;
        }

        _osierocone = _projekt.Versions[^1].OrphanedAnchors;
        Bridge.Wyczysc();
    }
}
```

- [ ] **Step 4: Napisz `PreviewPane`**

`src/Generator.Web/Components/PreviewPane.razor`:

```razor
@inject IJSRuntime JS
@implements IAsyncDisposable

<div class="podglad @(Viewport == "mobile" ? "waski" : "szeroki")">
    <iframe id="@_iframeId" src="@($"/_preview/{ProjectId}/{Version}/")" title="Podgląd Twojej strony"></iframe>
</div>

@code {
    private readonly string _iframeId = $"podglad-{Guid.NewGuid():N}";
    private DotNetObjectReference<CommentBridge>? _ref;

    [Parameter, EditorRequired] public string ProjectId { get; set; } = default!;
    [Parameter, EditorRequired] public int Version { get; set; }
    [Parameter, EditorRequired] public CommentBridge Bridge { get; set; } = default!;
    [Parameter] public string Viewport { get; set; } = "desktop";
    [Parameter] public bool TrybKomentowania { get; set; }

    protected override async Task OnAfterRenderAsync(bool first)
    {
        if (first)
        {
            _ref = DotNetObjectReference.Create(Bridge);
            await JS.InvokeVoidAsync("cmtHost.start", _ref, _iframeId);
        }
        await JS.InvokeVoidAsync("cmtHost.setMode", _iframeId, TrybKomentowania);
    }

    public ValueTask DisposeAsync()
    {
        _ref?.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 5: Uruchom testy i potwierdź, że przechodzą**

Run: `dotnet test tests/Generator.Web.Tests --filter RoundStatusTests`
Expected: PASS, 9 testów.

- [ ] **Step 6: Uruchom cały zestaw**

Run: `dotnet test && node --test tests/js/`
Expected: wszystkie testy .NET PASS (Taski 1–6), `pass 5` w node.

- [ ] **Step 7: Commit**

```bash
git add src/Generator.Web/Components tests/Generator.Web.Tests/RoundStatusTests.cs
git commit -m "feat(web): runda, galezie failed i test gwarancji z kontraktu 4.4"
```

---

## Po wykonaniu planu

Działa pętla komentarzy na atrapie API: klient widzi stronę, klika blok, pisze uwagę,
odpala rundę i dostaje nową wersję albo uczciwy komunikat. Nieudana runda niczego mu nie
zabiera — i jest test, który to pilnuje.

Czego **nie** ma i gdzie to dopisać:

- **Plan D (kreator i propozycje)** — sekcje 2 i 3 planu produktu: dwa wejścia (URL / pomysł),
  maks. 5 pytań, wybór 1 z 3 propozycji pokazanych jako podglądy. Niezależne od pętli.
- **Podłączenie do Planu B** — `HttpGeneratorApi : IGeneratorApi` zamiast `FakeGeneratorApi`.
  Jedna klasa, reszta bez zmian; atrapa zostaje na testy.
- **Postęp po SignalR** — dziś `RoundStatus` pyta o zadanie raz po starcie. Kontrakt §1 daje
  postęp po obwodzie Blazora; do zrobienia razem z Planem B, bo wymaga strony serwerowej.
- **Porównanie „przed / po" i powrót do wersji** (plan, sekcja 4, punkt 5) — snapshoty są
  gotowe po stronie silnika, brakuje widoku.
- **Wymóg zwrotny do kontraktu §1:** podgląd serwuje `Generator.Web` z `/_preview`, ze
  wstrzykiwanym skryptem; endpoint `/api/.../preview` zostaje dla klientów poza naszym UI.
