using System.Collections.Concurrent;
using Generator.Web.Contracts;

namespace Generator.Web.Mock;

public enum MockJobOutcome { Success, Retrying, Halted }

/// <summary>
/// Backend w pamięci, żeby frontend nie czekał na Plan B.
/// Odwzorowuje te zachowania kontraktu, które widzi UI: zadania zamiast odpowiedzi
/// synchronicznych, asymetrię liczników (4.1), gwarancję 4.4, frozen (4.5).
/// </summary>
public class MockProjectApi : IProjectApi
{
    private readonly ConcurrentDictionary<string, ProjectView> _projects = new();
    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly ConcurrentDictionary<(string, int), List<CommentDto>> _comments = new();

    public MockJobOutcome NextJobOutcome { get; set; } = MockJobOutcome.Success;
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Po jakim czasie powtorka (`retrying`) konczy sie sukcesem. `null` = nigdy,
    /// czyli zadanie zostaje w `retrying` — taki stan potrzebuja testy, ktore sprawdzaja
    /// sam komunikat i blokade przycisku.
    /// </summary>
    public TimeSpan? RetryResolvesAfter { get; set; }

    /// <summary>
    /// Katalog, w którym mock zapisuje snapshoty wersji. Musi być ten sam, z którego
    /// czyta `/preview` — endpoint serwuje PLIKI, nie metadane, więc bez zapisu cała
    /// ścieżka klienta kończy się pustym iframe'em.
    /// </summary>
    public string SnapshotRoot { get; set; } = Path.GetTempPath();

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
    /// Test gwarancji z 4.4 musi mieć co stracić — bez uwag przycisk „Popraw to"
    /// jest disabled i test przeszedłby próżno.
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

    /// <summary>
    /// Wybor propozycji MUSI utworzyc wersje 1 — to ona jest pierwsza strona klienta.
    /// Bez tego edytor otwiera sie bez iframe'a i klient nie ma w co kliknac. Testy
    /// komponentow tego nie widzialy, bo dorabialy sobie wersje recznym wywolaniem
    /// ApplyCommentsAsync w Setup().
    /// </summary>
    public async Task ChooseProposalAsync(string id, string proposalId)
    {
        var project = _projects[id];
        if (project.Versions.Count > 0) return;

        await ZapiszSnapshot(id, 1, []);
        _projects[id] = project with
        {
            Status = "active",
            Versions = [new VersionView(1, $"/preview/{id}/1", [], BasedOn: null)],
            CurrentVersion = 1,
        };
    }

    public async Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        if (_projects[id].Status == "frozen")
            throw new InvalidOperationException("projekt zamrozony (409)");   // kontrakt 4.5

        _comments[(id, version)] = [.. comments];

        var jobId = Guid.NewGuid().ToString();
        _jobs[jobId] = new JobView(jobId, "running", null);
        await Task.Delay(SimulatedDelay);

        // Projekt czytamy PO opoznieniu: przez te 2 sekundy stan mogl sie zmienic
        // (np. ForceFrozen z testu), a zapis ze starej kopii cicho by to skasowal.
        var project = _projects[id];

        switch (NextJobOutcome)
        {
            case MockJobOutcome.Success:
                await Udalo(jobId, id, version, comments);
                break;

            // Kontrakt 4.4: nieudana runda NIE zuzywa rundy i NIE rusza statusow.
            case MockJobOutcome.Retrying:
                _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView("retrying", 1));

                // Powtorka, ktora nigdy sie nie konczy, nie jest powtorka. Domyslnie
                // (null) zadanie zostaje w `retrying` — testy potrzebuja stanu, ktory
                // sam sie nie zmienia. Aplikacja dev ustawia opoznienie, zeby dalo sie
                // zobaczyc, ze polling podnosi wynik bez odswiezania strony.
                if (RetryResolvesAfter is { } opoznienie)
                    _ = DokonczPowtorke(jobId, id, version, comments, opoznienie);
                break;

