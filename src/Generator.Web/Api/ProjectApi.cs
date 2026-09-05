using Generator.Engine.Jobs;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Web.Contracts;

namespace Generator.Web.Api;

/// Kontrakt 1 w procesie. Blazor Server wola to bezposrednio (postep idzie
/// obwodem SignalR), a ApiEndpoints jest cienka warstwa HTTP nad tym samym.
public class ProjectApi(ProjectPaths paths, JobQueue queue) : IProjectApi
{
    public Task<ProjectView> CreateAsync(string source, string description)
    {
        var kind = source.Equals("url", StringComparison.OrdinalIgnoreCase)
            ? SourceKind.Url : SourceKind.Idea;

        // ProjectStore.Create nadaje wlasny GUID, a katalog musimy znac wczesniej.
        // Nadpisujemy Id na nazwe katalogu: inaczej meta.Id i folder rozjezdzaja
        // sie i GetAsync szuka projektu tam, gdzie go nie ma.
        var id = Guid.NewGuid().ToString();
        var store = paths.Store(id);
        var meta = store.Create(kind) with
        {
            Id = id,
            Description = description,
            SourceUrl = kind == SourceKind.Url ? description : null,
        };
        store.Save(meta);
        return Task.FromResult(ToView(meta));
    }

    public Task<ProjectView> GetAsync(string id) => Task.FromResult(ToView(Wczytaj(id)));

    public Task<string> RequestProposalsAsync(string id)
    {
        var meta = Wczytaj(id);
        if (meta.Status == ProjectStatus.Frozen) throw new ProjectFrozenException(id);

        return Task.FromResult(queue.Enqueue(id, async ct =>
        {
            var wynik = await paths.Engine(id).RunProposalsAsync(paths.Store(id).Load(), ct);
            if (!wynik.Succeeded) throw new InvalidOperationException(wynik.Failure!.Cause);
            ZapiszSzkice(id, wynik.Proposals);
        }));
    }

