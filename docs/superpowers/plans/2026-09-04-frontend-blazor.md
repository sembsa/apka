# Frontend Blazor — plan implementacji (Plan C z 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Stan (2026-09-04, po scaleniu):** Przemek napisał równolegle własny plan tej samej
> części (`frontend-petla-komentarzy.md`). Scalone w ten dokument, tamten usunięty —
> repo nie ma dwóch wersji tego, co znaczy Plan C.
>
> **Sebastianie:** wiem, że w `b6d4fee` usunąłeś ten plik z adnotacją „wygrywa wersja
> Przemka" — ustąpiliśmy sobie jednocześnie, dokładnie tak samo jak przy decyzjach 1 i 5.
> Plik wraca pod Twoją nazwą, bo Przemek wybrał Twoją strukturę i zakres (8 zadań, kreator
> i propozycje, `note` przy uwadze — tego w moim planie nie było). Treść jest scaleniem
> obu, nie przywróceniem Twojej wersji: lista zmian niżej. Nic Twojego nie zginęło. Co weszło z tamtego planu:
> API bUnit **2.x** (`BunitContext`, `Render<T>`) w 25 miejscach zamiast 1.x, które
> **nie kompiluje się** na `bunit 2.9.0` (`CS0619`, sprawdzone); Task 4a — serwowanie
> podglądu ze wstrzykiwaniem skryptu, bez którego `PreviewUrl` prowadzi w nicość, a nasz
> `<script>` wylądowałby w ZIP-ie klienta; jawne `targetOrigin` i walidacja `event.source`
> zamiast `'*'`; test gwarancji z §4.4 jako jeden test z trzema asercjami. Czego **nie**
> wzięliśmy z tamtego planu, bo Twoja wersja była lepsza: `e.target.closest()` zamiast
> ręcznego przejścia w górę drzewa, walidacja formatu id w jednym miejscu (C#, nie
> dodatkowo w JS), `viewport` mierzony realną szerokością iframe'a.
>
> **Dla Claude'a Przemka:** to **propozycja w Twoim obszarze**, nie polecenie. Frontend i kontrakt komentarza są Twoje — sekcję §3 kontraktu napisałeś Ty i ten plan z niej wynika, nie odwrotnie. Blok „Decyzje Przemka" niżej jest cytatem z Twoich ustaleń; jeśli plan gdzieś się od nich odkleja, to plan jest błędny. Zmieniaj, wywalaj zadania, przestawiaj kolejność.

**Goal:** Blazor Server, w którym nietechniczny klient przechodzi całą ścieżkę — opisuje pomysł albo podaje URL, wybiera jedną z trzech propozycji, oglądą wersję w iframe, klika w blok strony i pisze uwagę słowami, a potem widzi odpowiedź przy swojej uwadze.

**Architecture:** Blazor Server (obwód SignalR niesie postęp zadań, bez dorabiania SSE). Strony rozmawiają z backendem przez `IProjectApi` — w tym planie implementowany jako `MockProjectApi` w pamięci, żeby frontend **nie czekał na Plan B**. Mostek komentarza: klik w iframe → `postMessage` → shim JS → metoda `[JSInvokable]`.

**Tech Stack:** .NET 10, Blazor Server, bUnit + xUnit, `System.Threading.Channels` w mocku.

**Spec:**
- [`docs/api-contract.md`](../../api-contract.md) — §1 zasoby, §3 komentarz (Twoje), §4 liczniki i `failed`
- [`docs/plan/2026-09-04-generator-stron-plan.md`](../../plan/2026-09-04-generator-stron-plan.md) — sekcje 3, 4, 7
- [`2026-09-04-silnik-generatora.md`](2026-09-04-silnik-generatora.md) — Plan A (silnik), dla kontekstu

**Zależności:** żadnej twardej. Plan B (HTTP API) podmienia `MockProjectApi` na klienta HTTP za tym samym interfejsem. Dopóki go nie ma, cały frontend rozwija się i testuje na mocku.

## Global Constraints

### Decyzje Przemka — cytat z kontraktu §3, nie do wymyślania od nowa

- **`data-cmt-id` nadaje generator**, frontend **nigdy** go nie nadaje ani nie dorabia ze ścieżki DOM.
- **Format id:** `[a-z][a-z0-9-]{0,39}`, opisuje **rolę, nie pozycję** (`oferta-strzyzenie`, nie `oferta-1`).
- **`Comment.Id` to GUID nadany w przeglądarce**, nie na serwerze — czyni ponowienie „Popraw to" idempotentnym.
- **`anchor: null` = komentarz globalny.** Bez osobnego typu i bez osobnego znacznika: brak anchora *jest* znaczeniem.
- **`viewport`** (`desktop`/`mobile`) jedzie z każdym komentarzem — „telefon za mało widoczny" jest nierozstrzygalne bez szerokości, przy której klient patrzył.
- **Kolejność komentarzy = kolejność dodawania.** Przy sprzecznych obowiązuje **ostatni**, i to musi być widoczne w odpowiedzi.
- **Frontend nie dotyka statusu.** Tworzy `open`, umie **wycofać** (usunąć, dopóki `open`). `applied`/`rejected` ustawia silnik z `commentResults[]`.
- **`note` jest tekstem PRZY komentarzu**, nie zbiorczym podsumowaniem wersji.
- **Nie wysyłamy migawki treści bloku** — pole, na które nikt nie reaguje, jest kosztem po obu stronach mostka.

### Techniczne

- **.NET 10**, Blazor **Server** (nie WASM — decyzja 1 planu produktu).
- **bUnit jest dozwolony** jako zależność NuGet. Plan A zakazuje pakietów w silniku; dla UI to nie ma sensu — testów komponentów Blazora nie pisze się ręcznie.
- **`dotnet sln add`, nigdy `dotnet new sln`.** Solucja `Generator.sln` już istnieje (Plan A) i Sebastian pracuje w niej równolegle. Regeneracja = konflikt.
- **Twoje pliki to `src/Generator.Web` i `tests/Generator.Web.Tests`.** Nie ruszaj `src/Generator.Engine`, `src/Generator.FakeClaude`, `src/Generator.Cli`, `tests/Generator.Engine.Tests` — to Plan A, w robocie w tym samym czasie.
- **Rytm gita z `CLAUDE.md`** obowiązuje: pull przed pracą, mały commit, push od razu.

---

## Struktura plików

| Plik | Odpowiedzialność |
|---|---|
| `src/Generator.Web/Contracts/IProjectApi.cs` | interfejs backendu w kształcie kontraktu §1 |
| `src/Generator.Web/Contracts/Dtos.cs` | DTO: `ProjectView`, `VersionView`, `CommentDto`, `JobView`, `ProposalView` |
| `src/Generator.Web/Mock/MockProjectApi.cs` | implementacja w pamięci: opóźnienia, `retrying`, `halted`, `frozen` |
| `src/Generator.Web/Components/Pages/Start.razor` | kreator wejścia: URL albo opis |
| `src/Generator.Web/Components/Pages/Proposals.razor` | trzy propozycje jako podglądy |
| `src/Generator.Web/Components/Pages/Editor.razor` | podgląd wersji + tryb komentowania + lista uwag |
| `src/Generator.Web/Components/CommentBridge.razor` | odbiór kliknięć z iframe przez `[JSInvokable]` |
| `src/Generator.Web/Components/CommentList.razor` | uwagi ze statusami i `note` |
| `src/Generator.Web/Components/JobStatus.razor` | postęp zadania, gałęzie `retrying`/`halted` |
| `src/Generator.Web/wwwroot/js/comment-bridge.js` | shim: nasłuch `postMessage`, wywołanie `DotNet` |
| `src/Generator.Web/wwwroot/js/preview-click.js` | skrypt wstrzykiwany do iframe: zbiera kliknięcia |
| `src/Generator.Web/Preview/PreviewInjector.cs` | doklejenie `<script>` przy serwowaniu, nie do snapshotu |
| `src/Generator.Web/Preview/PreviewEndpoint.cs` | trasa `/preview/{id}/{n}` — serwuje pliki snapshotu |
| `tests/Generator.Web.Tests/*` | bUnit, jeden plik na komponent |

---

### Task 1: Projekt, mock API i pierwszy test komponentu

**Files:**
- Create: `src/Generator.Web/*` (szablon), `src/Generator.Web/Contracts/IProjectApi.cs`, `src/Generator.Web/Contracts/Dtos.cs`, `src/Generator.Web/Mock/MockProjectApi.cs`, `tests/Generator.Web.Tests/Generator.Web.Tests.csproj`
- Modify: `Generator.sln` (przez `dotnet sln add`)
- Test: `tests/Generator.Web.Tests/MockProjectApiTests.cs`

**Interfaces:**
- Consumes: nic (mock jest samowystarczalny)
- Produces:
  - `record ProjectView(string Id, string Token, string Status, int RoundsUsed, int RoundsLimit, IReadOnlyList<VersionView> Versions)`
  - `record VersionView(int Number, string PreviewUrl, IReadOnlyList<string> OrphanedAnchors)`
  - `record CommentDto(string Id, string? Anchor, string Text, string Viewport, DateTimeOffset CreatedAt, string Status, string? Note)`
  - `record ProposalView(string Id, string Name, string PreviewHtml)`
  - `record JobView(string Id, string Status, JobFailureView? Failure)`, `record JobFailureView(string Handling, int Attempts)`
  - `interface IProjectApi` z `Task<ProjectView> CreateAsync(string source, string description)`, `Task<ProjectView> GetAsync(string id)`, `Task<string> RequestProposalsAsync(string id)`, `Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id)`, `Task ChooseProposalAsync(string id, string proposalId)`, `Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)`, `Task<JobView> GetJobAsync(string jobId)`, `Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version)`
  - `class MockProjectApi : IProjectApi` z przełącznikami `NextJobOutcome` (`Success`/`Retrying`/`Halted`), `SimulatedDelay`