            case MockJobOutcome.Halted:
                _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView("halted", 2));
                break;
        }

        return jobId;
    }

    /// <summary>
    /// Udana runda: raport per komentarz, nowy snapshot, podbity licznik. Wspolna dla
    /// wyniku `success` i dla powtorki, ktora sie w koncu udala — inaczej dwie sciezki
    /// liczylyby to samo osobno i rozjechalyby sie, jak wczesniej „czy trwa".
    /// </summary>
    private async Task Udalo(string jobId, string id, int version, IReadOnlyList<CommentDto> comments)
    {
        _comments[(id, version)] =
        [
            .. comments.Select(c => c with
            {
                Status = "applied",
                Note = $"Zrobione: {c.Text}",
            }),
        ];

        var project = _projects[id];

        // Numer z maksimum, nie z liczby wersji: po powrocie do starszej wersji liczba
        // elementow przestaje odpowiadac najwyzszemu numerowi i doszloby do kolizji.
        var numer = project.Versions.Count == 0 ? 1 : project.Versions.Max(v => v.Number) + 1;

        // Rodzicem jest wersja, ktora klient WIDZIAL, gdy zglaszal uwagi — po rollbacku
        // to nie jest ostatnia w historii.
        var rodzic = project.CurrentVersion > 0 ? project.CurrentVersion : (int?)null;

        await ZapiszSnapshot(id, numer, comments);
        _projects[id] = project with
        {
            RoundsUsed = project.RoundsUsed + 1,
            Versions = [.. project.Versions,
                new VersionView(numer, $"/preview/{id}/{numer}", [], rodzic)],
            CurrentVersion = numer,
        };
        _jobs[jobId] = new JobView(jobId, "succeeded", null);
    }

    /// <summary>
    /// Silnik powtarza w tle i po chwili ma wynik. Kontrakt 4.1: powtorka zjada budzet,
    /// nie runde — tu podbija sie tylko licznik rund tej JEDNEJ udanej proby.
    /// </summary>
    private async Task DokonczPowtorke(
        string jobId, string id, int version, IReadOnlyList<CommentDto> comments, TimeSpan po)
    {
        await Task.Delay(po);
        if (_projects[id].Status == "frozen") return;   // kontrakt 4.5
        await Udalo(jobId, id, version, comments);
    }

    /// <summary>
    /// Udaje to, co robi VersionStore z Planu A: kladzie na dysk katalog wersji z gotowa
    /// strona. Bloki maja data-cmt-id opisujace ROLE, nigdy pozycje (kontrakt 3.1) —
    /// "oferta-1" jest bledem, bo wstawienie elementu przesuwa numery i komentarze
    /// wskazuja w cudze bloki. Skryptu podgladu tu NIE MA: dokleja go PreviewInjector
    /// przy serwowaniu, zeby ZIP klienta zostal czysty.
    /// </summary>
    private async Task ZapiszSnapshot(string id, int numer, IReadOnlyList<CommentDto> uwagi)
    {
        var katalog = Path.Combine(SnapshotRoot, id, $"v{numer}");
        Directory.CreateDirectory(katalog);

        var uwzglednione = uwagi.Count == 0
            ? "<!-- wersja poczatkowa -->"
            : string.Join("\n    ", uwagi.Select(u =>
                $"<!-- uwzgledniono: {System.Net.WebUtility.HtmlEncode(u.Text)} -->"));

        var html = $"""
        <html lang="pl">
        <head><meta charset="utf-8"><title>Fryzjer</title><link rel="stylesheet" href="styl.css"></head>
        <body>
            {uwzglednione}
            <h1 data-cmt-id="hero">Fryzjer Anna</h1>
            <section data-cmt-id="oferta-strzyzenie"><h2>Strzyżenie</h2><p>60 zł</p></section>
            <section data-cmt-id="oferta-broda"><h2>Broda</h2><p>40 zł</p></section>
            <ul data-cmt-id="cennik"><li>Strzyżenie 60 zł</li><li>Broda 40 zł</li></ul>
            <footer data-cmt-id="kontakt">tel. 600 100 200, ul. Krakowska 5</footer>
        </body>
        </html>
        """;

        await File.WriteAllTextAsync(Path.Combine(katalog, "index.html"), html);
        await File.WriteAllTextAsync(Path.Combine(katalog, "styl.css"),
            "body{font-family:system-ui;max-width:38rem;margin:2rem auto;line-height:1.5}");
    }

    public Task<JobView> GetJobAsync(string jobId) => Task.FromResult(_jobs[jobId]);

    public Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version) =>
        Task.FromResult<IReadOnlyList<CommentDto>>(
            _comments.TryGetValue((id, version), out var list) ? list : []);
}