    public Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id)
    {
        var katalog = Path.Combine(paths.Dir(id), "proposals");
        if (!Directory.Exists(katalog)) return Task.FromResult<IReadOnlyList<ProposalView>>([]);

        var lista = SzkiceZDysku(katalog);
        return Task.FromResult<IReadOnlyList<ProposalView>>(lista);
    }

    /// Dozwolone identyfikatory propozycji. Ta sama trojka co `GenerationService.SketchIds`
    /// — swiadome powtorzenie, bo to granica zaufania: silnik ma swoja biala liste jako
    /// OSTATNIA linie obrony, a ta jest PIERWSZA. Wspolna stala kusi, ale zrobilaby
    /// z dwoch niezaleznych warstw jedna.
    private static readonly string[] DozwoloneId = ["a", "b", "c"];

    public Task ChooseProposalAsync(string id, string proposalId)
    {
        var store = paths.Store(id);
        var meta = store.Load();

        // Walidacja przy ZAPISIE, nie przy odczycie (watpliwosc 4 z Zadania 4).
        // Silnik traktuje wybor spoza listy jak brak wyboru — cicho, bo jest
        // ostatnia linia obrony. Tutaj musi byc glosno: literowka w zadaniu HTTP
        // dalaby wersje 1 bez kierunku, za pieniadze, a klient nie dowiedzialby sie,
        // ze jego wybor przepadl. `proposalId` idzie prosto z URL-a.
        if (!DozwoloneId.Contains(proposalId))
            throw new KeyNotFoundException($"nie ma propozycji {proposalId} w projekcie {id}");

        if (!File.Exists(Path.Combine(paths.Dir(id), "proposals", $"{proposalId}.html")))
            throw new KeyNotFoundException($"nie ma propozycji {proposalId} w projekcie {id}");

        // Wybor NIE jest zadaniem: model sie nie uruchamia (kontrakt, IProjectApi).
        store.Save(meta with { ChosenProposal = proposalId });
        return Task.CompletedTask;
    }

    /// Wersja 1 z wybranej propozycji — `POST /api/projects/{id}/versions` z §1.
    /// Nie ma tego w IProjectApi Przemka, bo mock robil ja przy wyborze; prawdziwa
    /// generacja trwa minuty, wiec musi byc zadaniem.
    public Task<string> CreateFirstVersionAsync(string id)
    {
        var meta = Wczytaj(id);
        if (meta.Status == ProjectStatus.Frozen) throw new ProjectFrozenException(id);
        if (meta.ChosenProposal is null)
            throw new InvalidOperationException($"projekt {id}: klient nie wybral propozycji");

        return Task.FromResult(queue.Enqueue(id, async ct =>
        {
            var wynik = await paths.Engine(id).RunFirstVersionAsync(paths.Store(id).Load(), ct);
            if (!wynik.Succeeded) throw new InvalidOperationException(wynik.Failure!.Cause);
        }));
    }

    public Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        var meta = Wczytaj(id);
        if (meta.Status == ProjectStatus.Frozen) throw new ProjectFrozenException(id);

        // Straz przeniesiona z Zadania 4 (watpliwosc 3 wykonawcy). Silnikowe
        // `RunRoundAsync` na projekcie BEZ wersji buduje wersje 1 promptem
        // „popraw obecna strone" na pustym WorkDir i ZUZYWA klientowi jedna z 15,
        // wbrew rulingowi 7. W silniku tego nie zamykamy: `Generator.Cli` wola
        // `RunRoundAsync` wprost wlasnie po to, zeby postawic pierwsza strone,
        // i 23 testy silnika na tym stoja — to kontrakt silnika, nie blad.
        // Zamykamy to TUTAJ, bo to jedyna sciezka, ktora dosiega klient.
        //
        // Sam warunek `version != CurrentVersion` NIE wystarcza: przy zerowej
        // liczbie wersji `CurrentVersion` rowna sie 0, wiec zadanie z `version: 0`
        // przeszloby przez niego bez szwanku.
        if (meta.CurrentVersion == 0)
            throw new StaleVersionException(version, 0);

        if (version != meta.CurrentVersion) throw new StaleVersionException(version, meta.CurrentVersion);

        // Zapis PRZED kolejka, nie w zadaniu: kontrakt 4.4 gwarantuje, ze po
        // nieudanej rundzie uwagi zostaja `open` i klient nie musi ich pisac
        // od nowa. Gdyby zapis siedzial w zadaniu, awaria zabralaby mu je razem
        // z runda.
        var store = paths.Comments(id);
        var silnikowe = comments.Select(ToComment).ToList();
        store.Upsert(version, silnikowe);

        return Task.FromResult(queue.Enqueue(id, async ct =>
        {
            var wynik = await paths.Engine(id).RunRoundAsync(paths.Store(id).Load(), silnikowe, ct);
            if (!wynik.Succeeded) throw new InvalidOperationException(wynik.Failure!.Cause);

            // Kontrakt 4.4.2: status zmienia sie WYLACZNIE z raportu i WYLACZNIE
            // po udanej rundzie. Uwagi ida do wersji, ktora byla biezaca, gdy je
            // pisano — nie do nowo powstalej.
            store.ApplyResults(version, wynik.CommentResults);
        }));
    }

    public Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version) =>
        Task.FromResult<IReadOnlyList<CommentDto>>(
            [.. paths.Comments(id).ForVersion(version).Select(k => new CommentDto(
                k.Id, k.Anchor, k.Text, k.Viewport, k.CreatedAt,
                k.Status switch
                {
                    CommentStatus.Applied => "applied",
                    CommentStatus.Rejected => "rejected",
                    _ => "open",
                },
                k.Note))]);

    /// Kontrakt 5.1: przestawia biezaca wersje. Historia zostaje, nic nie jest
    /// usuwane, runda sie nie zuzywa. Dziala takze na projekcie `frozen` (4.5:
    /// klient nie traci dostepu do tego, co juz ma) — ale nie w trakcie zadania,
    /// bo podmienialoby work/ pod pracujacym modelem.
    public Task RollbackAsync(string id, int version)
    {
        if (queue.MaAktywne(id)) throw new JobRunningException(id);

        var store = paths.Store(id);
        var meta = store.Load();
        if (meta.Versions.All(v => v.Number != version))
            throw new KeyNotFoundException($"projekt {id} nie ma wersji {version}");

        paths.Versions(id).Restore(version);
        store.Save(meta with { CurrentVersion = version });
        return Task.CompletedTask;
    }

    public Task<JobView> GetJobAsync(string jobId) =>
        Task.FromResult(queue.Get(jobId)
            ?? throw new KeyNotFoundException($"nie ma zadania {jobId}"));

    private ProjectMeta Wczytaj(string id) =>
        paths.Exists(id) ? paths.Store(id).Load() : throw new KeyNotFoundException($"nie ma projektu {id}");

    private void ZapiszSzkice(string id, IReadOnlyList<Proposal> propozycje)
    {
        // Silnik pisze szkice na dysk sam; ta metoda istnieje na wypadek atrapy
        // w testach, ktora zwraca je tylko w pamieci. Idempotentna.
        var katalog = Path.Combine(paths.Dir(id), "proposals");
        Directory.CreateDirectory(katalog);
        foreach (var p in propozycje)
        {
            var plik = Path.Combine(katalog, $"{p.Id}.html");
            if (!File.Exists(plik)) File.WriteAllText(plik, p.Html);
        }
    }

    private static List<ProposalView> SzkiceZDysku(string katalog) =>
        [.. new[] { "a", "b", "c" }
            .Select(x => (Id: x, Plik: Path.Combine(katalog, $"{x}.html")))
            .Where(x => File.Exists(x.Plik))
            .Select(x =>
            {
                var html = File.ReadAllText(x.Plik);
                var m = System.Text.RegularExpressions.Regex.Match(
                    html, @"<title[^>]*>(.*?)</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Singleline);
                return new ProposalView(x.Id,
                    m.Success ? m.Groups[1].Value.Trim() : $"Kierunek {x.Id.ToUpperInvariant()}", html);
            })];

    private static Comment ToComment(CommentDto d) =>
        new(d.Id, d.Anchor, d.Text, d.Viewport, d.CreatedAt);

    /// Kontrakt 1: spentUsd/budgetUsd NIE wychodza. ProjectView ich nie ma i
    /// mapowanie jest jedynym miejscem, w ktorym ktos moglby je dolozyc.
    private static ProjectView ToView(ProjectMeta m) => new(
        m.Id,
        m.Token,
        m.Status switch
        {
            ProjectStatus.Draft => "draft",
            ProjectStatus.Active => "active",
            _ => "frozen",
        },
        m.RoundsUsed,
        m.RoundsLimit,
        [.. m.Versions.Select(v => new VersionView(
            v.Number,
            $"/preview/{m.Id}/{v.Number}/",     // kontrakt 5.4: ukosnik na koncu
            v.OrphanedAnchors,
            v.BasedOn,
            v.ChangedAnchors ?? []))],
        m.CurrentVersion);
}