- [x] **Step 1: Utwórz projekty**

```bash
dotnet new blazor -o src/Generator.Web -f net10.0 --interactivity Server --empty
dotnet new xunit -o tests/Generator.Web.Tests -f net10.0
dotnet sln add src/Generator.Web tests/Generator.Web.Tests     # NIGDY dotnet new sln
dotnet add tests/Generator.Web.Tests reference src/Generator.Web
dotnet add tests/Generator.Web.Tests package bunit --version 2.9.0
```

- [x] **Step 2: Napisz failujące testy mocka**

`tests/Generator.Web.Tests/MockProjectApiTests.cs`:

```csharp
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Xunit;

namespace Generator.Web.Tests;

public class MockProjectApiTests
{
    private static MockProjectApi Api() => new() { SimulatedDelay = TimeSpan.Zero };

    private static CommentDto C(string id, string? anchor, string text) =>
        new(id, anchor, text, "desktop", DateTimeOffset.UtcNow, "open", null);

    [Fact]
    public async Task Nowy_projekt_ma_token_i_liczniki_z_kontraktu()
    {
        var p = await Api().CreateAsync("idea", "Fryzjer w Nowym Saczu");

        Assert.NotEmpty(p.Token);
        Assert.Equal(0, p.RoundsUsed);
        Assert.Equal(15, p.RoundsLimit);
        Assert.Empty(p.Versions);
    }

    [Fact]
    public async Task Runda_zwraca_jobId_a_nie_gotowa_wersje()
    {
        // Kontrakt 1: wszystko, co uruchamia model, jest zadaniem.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");

        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", "hero", "telefon za maly")]);

        Assert.NotEmpty(jobId);
        var job = await api.GetJobAsync(jobId);
        Assert.Contains(job.Status, new[] { "queued", "running", "succeeded" });
    }

    [Fact]
    public async Task Halted_niesie_handling_ale_nie_przyczyne()
    {
        // Kontrakt 4.3: cause NIE idzie do UI.
        var api = Api();
        api.NextJobOutcome = MockJobOutcome.Halted;
        var p = await api.CreateAsync("idea", "x");

        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]);
        var job = await api.GetJobAsync(jobId);

        Assert.Equal("failed", job.Status);
        Assert.Equal("halted", job.Failure!.Handling);
    }

    [Fact]
    public async Task Nieudana_runda_nie_zuzywa_rundy_i_zostawia_komentarze_open()
    {
        // Kontrakt 4.4: z punktu widzenia klienta to BRAK ZDARZENIA.
        var api = Api();
        api.NextJobOutcome = MockJobOutcome.Halted;
        var p = await api.CreateAsync("idea", "x");
        await api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]);

        var after = await api.GetAsync(p.Id);
        var comments = await api.GetCommentsAsync(p.Id, 1);

        Assert.Equal(0, after.RoundsUsed);
        Assert.All(comments, c => Assert.Equal("open", c.Status));
    }

    [Fact]
    public async Task Wyczerpanie_rund_zamraza_projekt_a_apply_odmawia()
    {
        // Kontrakt 4.5: frozen, podglad dziala, nowe rundy odrzucane.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        api.ForceFrozen(p.Id);

        var frozen = await api.GetAsync(p.Id);
        Assert.Equal("frozen", frozen.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]));
    }

    [Fact]
    public async Task Udana_runda_zwraca_note_przy_kazdym_komentarzu()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", "hero", "telefon za maly")]);
        await api.GetJobAsync(jobId);

        var comments = await api.GetCommentsAsync(p.Id, 1);
        var c = Assert.Single(comments);
        Assert.Equal("applied", c.Status);
        Assert.False(string.IsNullOrWhiteSpace(c.Note));
    }
}
```

- [x] **Step 3: Uruchom, potwierdź porażkę**

Run: `dotnet test tests/Generator.Web.Tests -v q`
Expected: błąd kompilacji — `Generator.Web.Contracts` i `Generator.Web.Mock` nie istnieją.

- [x] **Step 4: Zaimplementuj kontrakty i mock**

`src/Generator.Web/Contracts/Dtos.cs`:

```csharp
namespace Generator.Web.Contracts;

public record VersionView(int Number, string PreviewUrl, IReadOnlyList<string> OrphanedAnchors);

public record ProjectView(
    string Id,
    string Token,
    string Status,              // "draft" | "active" | "frozen"
    int RoundsUsed,
    int RoundsLimit,
    IReadOnlyList<VersionView> Versions);

/// Kształt z kontraktu 3.2. Id to GUID z PRZEGLĄDARKI — nie z serwera.
public record CommentDto(
    string Id,
    string? Anchor,             // null = komentarz globalny
    string Text,
    string Viewport,            // "desktop" | "mobile"
    DateTimeOffset CreatedAt,
    string Status,              // "open" | "applied" | "rejected"
    string? Note);              // zdanie PRZY komentarzu, nie zbiorcze

public record ProposalView(string Id, string Name, string PreviewHtml);

/// Kontrakt 4.3: UI rozgałęzia się po Handling. Cause tu nie ma i mieć nie będzie.
public record JobFailureView(string Handling, int Attempts);

public record JobView(string Id, string Status, JobFailureView? Failure);
```

`src/Generator.Web/Contracts/IProjectApi.cs`:

```csharp
namespace Generator.Web.Contracts;

/// Kształt kontraktu §1. MockProjectApi teraz, klient HTTP po Planie B —
/// strony nie zauważą podmiany.
public interface IProjectApi
{
    Task<ProjectView> CreateAsync(string source, string description);
    Task<ProjectView> GetAsync(string id);
    Task<string> RequestProposalsAsync(string id);
    Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id);
    Task ChooseProposalAsync(string id, string proposalId);
    Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments);
    Task<JobView> GetJobAsync(string jobId);
    Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version);
}
```

`src/Generator.Web/Mock/MockProjectApi.cs`:

```csharp
using System.Collections.Concurrent;
using Generator.Web.Contracts;

namespace Generator.Web.Mock;

public enum MockJobOutcome { Success, Retrying, Halted }

/// Backend w pamieci, zeby frontend nie czekal na Plan B.
/// Odwzorowuje te zachowania kontraktu, ktore widzi UI: zadania zamiast
/// odpowiedzi synchronicznych, asymetrie licznikow (4.1), gwarancje 4.4, frozen (4.5).
public class MockProjectApi : IProjectApi
{
    private readonly ConcurrentDictionary<string, ProjectView> _projects = new();
    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly ConcurrentDictionary<(string, int), List<CommentDto>> _comments = new();

    public MockJobOutcome NextJobOutcome { get; set; } = MockJobOutcome.Success;
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.FromSeconds(2);

    public Task<ProjectView> CreateAsync(string source, string description)
    {
        var p = new ProjectView(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString("N"),
            "draft",
            RoundsUsed: 0,
            RoundsLimit: 15,
            Versions: []);
        _projects[p.Id] = p;
        return Task.FromResult(p);
    }

    public Task<ProjectView> GetAsync(string id) => Task.FromResult(_projects[id]);

    public void ForceFrozen(string id) => _projects[id] = _projects[id] with { Status = "frozen" };

    public async Task<string> RequestProposalsAsync(string id)
    {
        await Task.Delay(SimulatedDelay);
        var jobId = Guid.NewGuid().ToString();
        _jobs[jobId] = new JobView(jobId, "succeeded", null);
        return jobId;
    }

    public Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id) =>
        Task.FromResult<IReadOnlyList<ProposalView>>(
        [
            new("p-1", "Ciepły i prosty", "<html><body style='font-family:system-ui'><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>"),
            new("p-2", "Nowoczesny, ciemny", "<html><body style='background:#111;color:#eee'><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>"),
            new("p-3", "Klasyczny z cennikiem", "<html><body><h1 data-cmt-id=\"hero\">Fryzjer</h1><ul data-cmt-id=\"cennik\"><li>Strzyżenie 60 zł</li></ul></body></html>"),
        ]);

    public Task ChooseProposalAsync(string id, string proposalId)
    {
        _projects[id] = _projects[id] with { Status = "active" };
        return Task.CompletedTask;
    }

    public async Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        var project = _projects[id];
        if (project.Status == "frozen")
            throw new InvalidOperationException("projekt zamrozony (409)");   // kontrakt 4.5

        _comments[(id, version)] = [.. comments];

        var jobId = Guid.NewGuid().ToString();
        _jobs[jobId] = new JobView(jobId, "running", null);
        await Task.Delay(SimulatedDelay);

        switch (NextJobOutcome)
        {
            case MockJobOutcome.Success:
                _comments[(id, version)] =
                [
                    .. comments.Select(c => c with
                    {
                        Status = "applied",
                        Note = $"Zrobione: {c.Text}",
                    }),
                ];
                _projects[id] = project with
                {
                    RoundsUsed = project.RoundsUsed + 1,
                    Versions = [.. project.Versions,
                        new VersionView(project.Versions.Count + 1, $"/preview/{id}/{project.Versions.Count + 1}", [])],
                };
                _jobs[jobId] = new JobView(jobId, "succeeded", null);
                break;

            // Kontrakt 4.4: nieudana runda NIE zuzywa rundy i NIE rusza statusow.
            case MockJobOutcome.Retrying:
                _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView("retrying", 1));
                break;

            case MockJobOutcome.Halted:
                _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView("halted", 2));
                break;
        }

        return jobId;
    }

    public Task<JobView> GetJobAsync(string jobId) => Task.FromResult(_jobs[jobId]);

    public Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version) =>
        Task.FromResult<IReadOnlyList<CommentDto>>(
            _comments.TryGetValue((id, version), out var list) ? list : []);
}
```

Zarejestruj w `Program.cs`: `builder.Services.AddSingleton<IProjectApi, MockProjectApi>();`

- [x] **Step 5: Uruchom testy i commituj**

Run: `dotnet test tests/Generator.Web.Tests -v q`
Expected: 6 testów PASS.

```bash
git add src/Generator.Web tests/Generator.Web.Tests Generator.sln
git commit -m "feat(web): projekt Blazor Server, kontrakt IProjectApi i mock backendu"
git push
```

---

### Task 2: Kreator wejścia — URL albo opis

**Files:**
- Create: `src/Generator.Web/Components/Pages/Start.razor`
- Test: `tests/Generator.Web.Tests/StartPageTests.cs`

**Interfaces:**
- Consumes: `IProjectApi.CreateAsync`
- Produces: strona `/`, po wysłaniu nawigacja do `/proposals/{projectId}`

Sekcja 2 planu produktu: **maks. 5 pytań w prostym języku.** Żadnego pytania o technologię, fonty, kolory.

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/StartPageTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

public class StartPageTests : BunitContext
{
    private MockProjectApi Register()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        Services.AddSingleton<IProjectApi>(api);
        return api;
    }

    [Fact]
    public void Domyslnie_pyta_o_opis_pomyslu()
    {
        Register();
        var cut = Render<Start>();

        Assert.Contains("Opowiedz", cut.Markup);
        Assert.NotNull(cut.Find("textarea"));
    }

    [Fact]
    public void Przelacznik_pokazuje_pole_na_adres_strony()
    {
        Register();
        var cut = Render<Start>();

        cut.Find("[data-test='tryb-url']").Click();

        Assert.NotNull(cut.Find("input[type='url']"));
    }

    [Fact]
    public void Nie_pyta_o_technologie_fonty_ani_kolory()
    {
        // Sekcja 1 planu: klient NIC nie robi sam. Te pytania to nie jego praca.
        Register();
        var cut = Render<Start>();

        foreach (var slowo in new[] { "font", "kolor", "framework", "CSS", "szablon" })
            Assert.DoesNotContain(slowo, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pusty_formularz_nie_tworzy_projektu()
    {
        Register();
        var cut = Render<Start>();

        cut.Find("form").Submit();

        Assert.Contains("Napisz", cut.Markup);   // komunikat, nie wyjątek
    }
}
```

- [x] **Step 2: Uruchom, potwierdź porażkę**

Run: `dotnet test tests/Generator.Web.Tests --filter StartPageTests -v q`
Expected: błąd kompilacji — `Start` nie istnieje.

- [x] **Step 3: Napisz stronę**

`src/Generator.Web/Components/Pages/Start.razor`:

```razor
@page "/"
@inject IProjectApi Api
@inject NavigationManager Nav
@using Generator.Web.Contracts

<h1>Zrobimy Ci stronę</h1>
<p>Odpowiedz jednym akapitem. Resztą zajmiemy się my.</p>

<div role="group">
    <button data-test="tryb-opis" class="@(_mode == Mode.Idea ? "wybrany" : "")"
            @onclick="() => _mode = Mode.Idea">Nie mam strony</button>
    <button data-test="tryb-url" class="@(_mode == Mode.Url ? "wybrany" : "")"
            @onclick="() => _mode = Mode.Url">Mam stronę, ale słabą</button>
</div>

<form @onsubmit="Send" @onsubmit:preventDefault>
    @if (_mode == Mode.Idea)
    {
        <label for="opis">Opowiedz, czym się zajmujesz i dla kogo</label>
        <textarea id="opis" rows="5" @bind="_text"
                  placeholder="Np. Strzygę damsko-męsko w Nowym Sączu, ul. Krakowska 5, tel. 600 100 200, pon-pt 9-18."></textarea>
    }
    else
    {
        <label for="url">Adres Twojej obecnej strony</label>
        <input id="url" type="url" @bind="_text" placeholder="https://..." />
    }

    @if (_error is not null)
    {
        <p role="alert">@_error</p>
    }

    <button type="submit" disabled="@_busy">@(_busy ? "Tworzę…" : "Dalej")</button>
</form>

@code {
    private enum Mode { Idea, Url }

    private Mode _mode = Mode.Idea;
    private string _text = string.Empty;
    private string? _error;
    private bool _busy;

    private async Task Send()
    {
        if (string.IsNullOrWhiteSpace(_text))
        {
            _error = _mode == Mode.Idea
                ? "Napisz kilka słów o tym, czym się zajmujesz."
                : "Napisz adres swojej strony.";
            return;
        }

        _error = null;
        _busy = true;
        var project = await Api.CreateAsync(_mode == Mode.Idea ? "idea" : "url", _text.Trim());
        Nav.NavigateTo($"/proposals/{project.Id}");
    }
}
```

- [x] **Step 4: Uruchom testy**

Run: `dotnet test tests/Generator.Web.Tests --filter StartPageTests -v q`
Expected: 4 testy PASS.

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web/Components/Pages/Start.razor tests/Generator.Web.Tests/StartPageTests.cs
git commit -m "feat(web): kreator wejscia — opis pomyslu albo adres strony"
git push
```

---

### Task 3: Trzy propozycje jako podglądy, nie opisy

**Files:**
- Create: `src/Generator.Web/Components/Pages/Proposals.razor`
- Test: `tests/Generator.Web.Tests/ProposalsPageTests.cs`

**Interfaces:**
- Consumes: `IProjectApi.RequestProposalsAsync`, `GetProposalsAsync`, `ChooseProposalAsync`
- Produces: strona `/proposals/{projectId}`, po wyborze nawigacja do `/editor/{projectId}`

Sekcja 3 planu: propozycje pokazujemy **jako podglądy**, klient ma zobaczyć, nie czytać specyfikację. Wybór to **jedno kliknięcie**, bez pisania.

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/ProposalsPageTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

public class ProposalsPageTests : BunitContext
{
    private async Task<(MockProjectApi api, string projectId)> Setup()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        return (api, p.Id);
    }

    [Fact]
    public async Task Pokazuje_trzy_propozycje_jako_podglady_w_iframe()
    {
        var (_, id) = await Setup();
        var cut = Render<Proposals>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("iframe").Count));
    }

    [Fact]
    public async Task Wybor_to_jedno_klikniecie_bez_pisania()
    {
        var (_, id) = await Setup();
        var cut = Render<Proposals>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='wybierz']")));

        Assert.Empty(cut.FindAll("textarea"));
        Assert.Empty(cut.FindAll("input[type='text']"));
    }

    [Fact]
    public async Task Podczas_generowania_widac_ze_cos_sie_dzieje()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.FromMilliseconds(300) };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "x");

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        Assert.Contains("Przygotowuję", cut.Markup);
    }
}
```

- [x] **Step 2: Uruchom, potwierdź porażkę**

Run: `dotnet test tests/Generator.Web.Tests --filter ProposalsPageTests -v q`
Expected: błąd kompilacji — `Proposals` nie istnieje.

- [x] **Step 3: Napisz stronę**

`src/Generator.Web/Components/Pages/Proposals.razor`:

```razor
@page "/proposals/{ProjectId}"
@inject IProjectApi Api
@inject NavigationManager Nav
@using Generator.Web.Contracts

<h1>Wybierz, która wygląda lepiej</h1>

@if (_proposals is null)
{
    <p aria-live="polite">Przygotowuję trzy propozycje…</p>
}
else
{
    <div class="propozycje">
        @foreach (var p in _proposals)
        {
            <figure>
                @* srcdoc, nie src: propozycja nie jest jeszcze zapisana na dysku *@
                <iframe title="@p.Name" srcdoc="@p.PreviewHtml" sandbox=""></iframe>
                <figcaption>@p.Name</figcaption>
                <button data-test="wybierz" @onclick="() => Choose(p.Id)">Ta mi się podoba</button>
            </figure>
        }
    </div>
}

@code {
    [Parameter] public string ProjectId { get; set; } = string.Empty;

    private IReadOnlyList<ProposalView>? _proposals;

    protected override async Task OnInitializedAsync()
    {
        await Api.RequestProposalsAsync(ProjectId);
        _proposals = await Api.GetProposalsAsync(ProjectId);
    }

    private async Task Choose(string proposalId)
    {
        await Api.ChooseProposalAsync(ProjectId, proposalId);
        Nav.NavigateTo($"/editor/{ProjectId}");
    }
}
```

- [x] **Step 4: Uruchom testy**

Run: `dotnet test tests/Generator.Web.Tests --filter ProposalsPageTests -v q`
Expected: 3 testy PASS.

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web/Components/Pages/Proposals.razor tests/Generator.Web.Tests/ProposalsPageTests.cs
git commit -m "feat(web): trzy propozycje jako podglady, wybor jednym klikniecien"
git push
```

---

### Task 4: Mostek komentarza — iframe → `postMessage` → `[JSInvokable]`

To zadanie, przed którym sam ostrzegałeś w §3: mostek leży w rdzeniu produktu. Dlatego dostaje osobne zadanie i test na **kształt payloadu**, nie na to, jak go zbierasz.

**Files:**
- Create: `src/Generator.Web/Components/CommentBridge.razor`, `src/Generator.Web/wwwroot/js/comment-bridge.js`, `src/Generator.Web/wwwroot/js/preview-click.js`
- Test: `tests/Generator.Web.Tests/CommentBridgeTests.cs`

**Interfaces:**
- Consumes: nic
- Produces:
  - `class CommentBridge` z `[JSInvokable] public Task OnBlockClicked(ClickPayload payload)` i `[Parameter] public EventCallback<CommentDto> OnCommentDrafted { get; set; }`
  - `record ClickPayload(string? Anchor, string Viewport)`
  - **Kontrakt skryptu w iframe:** na klik wysyła `window.parent.postMessage({type:'cmt-click', anchor, viewport}, window.location.origin)`, gdzie `anchor` to wartość `data-cmt-id` najbliższego przodka albo `null`. Bez `x`/`y` — przechodziły przez cały łańcuch i nikt ich nie czytał.

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/CommentBridgeTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

public class CommentBridgeTests : BunitContext
{
    [Fact]
    public async Task Klik_w_blok_tworzy_komentarz_z_anchorem_i_guidem_z_przegladarki()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("oferta-strzyzenie", "desktop"));

        Assert.NotNull(drafted);
        Assert.Equal("oferta-strzyzenie", drafted.Anchor);
        Assert.Equal("desktop", drafted.Viewport);
        Assert.Equal("open", drafted.Status);
        Assert.True(Guid.TryParse(drafted.Id, out _), "Id musi byc GUID-em nadanym w przegladarce");
        Assert.Null(drafted.Note);
    }

    [Fact]
    public async Task Klik_poza_blokiem_daje_komentarz_globalny_bez_osobnego_typu()
    {
        // Kontrakt 3.2: anchor == null JEST znaczeniem, nie brakiem danych.
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload(null, "mobile"));

        Assert.NotNull(drafted);
        Assert.Null(drafted.Anchor);
        Assert.Equal("mobile", drafted.Viewport);
    }

    [Fact]
    public async Task Anchor_niezgodny_z_formatem_traktujemy_jak_globalny()
    {
        // Format z 3.1: [a-z][a-z0-9-]{0,39}. "Hero" i "oferta 1" nie przechodza.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var drafted = new List<CommentDto>();
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted.Add(c)));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("Hero", "desktop"));
        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("oferta 1", "desktop"));

        Assert.Equal(2, drafted.Count);
        Assert.All(drafted, c => Assert.Null(c.Anchor));
    }

    [Fact]
    public async Task Nie_przenosi_migawki_tresci_bloku()
    {
        // Kontrakt 3.2: swiadomie NIE wysylamy snapshotu tresci.
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("hero", "desktop"));

        var pola = typeof(CommentDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Snapshot", pola);
        Assert.DoesNotContain("BlockHtml", pola);
        Assert.NotNull(drafted);
    }
}
```

- [x] **Step 2: Uruchom, potwierdź porażkę**

Run: `dotnet test tests/Generator.Web.Tests --filter CommentBridgeTests -v q`
Expected: błąd kompilacji — `CommentBridge` i `ClickPayload` nie istnieją.

- [x] **Step 3: Zaimplementuj mostek**

`src/Generator.Web/Components/CommentBridge.razor`:

```razor
@implements IAsyncDisposable
@inject IJSRuntime JS
@using System.Text.RegularExpressions
@using Generator.Web.Contracts

@code {
    /// Kształt payloadu z iframe. Bez migawki treści — kontrakt 3.2.
    public record ClickPayload(string? Anchor, string Viewport);

    [Parameter] public EventCallback<CommentDto> OnCommentDrafted { get; set; }

    /// <summary>Id elementu iframe, z ktorego wolno przyjmowac klikniecia.</summary>
    [Parameter, EditorRequired] public string IframeId { get; set; } = default!;

    private DotNetObjectReference<CommentBridge>? _self;

    /// Format z kontraktu 3.1. Id niezgodne z formatem traktujemy jak brak anchora —
    /// lepiej komentarz globalny niż wskazanie w nieistniejący blok.
    private static readonly Regex AnchorFormat = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _self = DotNetObjectReference.Create(this);
        // IframeId przekazuje strona z podgladem - shim sprawdza, ze wiadomosc
        // przyszla wlasnie z tego iframe'a, a nie z dowolnego okna.
        await JS.InvokeVoidAsync("commentBridge.listen", _self, IframeId);
    }

    [JSInvokable]
    public async Task OnBlockClicked(ClickPayload payload)
    {
        var anchor = payload.Anchor is not null && AnchorFormat.IsMatch(payload.Anchor)
            ? payload.Anchor
            : null;

        var comment = new CommentDto(
            Id: Guid.NewGuid().ToString(),      // GUID w przeglądarce — kontrakt 3.2
            Anchor: anchor,
            Text: string.Empty,                 // treść dopisuje klient w formularzu
            Viewport: payload.Viewport,
            CreatedAt: DateTimeOffset.UtcNow,
            Status: "open",
            Note: null);

        await OnCommentDrafted.InvokeAsync(comment);
    }

    public async ValueTask DisposeAsync()
    {
        if (_self is not null)
        {
            try { await JS.InvokeVoidAsync("commentBridge.stop"); } catch (JSDisconnectedException) { }
            _self.Dispose();
        }
    }
}
```

`src/Generator.Web/wwwroot/js/comment-bridge.js`:

```javascript
// Shim: jedyne miejsce, w ktorym postMessage spotyka sie z .NET.
// Iframe traktujemy jak niezaufany, mimo ze jest same-origin: jego tresc
// wygenerowal model z danych klienta. Stad trzy bramki przed invokeMethodAsync.
window.commentBridge = (() => {
  let handler = null;

  return {
    listen(dotNetRef, iframeId) {
      handler = (event) => {
        const iframe = document.getElementById(iframeId);
        if (!iframe || event.source !== iframe.contentWindow) return;  // 1. nasz iframe
        if (event.origin !== window.location.origin) return;           // 2. nasze pochodzenie
        const d = event.data;
        if (!d || d.type !== 'cmt-click') return;                      // 3. znany ksztalt
        dotNetRef.invokeMethodAsync('OnBlockClicked', {
          anchor: typeof d.anchor === 'string' ? d.anchor : null,
          viewport: d.viewport === 'mobile' ? 'mobile' : 'desktop',
        });
      };
      window.addEventListener('message', handler);
    },
    stop() {
      if (handler) window.removeEventListener('message', handler);
      handler = null;
    },
  };
})();
```

`src/Generator.Web/wwwroot/js/preview-click.js` — skrypt **wstrzykiwany do strony klienta dopiero przy serwowaniu podglądu** (Task 4a), nigdy zapisywany do snapshotu:

```javascript
// Zostaje przy e.target.closest('[data-cmt-id]') - to jedna linia standardowego DOM
// i robi dokladnie to, co trzeba. Format id waliduje C# (AnchorFormat), w jednym miejscu.
// Viewport bierzemy z wlasnej szerokosci iframe'a: to jest doslownie ta szerokosc,
// przy ktorej klient patrzyl (kontrakt 3.2). Prog 768 musi byc rowny progowi, na ktorym
// host przelacza szerokosc podgladu - jesli sie rozjada, viewport zacznie klamac.
document.addEventListener('click', (e) => {
  const block = e.target.closest('[data-cmt-id]');
  window.parent.postMessage({
    type: 'cmt-click',
    anchor: block ? block.getAttribute('data-cmt-id') : null,
    viewport: window.innerWidth < 768 ? 'mobile' : 'desktop',
  }, window.location.origin);   // nigdy '*'
}, true);
```

- [x] **Step 4: Uruchom testy**

Run: `dotnet test tests/Generator.Web.Tests --filter CommentBridgeTests -v q`
Expected: 4 testy PASS.

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web/Components/CommentBridge.razor src/Generator.Web/wwwroot/js tests/Generator.Web.Tests/CommentBridgeTests.cs
git commit -m "feat(web): mostek komentarza — postMessage do JSInvokable, GUID w przegladarce"
git push
```

---

### Task 4a: Serwowanie podglądu i wstrzykiwanie skryptu

Bez tego zadania `VersionView.PreviewUrl` z Taska 1 wskazuje pod adres, którego nikt nie
obsługuje, a skrypt z Taska 4 nie ma jak dojść do strony klienta. **Wstrzykiwanie musi
dziać się przy serwowaniu, nie przy generowaniu:** gdyby generator zapisał nasz `<script>`
do snapshotu, klient dostałby go w ZIP-ie i w opublikowanej stronie. Decyzja 4 planu
produktu mówi, że prosta strona ma być prosta również w środku.

**Files:**
- Create: `src/Generator.Web/Preview/PreviewInjector.cs`
- Create: `src/Generator.Web/Preview/PreviewEndpoint.cs`
- Modify: `src/Generator.Web/Program.cs`
- Test: `tests/Generator.Web.Tests/PreviewInjectorTests.cs`

**Interfaces:**
- Consumes: nic (czysta operacja na stringu + endpoint).
- Produces:
  - `static class PreviewInjector` z `static string Inject(string html, string scriptUrl)`
  - `static class PreviewEndpoint` z `static void MapPreview(this WebApplication app, Func<string,int,string> snapshotDir)`
  - trasa `GET /preview/{projectId}/{version:int}/{**sciezka}` — zgodna z `PreviewUrl` z Taska 1

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/PreviewInjectorTests.cs`:

```csharp
using Generator.Web.Preview;
using Xunit;

namespace Generator.Web.Tests;

public class PreviewInjectorTests
{
    private const string Url = "/js/preview-click.js";

    [Fact]
    public void Wstrzykuje_przed_zamknieciem_body()
    {
        var html = """<html><body><h1 data-cmt-id="hero">Fryzjer</h1></body></html>""";
        var wynik = PreviewInjector.Inject(html, Url);

        Assert.Contains($"""<script src="{Url}"></script></body>""", wynik);
        Assert.Contains("""data-cmt-id="hero" """.TrimEnd(), wynik);   // tresc klienta nietknieta
    }

    [Fact]
    public void Bez_body_doklejamy_na_koniec()
    {
        // Twarda bramka z 5.2 wymaga tylko <html i </html>, wiec HTML bez </body>
        // moze przejsc walidacje. Podglad musi wtedy dzialac tak samo.
        var wynik = PreviewInjector.Inject("<html><p>bez body</p></html>", Url);
        Assert.EndsWith($"""<script src="{Url}"></script>""", wynik);
    }

    [Fact]
    public void Wstrzykuje_dokladnie_raz_przy_wielu_body()
    {
        var html = "<html><body>a</body><body>b</body></html>";
        Assert.Equal(1, Wystapienia(PreviewInjector.Inject(html, Url), "preview-click.js"));
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

- [x] **Step 2: Uruchom testy i potwierdź, że failują**

Run: `dotnet test tests/Generator.Web.Tests --filter PreviewInjectorTests -v q`
Expected: FAIL — kompilacja, `PreviewInjector` nie istnieje (`CS0246`).

- [x] **Step 3: Napisz injector**

`src/Generator.Web/Preview/PreviewInjector.cs`:

```csharp
namespace Generator.Web.Preview;

/// <summary>
/// Dokleja skrypt zbierajacy klikniecia PRZY SERWOWANIU podgladu.
/// Snapshot na dysku i ZIP klienta zostaja czyste.
/// </summary>
public static class PreviewInjector
{
    public static string Inject(string html, string scriptUrl)
    {
        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase)) return html;

        var tag = $"""<script src="{scriptUrl}"></script>""";
        var i = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? html.Insert(i, tag) : html + tag;
    }
}
```

- [x] **Step 4: Uruchom testy i potwierdź, że przechodzą**

Run: `dotnet test tests/Generator.Web.Tests --filter PreviewInjectorTests -v q`
Expected: PASS, 4 testy.

- [x] **Step 5: Zamapuj endpoint**

`src/Generator.Web/Preview/PreviewEndpoint.cs`:

```csharp
namespace Generator.Web.Preview;

public static class PreviewEndpoint
{
    /// <param name="snapshotDir">(projectId, numerWersji) -> katalog snapshotu na dysku</param>
    public static void MapPreview(this WebApplication app, Func<string, int, string> snapshotDir)
    {
        app.MapGet("/preview/{projectId}/{version:int}/{**sciezka}",
            async (string projectId, int version, string? sciezka, HttpContext ctx) =>
        {
            var katalog = Path.GetFullPath(snapshotDir(projectId, version));
            var plik = Path.GetFullPath(Path.Combine(
                katalog, string.IsNullOrEmpty(sciezka) ? "index.html" : sciezka));

            // Wyjscie poza katalog snapshotu: sciezka podana przez klienta nie moze
            // czytac naszego dysku (../../ w URL-u).
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
                    PreviewInjector.Inject(await File.ReadAllTextAsync(plik), "/js/preview-click.js"));
                return;
            }

            ctx.Response.ContentType = plik.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                ? "text/css" : "application/octet-stream";
            await ctx.Response.SendFileAsync(plik);
        });
    }
}
```

W `src/Generator.Web/Program.cs` przed `app.Run();`:

```csharp
app.MapPreview((projectId, version) =>
    Path.Combine(builder.Configuration["Projects:Root"] ?? Path.GetTempPath(), projectId, $"v{version}"));
```

- [x] **Step 6: Zbuduj i potwierdź kompilację**

Run: `dotnet build src/Generator.Web`
Expected: Build succeeded.

- [x] **Step 7: Commit**

```bash
git add src/Generator.Web/Preview src/Generator.Web/Program.cs tests/Generator.Web.Tests/PreviewInjectorTests.cs
git commit -m "feat(web): podglad snapshotu ze wstrzykiwanym skryptem, snapshot nietkniety"
git push
```

---

### Task 5: Lista komentarzy — statusy i `note` przy uwadze

**Files:**
- Create: `src/Generator.Web/Components/CommentList.razor`
- Test: `tests/Generator.Web.Tests/CommentListTests.cs`

**Interfaces:**
- Consumes: `CommentDto`
- Produces: `class CommentList` z `[Parameter] IReadOnlyList<CommentDto> Comments`, `[Parameter] EventCallback<string> OnWithdraw`

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/CommentListTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

public class CommentListTests : BunitContext
{
    private static CommentDto C(string id, string status, string? note, string? anchor = "hero") =>
        new(id, anchor, $"uwaga {id}", "desktop", DateTimeOffset.UnixEpoch, status, note);

    [Fact]
    public void Note_jest_przy_komentarzu_nie_w_zbiorczym_podsumowaniu()
    {
        // Kontrakt 3.3 i sekcja 4 planu: klient musi wiedziec, ze zostal zrozumiany.
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "applied", "Powiekszylem telefon w naglowku.")]));

        var item = cut.Find("[data-test='komentarz-c1']");
        Assert.Contains("Powiekszylem telefon", item.TextContent);
    }

    [Fact]
    public void Rejected_pokazuje_uzasadnienie_i_nie_wyglada_jak_awaria()
    {
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "rejected", "Nie mam zdjecia, ktorym moglbym to zastapic.")]));

        var item = cut.Find("[data-test='komentarz-c1']");
        Assert.Contains("Nie mam zdjecia", item.TextContent);
        Assert.DoesNotContain("błąd", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("awaria", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wycofac_mozna_tylko_komentarz_open()
    {
        // Kontrakt 3.3: frontend umie wycofac, dopoki open. Statusu nie dotyka.
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "open", null), C("c2", "applied", "zrobione")]));

        Assert.NotNull(cut.Find("[data-test='wycofaj-c1']"));
        Assert.Empty(cut.FindAll("[data-test='wycofaj-c2']"));
    }

    [Fact]
    public void Komentarz_globalny_jest_opisany_jako_dotyczacy_calosci()
    {
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "open", null, anchor: null)]));

        Assert.Contains("cała strona", cut.Find("[data-test='komentarz-c1']").TextContent);
    }

    [Fact]
    public void Kolejnosc_wyswietlania_to_kolejnosc_dodawania()
    {
        // Kontrakt 3.2: przy sprzecznych uwagach obowiazuje OSTATNIA — klient musi
        // widziec, ktora to jest.
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "open", null), C("c2", "open", null)]));

        var markup = cut.Markup;
        Assert.True(markup.IndexOf("komentarz-c1", StringComparison.Ordinal)
                  < markup.IndexOf("komentarz-c2", StringComparison.Ordinal));
    }
}
```

- [x] **Step 2: Uruchom, potwierdź porażkę**

Run: `dotnet test tests/Generator.Web.Tests --filter CommentListTests -v q`
Expected: błąd kompilacji — `CommentList` nie istnieje.

- [x] **Step 3: Napisz komponent**

`src/Generator.Web/Components/CommentList.razor`:

```razor
@using Generator.Web.Contracts

<ol class="uwagi">
    @foreach (var c in Comments)
    {
        <li data-test="komentarz-@c.Id" class="uwaga @c.Status">
            <p class="gdzie">@(c.Anchor is null ? "cała strona" : $"blok: {c.Anchor}")</p>
            <p class="tresc">@c.Text</p>

            @if (c.Note is not null)
            {
                <p class="odpowiedz">@c.Note</p>
            }

            @if (c.Status == "open")
            {
                <button data-test="wycofaj-@c.Id" @onclick="() => OnWithdraw.InvokeAsync(c.Id)">
                    Wycofaj
                </button>
            }
        </li>
    }
</ol>

@code {
    [Parameter] public IReadOnlyList<CommentDto> Comments { get; set; } = [];
    [Parameter] public EventCallback<string> OnWithdraw { get; set; }
}
```

- [x] **Step 4: Uruchom testy**

Run: `dotnet test tests/Generator.Web.Tests --filter CommentListTests -v q`
Expected: 5 testów PASS.

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web/Components/CommentList.razor tests/Generator.Web.Tests/CommentListTests.cs
git commit -m "feat(web): lista uwag — note przy komentarzu, wycofanie tylko dla open"
git push
```

---

### Task 6: Stan zadania — gałęzie `retrying` i `halted`

**Files:**
- Create: `src/Generator.Web/Components/JobStatus.razor`
- Test: `tests/Generator.Web.Tests/JobStatusTests.cs`

**Interfaces:**
- Consumes: `JobView`, `JobFailureView`
- Produces: `class JobStatus` z `[Parameter] JobView? Job`, `[Parameter] EventCallback OnRetry`

Kontrakt §4.3: UI rozgałęzia się **po `handling`**, nie po przyczynie. Sens obu komunikatów: *nic nie przepadło*, a przy `halted` dodatkowo, że problem jest u nas i nie ma sensu klikać ponownie.

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/JobStatusTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

public class JobStatusTests : BunitContext
{
    [Fact]
    public void Running_pokazuje_ze_pracujemy_bez_szczegolow_technicznych()
    {
        var cut = Render<JobStatus>(ps => ps
            .Add(p => p.Job, new JobView("j1", "running", null)));

        Assert.Contains("pracuj", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // Bez DoesNotContain("claude"): sekcja 7 planu produktu WPROST przewiduje komunikat
        // "Claude pracuje nad Twoja strona". Nazwa modelu nie jest wyciekiem technicznym.
        foreach (var techniczne in new[] { "token", "exit", "stdout", "permission", "session" })
            Assert.DoesNotContain(techniczne, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retrying_wyglada_dla_klienta_jak_dalsza_praca()
    {
        // Kontrakt 4.3: powtorki sa NIEWIDOCZNE dla klienta.
        var cut = Render<JobStatus>(ps => ps
            .Add(p => p.Job, new JobView("j1", "failed", new JobFailureView("retrying", 1))));

        Assert.Contains("pracuj", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-test='ponow']"));
    }

    [Fact]
    public void Halted_mowi_ze_problem_jest_u_nas_i_nie_ma_sensu_klikac()
    {
        var cut = Render<JobStatus>(ps => ps
            .Add(p => p.Job, new JobView("j1", "failed", new JobFailureView("halted", 2))));

        var text = cut.Markup;
        Assert.Contains("u nas", text);
        Assert.Contains("nic nie przepadło", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-test='ponow']"));
    }

    [Fact]
    public void Nigdy_nie_pokazuje_przyczyny_technicznej()
    {
        // Kontrakt 4.3: cause nie istnieje w JobFailureView i nie ma sie skad wziac.
        var pola = typeof(JobFailureView).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Cause", pola);
    }

    [Fact]
    public void Brak_zadania_nic_nie_renderuje()
    {
        var cut = Render<JobStatus>(ps => ps.Add(p => p.Job, (JobView?)null));
        Assert.Empty(cut.Markup.Trim());
    }
}
```

- [x] **Step 2: Uruchom, potwierdź porażkę**

Run: `dotnet test tests/Generator.Web.Tests --filter JobStatusTests -v q`
Expected: błąd kompilacji — `JobStatus` nie istnieje.

- [x] **Step 3: Napisz komponent**

`src/Generator.Web/Components/JobStatus.razor`:

```razor
@using Generator.Web.Contracts

@if (Job is not null)
{
    @if (Job.Status is "queued" or "running" || Job.Failure?.Handling == "retrying")
    {
        @* Kontrakt 4.3: powtórka jest dla klienta dalszą pracą, nie awarią. *@
        <p aria-live="polite" data-test="pracuje">Claude pracuje nad Twoją stroną…</p>
    }
    else if (Job.Failure?.Handling == "halted")
    {
        <p role="alert" data-test="zatrzymane">
            Coś się u nas zacięło i nie udało się wprowadzić poprawek —
            <strong>nic nie przepadło</strong>: Twoja strona i uwagi są na miejscu.
            Zajmiemy się tym, klikanie ponownie nic nie zmieni.
        </p>
    }
}

@code {
    [Parameter] public JobView? Job { get; set; }
    [Parameter] public EventCallback OnRetry { get; set; }
}
```

- [x] **Step 4: Uruchom testy**

Run: `dotnet test tests/Generator.Web.Tests --filter JobStatusTests -v q`
Expected: 5 testów PASS.

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web/Components/JobStatus.razor tests/Generator.Web.Tests/JobStatusTests.cs
git commit -m "feat(web): stan zadania — retrying jako praca, halted jako problem u nas"
git push
```

---

### Task 7: Edytor — podgląd, tryb komentowania, runda

**Files:**
- Create: `src/Generator.Web/Components/Pages/Editor.razor`
- Test: `tests/Generator.Web.Tests/EditorPageTests.cs`

**Interfaces:**
- Consumes: `IProjectApi`, `CommentBridge`, `CommentList`, `JobStatus`
- Produces: strona `/editor/{projectId}`

- [x] **Step 1: Napisz failujące testy**

`tests/Generator.Web.Tests/EditorPageTests.cs`:

```csharp
using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

public class EditorPageTests : BunitContext
{
    private async Task<(MockProjectApi api, string id)> Setup(MockJobOutcome outcome = MockJobOutcome.Success)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, NextJobOutcome = outcome };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");
        await api.ApplyCommentsAsync(p.Id, 1, []);
        return (api, p.Id);
    }

    [Fact]
    public async Task Klient_nie_ma_zadnego_pola_do_edycji_strony()
    {
        // Sekcja 1 planu: w edytorze klient NIE poprawia sam.
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.Empty(cut.FindAll("[contenteditable='true']"));
        Assert.Empty(cut.FindAll("input[type='color']"));
        Assert.Empty(cut.FindAll("select[name='font']"));
    }

    [Fact]
    public async Task Pokazuje_licznik_rund_ale_nigdy_budzetu()
    {
        // Kontrakt 4.1: budzet widzimy tylko my.
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.Contains("poprawk", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("$", cut.Markup);
        Assert.DoesNotContain("budżet", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usd", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kontrakt 4.4 — GWARANCJA, nie konsekwencja. Skladaja sie na nia trzy niezalezne
    /// mechanizmy: snapshot zostawia poprzednia wersje nietknieta, status komentarza
    /// zmienia tylko silnik, licznik rund podbija sie tylko przy udanej rundzie.
    /// Dlatego jeden test i trzy asercje razem: gdy ktos kiedys zdecyduje, ze "powtorka
    /// zjada runde dla ograniczenia kosztu", ma zlamac te regule wprost, a nie rozmyc
    /// ja po cichu w trzech osobnych plikach.
    /// </summary>
    [Fact]
    public async Task Nieudana_runda_nie_zabiera_klientowi_niczego()
    {
        var (api, id) = await Setup(MockJobOutcome.Halted);
        var przed = await api.GetAsync(id);
        var wersjaPrzed = przed.Versions[^1].Number;
        var rundyPrzed = przed.RoundsUsed;
        api.SeedComments(id, wersjaPrzed, "telefon za mało widoczny", "cennik nad opiniami");

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));

        await cut.Find("[data-test='popraw']").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("u nas", cut.Markup));

        var po = await api.GetAsync(id);
        // 1. podglad nietkniety - klient nadal oglada te sama wersje
        Assert.Equal(wersjaPrzed, po.Versions[^1].Number);
        Assert.Contains($"/preview/{id}/{wersjaPrzed}", cut.Find("iframe").GetAttribute("src")!);
        // 2. komentarze zostaja, i zostaja open - silnik nie wydal raportu per commentId,
        //    wiec nikt nie mial prawa zmienic statusu (kontrakt 3.3)
        var uwagi = await api.GetCommentsAsync(id, wersjaPrzed);
        Assert.Equal(2, uwagi.Count);
        Assert.All(uwagi, k => Assert.Equal("open", k.Status));
        // 3. licznik rund sie nie ruszyl (kontrakt 4.1: budzet tak, rundy nie)
        Assert.Equal(rundyPrzed, po.RoundsUsed);
    }

    [Fact]
    public async Task Osierocone_kotwice_sa_widoczne_nigdy_wyciszane()
    {
        // Kontrakt 3.4: niepusta orphanedAnchors[] JEST widoczna w UI.
        var (api, id) = await Setup();
        var p = await api.GetAsync(id);
        api.ForceOrphaned(id, p.Versions[^1].Number, ["cennik"]);

        var cut = Render<Editor>(ps => ps.Add(x => x.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='osierocone']")));
        Assert.Contains("cennik", cut.Find("[data-test='osierocone']").TextContent);
    }

    [Fact]
    public async Task Frozen_zostawia_podglad_ale_blokuje_nowa_runde()
    {
        // Kontrakt 4.5: zamrozenie, nie odciecie.
        var (api, id) = await Setup();
        api.ForceFrozen(id);

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.NotNull(cut.Find("[data-test='zamrozone']"));
        Assert.True(cut.Find("[data-test='popraw']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Przycisk_popraw_jest_nieaktywny_bez_uwag()
    {
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='popraw']")));
        Assert.True(cut.Find("[data-test='popraw']").HasAttribute("disabled"));
    }
}
```

- [x] **Step 2: Dodaj `ForceOrphaned` i `SeedComments` do mocka, uruchom testy**

W `MockProjectApi`:

```csharp
public void ForceOrphaned(string id, int version, IReadOnlyList<string> anchors)
{
    var p = _projects[id];
    _projects[id] = p with
    {
        Versions = [.. p.Versions.Select(v =>
            v.Number == version ? v with { OrphanedAnchors = anchors } : v)],
    };
}

/// <summary>
/// Zasiewa uwagi w statusie open, bez przechodzenia przez klik w iframe.
/// Test gwarancji z 4.4 musi miec co stracic - bez uwag przycisk "Popraw to"
/// jest disabled i test przeszedlby prozno.
/// </summary>
public void SeedComments(string id, int version, params string[] teksty)
{
    _comments[(id, version)] = [.. teksty.Select((t, i) => new CommentDto(
        Id: $"seed-{i}",
        Anchor: "hero",
        Text: t,
        Viewport: "desktop",
        CreatedAt: DateTimeOffset.UnixEpoch,
        Status: "open",
        Note: null))];
}
```

Run: `dotnet test tests/Generator.Web.Tests --filter EditorPageTests -v q`
Expected: błąd kompilacji — `Editor` nie istnieje.

- [x] **Step 3: Napisz stronę**

`src/Generator.Web/Components/Pages/Editor.razor`:

```razor
@page "/editor/{ProjectId}"
@inject IProjectApi Api
@using Generator.Web.Contracts

<h1>Twoja strona</h1>

@if (_project is null)
{
    <p>Wczytuję…</p>
}
else
{
    var wersja = _project.Versions.Count > 0 ? _project.Versions[^1] : null;

    @if (_project.Status == "frozen")
    {
        <p role="alert" data-test="zamrozone">
            Wykorzystaliście wszystkie poprawki w tym pakiecie. Strona i podgląd zostają —
            nowych poprawek na razie nie wprowadzamy.
        </p>
    }

    <p data-test="licznik">
        Wykorzystane poprawki: @_project.RoundsUsed z @_project.RoundsLimit
    </p>

    @if (wersja is not null)
    {
        <iframe title="Podgląd Twojej strony" src="@wersja.PreviewUrl"></iframe>

        @if (wersja.OrphanedAnchors.Count > 0)
        {
            <p role="alert" data-test="osierocone">
                Uwaga: po ostatniej poprawce zniknęły bloki, do których przypisane były
                Twoje uwagi (@string.Join(", ", wersja.OrphanedAnchors)). Sprawdź, czy strona
                nadal zawiera to, co miała.
            </p>
        }
    }

    <CommentBridge OnCommentDrafted="Draft" />

    @if (_draft is not null)
    {
        <div data-test="nowa-uwaga">
            <label for="tresc">
                @(_draft.Anchor is null ? "Uwaga do całej strony" : $"Uwaga do bloku: {_draft.Anchor}")
            </label>
            <textarea id="tresc" rows="3" @bind="_draftText"
                      placeholder="Napisz swoimi słowami, np. telefon za mało widoczny"></textarea>
            <button data-test="dodaj" @onclick="AddDraft">Dodaj uwagę</button>
        </div>
    }

    <CommentList Comments="_comments" OnWithdraw="Withdraw" />

    <button data-test="popraw" disabled="@(_comments.Count == 0 || _project.Status == "frozen" || _job?.Status == "running")"
            @onclick="Apply">
        Popraw to
    </button>

    <JobStatus Job="_job" />
}

@code {
    [Parameter] public string ProjectId { get; set; } = string.Empty;

    private ProjectView? _project;
    private List<CommentDto> _comments = [];
    private CommentDto? _draft;
    private string _draftText = string.Empty;
    private JobView? _job;

    protected override async Task OnInitializedAsync() => await Reload();

    private async Task Reload()
    {
        _project = await Api.GetAsync(ProjectId);
        var wersja = _project.Versions.Count > 0 ? _project.Versions[^1].Number : 1;
        _comments = [.. await Api.GetCommentsAsync(ProjectId, wersja)];
    }

    private void Draft(CommentDto comment)
    {
        _draft = comment;
        _draftText = string.Empty;
    }

    private void AddDraft()
    {
        if (_draft is null || string.IsNullOrWhiteSpace(_draftText)) return;

        // Kolejnosc dodawania ma znaczenie — kontrakt 3.2, przy sprzecznych wygrywa ostatnia.
        _comments.Add(_draft with { Text = _draftText.Trim() });
        _draft = null;
        _draftText = string.Empty;
    }

    /// Kontrakt 3.3: frontend umie wycofac, dopoki open. Statusu nie zmienia.
    private void Withdraw(string commentId) =>
        _comments.RemoveAll(c => c.Id == commentId && c.Status == "open");

    private async Task Apply()
    {
        var wersja = _project!.Versions.Count > 0 ? _project.Versions[^1].Number : 1;
        var jobId = await Api.ApplyCommentsAsync(ProjectId, wersja, _comments);
        _job = await Api.GetJobAsync(jobId);

        // Kontrakt 4.4: przy nieudanej rundzie komentarze zostaja open i nic nie przepada.
        if (_job.Status == "succeeded") await Reload();
    }
}
```

- [x] **Step 4: Uruchom cały zestaw**

Run: `dotnet test tests/Generator.Web.Tests -v q`
Expected: wszystkie testy PASS — 32 z Tasków 1–7 (6+4+3+4+5+5+5).

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web tests/Generator.Web.Tests
git commit -m "feat(web): edytor — podglad, tryb komentowania, runda i osierocone kotwice"
git push
```

---

### Task 8: Ręczne przejście całej ścieżki

> **Wynik przejścia 2026-09-04 (Przemek).** To zadanie zarobiło na siebie w pierwszej
> minucie: aplikacja nie miała NIGDZIE `@rendermode`, więc chodziła w statycznym SSR
> i **każdy `@onclick` był martwy** — przy 44 zielonych testach bUnit, bo bUnit zawsze
> renderuje interaktywnie i tej klasy błędu nie widzi strukturalnie. Naprawione:
> `<Routes @rendermode="InteractiveServer" />` w `App.razor`, plus `comment-bridge.js`
> dociągany tagiem `<script>` (nikt go wcześniej nie ładował) i wspólny katalog
> snapshotów dla mocka i `/preview`.
>
> Sprawdzone żądaniami na działającej aplikacji: strona startowa renderuje kreator;
> `/js/preview-click.js` serwowany (200, `text/javascript`); `/preview/{id}/{n}/` zwraca
> snapshot z doklejonym `<script>` i nietkniętą treścią klienta; wyjście poza katalog
> snapshotu (`../../`) daje 404; po poprawce obwód SignalR łączy się (WebSocket w konsoli).
>
> **Przejście domknięte 2026-09-04 (druga próba).** Cała ścieżka klienta przeklikana
> w przeglądarce od pustej strony do drugiej wersji: kreator → 3 propozycje → wybór →
> edytor z podglądem → klik w blok w iframe → uwaga → runda → podgląd przeskakuje na
> `/preview/{id}/2`, licznik pokazuje „1 z 15", konsola bez błędów.
>
> **Drugi błąd, którego testy nie mogły złapać:** `ChooseProposalAsync` w mocku ustawiał
> tylko status i **nie tworzył wersji 1**, więc edytor otwierał się bez iframe'a i klient
> nie miał w co kliknąć. Testy komponentów tego nie widziały, bo `Setup()` dorabiał sobie
> wersję ręcznym `ApplyCommentsAsync`. Naprawione, plus test regresji
> `Wybor_propozycji_tworzy_wersje_1_z_podgladem` (45 testów).
>
> Pierwszy klik po współrzędnych dał „Uwaga do całej strony" i wyglądał na błąd kotwicy —
> okazał się poprawny: klik minął nagłówek i trafił w tło, a `closest('[data-cmt-id]')`
> słusznie zwrócił `null`. Klik dokładnie w `<h1 data-cmt-id="hero">` daje
> „Uwaga do bloku: hero", czyli łańcuch iframe → `postMessage` → shim → `[JSInvokable]`
> → UI działa end-to-end z trzema bramkami bezpieczeństwa włączonymi.
>
> **Gałęzie `failed` przeklikane 2026-09-04.** Nie miały wyzwalacza w UI, więc dodałem
> `GET /dev/wynik-rundy/{success|retrying|halted}` — **tylko pod `IsDevelopment()`**,
> bo w produkcji nie ma mocka i nikt z zewnątrz nie może przestawiać wyniku rundy.
>
> `retrying`: „Claude pracuje nad Twoją stroną…", licznik `0 z 15` nieruszony, wersja 1,
> uwaga nadal `open` i wycofywalna. Po odświeżeniu i rundzie `success` uwaga ocalała,
> licznik poszedł na `1 z 15`, raport „Zrobione: …" na miejscu.
> `halted`: komunikat „nic nie przepadło", licznik nieruszony, wersja nietknięta, uwaga
> `open`. W żadnej gałęzi nie wyciekło ani `halted`/`retrying`, ani budżet (kontrakt 4.3).
>
> **Trzeci błąd znaleziony przeklikaniem, nie testem:** UI pisał „Claude pracuje nad
> Twoją stroną…", a przycisk „Popraw to" był w tym czasie **aktywny** — klient
> wystartowałby drugie zadanie w trakcie powtórki. Przy `halted` jeszcze gorzej: tekst
> mówi „klikanie ponownie nic nie zmieni", a przycisk zapraszał do klikania. Przyczyna:
> `JobStatus` i `Editor` liczyły „czy trwa" **osobno**, a warunek `disabled` patrzył
> tylko na `Status == "running"` i nie widział `Failure.Handling`. Naprawione jednym
> miejscem prawdy (`JobViewExtensions`: `WTokuDlaKlienta`, `Zatrzymane`,
> `BlokujeNowaRunde`) — nie dwiema poprawkami osobno, bo rozjechały się właśnie dlatego,
> że były dwa. Test: `Nieudana_runda_nie_zaprasza_do_ponownego_klikania` (Theory na obu
> gałęziach). 50 testów.
>
> **Ograniczenie zdjęte 2026-09-04 (polling).** Edytor pobierał stan zadania raz, więc
> przy `retrying` klient zostawał z „pracuje" i zablokowanym przyciskiem **do odświeżenia
> strony**. Teraz `Editor` dopytuje o zadanie w tle (`PeriodicTimer`, domyślnie 2 s;
> `PollInterval` jest parametrem, bo testy nie mogą czekać sekund) i pętla **ustaje na
> stanie końcowym oraz przy zdjęciu komponentu** — inaczej każdy klient zostawiałby po
> sobie timer bijący w API. `Reload` wołamy tylko gdy stan naprawdę się zmienił
> (rekordy, równość po wartości), więc powtórka nie generuje ruchu na próżno.
>
> Żeby dało się to w ogóle zobaczyć, mock musiał **umieć dokończyć powtórkę**: dziś
> `retrying` wisiało w nieskończoność. Stąd `RetryResolvesAfter` — `null` (domyślnie)
> zostawia zadanie w `retrying`, bo testy komunikatu i blokady potrzebują stanu, który
> sam się nie zmienia; aplikacja dev ustawia 4 s. Ścieżka udanej rundy jest wspólna dla
> `success` i dla udanej powtórki (`Udalo`) — dwa razy to samo osobno rozjechałoby się
> tak samo jak wcześniej „czy trwa".
>
> Przeklikane: powtórka trwa („pracuje", licznik `0 z 15`, wersja 1), a po ~4 s wynik
> **dochodzi sam** — `pracuje` znika, iframe przeskakuje na `/2`, licznik `1 z 15`,
> raport „Zrobione: …" na miejscu, URL nietknięty. `halted` po 8 s nadal stoi na
> komunikacie i nie startuje pollingu (stan końcowy). Wyjście ze strony **w trakcie**
> powtórki nie zostawiło w logu serwera ani jednej linii — a to najprostszy sposób
> wywołania renderu w zdjęty komponent.
>
> Że test pollingu naprawdę broni pollingu, sprawdziłem wyłączając wywołanie `Dopytuj`:
> oba testy padają. Czerwony na błędzie kompilacji nie jest dowodem.
>
> Wyścig, którego log nie pokazał, ale który istnieje: klient może zamknąć kartę między
> `GetJobAsync` a renderem. Domknięte sprawdzeniem tokenu po zapytaniu i przechwyceniem
> `ObjectDisposedException` — wyjątek w wątku tła nie ma kto złapać wyżej.
>
> **Obserwacja produktowa, nie błąd:** po udanej rundzie lista uwag jest pusta, bo uwagi
> są zakotwiczone w wersji, a klient patrzy już na następną. Statusy `applied` z notatką
> „Zrobione: …" nigdy nie trafiają klientowi przed oczy. Do rozstrzygnięcia z Sebastianem:
> czy klient ma widzieć potwierdzenie, co zostało zrobione z jego uwagami.

Jedyne zadanie bez testu jednostkowego: sprawdzenie, czy to **da się przejść jako klient**, a nie czy komponenty działają.

**Files:**
- Modify: `src/Generator.Web/Program.cs` (rejestracja mocka, statyczne pliki JS)

- [x] **Step 1: Uruchom aplikację**

```bash
dotnet run --project src/Generator.Web
```

- [x] **Step 2: Przejdź ścieżkę klienta**

1. `/` → „Nie mam strony", wpisz opis fryzjera → **Dalej**
2. Trzy podglądy → **Ta mi się podoba**
3. W podglądzie kliknij w nagłówek → pojawia się „Uwaga do bloku: hero"
4. Napisz „telefon za mało widoczny" → **Dodaj uwagę**
5. Kliknij gdzieś poza blokiem → „Uwaga do całej strony", dodaj drugą uwagę
6. **Popraw to** → „Claude pracuje nad Twoją stroną…" → po chwili `note` przy każdej uwadze

- [x] **Step 3: Sprawdź gałęzie awarii**

W `Program.cs` tymczasowo `NextJobOutcome = MockJobOutcome.Halted`, przejdź krok 6 ponownie.
Oczekiwane: komunikat „nic nie przepadło", licznik poprawek **bez zmian**, uwagi nadal `open`,
brak przycisku „ponów". Potem `MockJobOutcome.Retrying` — klient widzi dalszą pracę, nie awarię.

- [x] **Step 4: Zanotuj, co nie zagrało**

Rzeczy do rozstrzygnięcia z Sebastianem (kontrakt, nie UI) wpisz do `docs/api-contract.md`
jako pytania — nie zgaduj po swojej stronie mostka.

- [x] **Step 5: Commit**

```bash
git add src/Generator.Web/Program.cs
git commit -m "chore(web): rejestracja mocka i statycznych plikow JS"
git push
```

---

## Po wykonaniu

Frontend chodzi na mocku i całą ścieżkę klienta da się przejść. Co dalej:

- **Plan B (HTTP API, Sebastian)** — po nim `MockProjectApi` zostaje w projekcie testowym, a produkcja dostaje klienta HTTP za tym samym `IProjectApi`. Żadna strona się nie zmienia.
- **Wygląd** — ten plan nie dotyka stylowania poza minimum. Generator produkuje strony klientom; jak ma wyglądać **nasza** aplikacja, to osobna rozmowa.
- **`preview-click.js`** — wstrzykiwanie tego skryptu do podglądu wersji robi backend przy serwowaniu `/preview/...`. To granica, którą trzeba domknąć z Sebastianem w Planie B.

---

## Dodane po planie: historia wersji, powrót i podświetlanie zmian (2026-09-05)

Poza planem, na prośbę Przemka. Dwie decyzje produktowe jego, oba warianty trudniejsze
z przedstawionych: **powrót przestawia wskaźnik** (nie tworzy kopii) i **zmiany
pokazujemy podświetleniem bloków** (nie diffem kodu). Kontrakt: sekcja 5.

**Kolejność, która się obroniła.** Najpierw czysty refaktor: `ProjectView.CurrentVersion`
i `VersionView.BasedOn`, migracja wszystkich `Versions[^1]` przy jeszcze prawdziwym
niezmienniku `CurrentVersion == Versions[^1].Number`, 52 testy nietknięte i zielone.
Dopiero na tej siatce rollback. Bez tego kroku każda pomyłka wyglądałaby jak błąd nowej
funkcji, a nie jak błąd migracji.

**Rzecz, która unieważniłaby całą funkcję.** Baza porównania to `BasedOn`, nie `numer-1`.
Po powrocie do v1 kolejna runda tworzy v3, której rodzicem jest **v1, nie v2**.
Implementacja po `numer-1` przechodzi każdy przebieg liniowy — czyli każdy, jaki zwykle
testujemy — i kłamie dokładnie tam, gdzie powrót ma sens. Dwa testy to rozstrzygają;
sprawdziłem podmianą, że oba padają przy błędnej bazie. Jeden z nich musiał sięgnąć do
**treści snapshotu**, bo sama lista zmian przy błędnej bazie wychodziła przypadkiem taka
sama (kumulacja wyróżników po rodzicu maskowała różnicę). Test, który obie implementacje
przepuszcza, niczego nie broni — mimo trafnej nazwy.

**Porównujemy znormalizowany `outerHtml`, nie tekst.** „Powiększ telefon" zmienia `style`
i **zostawia tekst identyczny** — porównanie po `TextContent` zgłosiłoby „nic się nie
zmieniło" w najczęstszym przypadku tego produktu. Jest na to test i sprawdziłem
podmianą, że pada przy porównaniu po tekście. Samo przeformatowanie zmianą nie jest,
inaczej podświetlałaby się cała strona i podświetlenie nie znaczyłoby nic. Parser to
AngleSharp, nie regex — treść pisze model, a regex na HTML gnije cicho.

**Podświetlenie włącza flaga `?zmienione=true`, nie lista kotwic w URL-u.** Zbiór
zmienionych bloków zna serwer; przyjmowanie go z query string dawałoby drugie źródło
prawdy i wpuszczało treść strony wprost do selektora CSS. Każda kotwica przechodzi
`Kotwica.Poprawna` — format 3.1 wyciągnięty z `CommentBridge` do jednego miejsca, bo
teraz jest dwóch odbiorców. Na próby wstrzyknięcia jest `Theory`.

**Powrót nie może być pułapką bez wyjścia.** Wersje nowsze od oglądanej są *porzucone*,
nie usunięte, więc historia pokazuje drogę powrotną także do nich — jest na to test.
Powrót jest zablokowany, gdy runda trwa (kończąca się runda przestawiłaby wskaźnik i
cicho unieważniła powrót) — tym samym predykatem `BlokujeNowaRunde`, nie czwartym
miejscem liczącym „czy coś się dzieje".

### Błąd, którego testy nie widziały — czwarty raz przeklikanie zarobiło na siebie

v3 podświetlał **dwa** bloki (`hero` i `kontakt`), choć powstał z v1, gdzie `hero` nie był
poprawiany. Przyczyna nie leżała w liczeniu rodzica: po powrocie do v1 `Reload` wciąga
uwagi tej wersji — **razem z zastosowanymi** — a runda wysyłała całą listę. Model
„poprawiał" powtórnie to, co już zrobione, a **klient płacił rundę za pracę wykonaną**.
Testy tego nie widziały, bo wołały API z czystą listą; UI z listą po powrocie nigdy nie
przeszło przez test. Runda wysyła teraz tylko `open`.

Przy okazji: atrapa **nadpisywała** uwagi wersji, więc raport „co zrobiliśmy" z
wcześniejszej rundy tej samej wersji przepadałby. Scalanie jest w jednym miejscu
(`Scal`) — nie wysłać czegoś to jedno, a skasować to drugie.

**Przeklikane:** runda na `hero` → powrót do v1 (podgląd wraca, v2 opisana jako nowsza,
droga powrotna do niej widoczna) → runda na `kontakt` z v1 → v3 podświetla **tylko
`kontakt`**, licznik `2 z 15` (powrót rundy nie zabrał), obramowanie liczone przez
`getComputedStyle` **wewnątrz iframe'a**, nie po samym atrybucie `src`. Wyłącznik zdejmuje
podświetlenie i klikanie w bloki nadal działa.

**Znalezione po drodze, poza zakresem:** wejście na link projektu, którego serwer nie zna
(u nas: restart aplikacji, bo atrapa trzyma stan w pamięci; w produkcji: usunięty lub
błędny token), daje **500 z wyjątkiem**, a nie ludzki komunikat. Przy modelu „bez kont,
token w linku" (decyzja 3) to normalna sytuacja klienta, nie awaria.
