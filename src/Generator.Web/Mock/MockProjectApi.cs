using System.Collections.Concurrent;
using Generator.Engine.Versioning;
using Generator.Web.Api;
using Generator.Web.Contracts;
using Generator.Web.Preview;

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

    /// <summary>
    /// Kotwice, ktore atrapa „poprawila" do danej wersji wlacznie. Kumuluja sie PO
    /// RODZICU, nie po numerze: inaczej wersja powstala liniowo gubilaby wyrozniki
    /// rodzica i podswietlenie pokazywaloby zmiany, ktorych nie bylo.
    /// </summary>
    private readonly ConcurrentDictionary<(string, int), HashSet<string>> _wyroznione = new();

    /// <summary>
    /// Projekty, dla ktorych szkice JUZ zamowiono. Bez tego atrapa oddawala trzy
    /// propozycje projektowi, ktory o nie nigdy nie poprosil — a prawdziwe
    /// `ProjectApi.GetProposalsAsync` na nieistniejacym `proposals/` oddaje PUSTA
    /// liste. Roznica jest widoczna dokladnie tam, gdzie `Proposals` rozgalezia sie
    /// na „pokaz, co jest" kontra „zamow komplet".
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _zamowione = new();

    /// <summary>
    /// projectId -> jobId zadania, ktore wlasnie trwa. Atrapa nie ma kolejki, wiec
    /// jej „zadanie" to samo wywolanie metody: rezerwacja zyje od wejscia do wyjscia
    /// (prawdziwa `JobQueue` trzyma ja do konca pracy w tle). To wystarcza, zeby
    /// odtworzyc jedyna sytuacje, w ktorej klient to widzi — drugi klik albo druga
    /// karta w trakcie generacji — i nie grozi zawieszeniem rezerwacji na zawsze.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _aktywne = new();

    /// Straz „jedno zadanie na projekt" (plan, sekcja 7). Prawdziwe `ProjectApi`
    /// pyta o to `queue.MaAktywne(id)` w Choose, Rollback i Apply.
    private void Wolne(string id)
    {
        if (_aktywne.ContainsKey(id)) throw new JobRunningException(id);
    }

    private void Zajmij(string id, string jobId)
    {
        if (!_aktywne.TryAdd(id, jobId)) throw new JobRunningException(id);
    }

    private void Zwolnij(string id, string jobId) =>
        _aktywne.TryRemove(new KeyValuePair<string, string>(id, jobId));

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

    /// <summary>
    /// Dopisuje JEDNA uwage o zadanym statusie, nie ruszajac pozostalych. Potrzebne, gdy
    /// test musi odtworzyc wersje, w ktorej cos jest juz `applied`, a cos dopiero `open`.
    /// </summary>
    public void SeedComment(string id, int version, string anchor, string text, string status)
    {
        var lista = _comments.TryGetValue((id, version), out var istniejace)
            ? new List<CommentDto>(istniejace)
            : [];

        lista.Add(new CommentDto(
            Id: $"seed-{anchor}-{lista.Count}",
            Anchor: anchor,
            Text: text,
            Viewport: "desktop",
            CreatedAt: DateTimeOffset.UnixEpoch,
            Status: status,
            Note: status == "applied" ? $"Zrobione: {text}" : null));

        _comments[(id, version)] = lista;
    }

    /// <summary>
    /// Wpisuje uwagi do wersji, ZOSTAWIAJAC te, ktore juz sa rozliczone. Runda przynosi
    /// tylko `open`, a `applied`/`rejected` naleza do historii tej wersji — nadpisanie
    /// skasowaloby klientowi raport „co zrobilismy" z wczesniejszej rundy.
    /// </summary>
    private void Scal(string id, int version, IReadOnlyList<CommentDto> nowe)
    {
        var byly = _comments.TryGetValue((id, version), out var lista)
            ? lista
            : [];

        _comments[(id, version)] =
        [
            .. byly.Where(c => nowe.All(n => n.Id != c.Id) && c.Status != "open"),
            .. nowe,
        ];
    }

    public async Task<string> RequestProposalsAsync(string id)
    {
        if (_projects[id].Status == "frozen") throw new ProjectFrozenException(id);   // kontrakt 4.5

        var jobId = Guid.NewGuid().ToString();
        Zajmij(id, jobId);
        try
        {
            await Task.Delay(SimulatedDelay);
            _zamowione[id] = 0;
            _jobs[jobId] = new JobView(jobId, "succeeded", null);
            return jobId;
        }
        finally
        {
            Zwolnij(id, jobId);
        }
    }

    /// Te same trzy identyfikatory co `GenerationService.SketchIds` i `ProjectApi`.
    /// Atrapa uzywala „p-1/p-2/p-3", wiec kazdy wybor przeklikany na niej byl
    /// wyborem, ktory prawdziwe API odrzuca jako nieistniejacy.
    private static readonly string[] DozwoloneId = ["a", "b", "c"];

    public Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id) =>
        Task.FromResult<IReadOnlyList<ProposalView>>(_zamowione.ContainsKey(id) ?
        [
            new("a", "Ciepły i prosty", "<html><body style='font-family:system-ui'><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>"),
            new("b", "Nowoczesny, ciemny", "<html><body style='background:#111;color:#eee'><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>"),
            new("c", "Klasyczny z cennikiem", "<html><body><h1 data-cmt-id=\"hero\">Fryzjer</h1><ul data-cmt-id=\"cennik\"><li>Strzyżenie 60 zł</li></ul></body></html>"),
        ] : []);

    /// <summary>
    /// Wybor propozycji MUSI utworzyc wersje 1 — to ona jest pierwsza strona klienta.
    /// Bez tego edytor otwiera sie bez iframe'a i klient nie ma w co kliknac. Testy
    /// komponentow tego nie widzialy, bo dorabialy sobie wersje recznym wywolaniem
    /// ApplyCommentsAsync w Setup().
    /// </summary>
    public async Task ChooseProposalAsync(string id, string proposalId)
    {
        Wolne(id);
        var project = _projects[id];

        // Ta sama biala lista i ten sam typ wyjatku co w `ProjectApi`. Atrapa
        // przyjmowala dotad kazdy tekst, wiec galezi „szkicu juz nie ma" nie dalo
        // sie przejsc ani przeklikac.
        if (!DozwoloneId.Contains(proposalId))
            throw new KeyNotFoundException($"nie ma propozycji {proposalId} w projekcie {id}");

        // Wybor tylko ZAPISUJE kierunek — wersje 1 buduje osobne zadanie, dokladnie jak
        // w `ProjectApi`. Atrapa robila to odwrotnie (wersja przy wyborze, puste
        // `CreateFirstVersionAsync`) i byla to ostatnia znana rozbieznosc: test
        // komponentu wolajacy samo `Choose` przechodzil, choc pod prawdziwym API klient
        // zostawalby w edytorze bez zadnej wersji.
        _wybrane[id] = proposalId;
        _projects[id] = project with { Status = "active" };
        await Task.CompletedTask;
    }

    /// Wybrany kierunek — u silnika `ProjectMeta.ChosenProposal`.
    private readonly ConcurrentDictionary<string, string> _wybrane = new();

    /// <summary>
    /// Atrapa tworzy wersje 1 juz przy wyborze i robi to synchronicznie, wiec nie ma
    /// czego czekac: zadanie jest gotowe w chwili powstania. W Planie B ta metoda
    /// odpala model na minuty, dlatego w kontrakcie jest zadaniem, a nie `void`.
    /// </summary>
    /// <summary>
    /// Wersja 1 z wybranego kierunku — osobne zadanie, jak w `ProjectApi`. Te same dwie
    /// straze, oba `InvalidOperationException` (tak rzuca prawdziwe API, a `Proposals`
    /// rozpoznaje odmowe wlasnie po tym typie):
    /// brak wyboru i „wersja pierwsza generuje sie tylko raz".
    /// </summary>
    public async Task<string> CreateFirstVersionAsync(string id)
    {
        Wolne(id);
        var project = _projects[id];

        if (project.Versions.Count > 0)
            throw new InvalidOperationException(
                $"projekt {id} ma juz {project.Versions.Count} wersji — wersje pierwsza " +
                "generuje sie tylko raz; kolejne zmiany ida przez runde uwag");

        if (!_wybrane.ContainsKey(id))
            throw new InvalidOperationException($"projekt {id}: klient nie wybral propozycji");

        var jobId = Guid.NewGuid().ToString();
        Zajmij(id, jobId);
        try
        {
            await ZapiszSnapshot(id, 1, [], rodzic: null);
            _projects[id] = _projects[id] with
            {
                Versions = [new VersionView(1, $"/preview/{id}/1/", [], BasedOn: null, ChangedAnchors: [])],
                CurrentVersion = 1,
            };
            _jobs[jobId] = new JobView(jobId, "succeeded", null);
            return jobId;
        }
        finally
        {
            Zwolnij(id, jobId);
        }
    }

    /// <summary>
    /// Kontrakt 5.1. Zamrozenie NIE blokuje powrotu — 4.5 blokuje nowe rundy, a nie
    /// ogladanie tego, co klient juz ma. Runda sie nie zuzywa, bo model sie nie uruchamia.
    /// </summary>
    public Task RollbackAsync(string id, int version)
    {
        // Kontrakt 5.1: powrotu nie wolno wykonac w trakcie zadania — konczaca sie
        // runda przestawilaby wskaznik i cicho uniewaznila powrot.
        Wolne(id);
        var project = _projects[id];

        // KeyNotFoundException, nie ArgumentOutOfRangeException: `Editor.Kolizja`
        // rozpoznaje ten pierwszy i pokazuje zdanie, a drugiego nie — wiec pod atrapa
        // ten sam klik ZRYWAL OBWOD, choc pod prawdziwym API konczy sie komunikatem.
        if (project.Versions.All(v => v.Number != version))
            throw new KeyNotFoundException($"projekt {id} nie ma wersji {version}");

        _projects[id] = project with { CurrentVersion = version };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gdzie lezy snapshot wersji. JEDNA prawda o tej sciezce jest `VersionStore` —
    /// atrapa nie ma prawa miec wlasnej. Recznie sklejane „SnapshotRoot/id/vN"
    /// zgadzalo sie tylko z atrapa: silnik pisze do „SnapshotRoot/id/versions/001",
    /// wiec po podmianie DI `/preview` czytalby katalog, ktorego nie ma, i klient
    /// dostawalby pusty iframe. Tego testy komponentow nie widza.
    /// </summary>
    private string Snapshot(string id, int numer) =>
        new VersionStore(Path.Combine(SnapshotRoot, id)).SnapshotPath(numer);

    /// <summary>
    /// To, co w produkcji robi silnik: ma snapshoty na dysku, wiec on liczy zmiany
    /// (kontrakt 5.3). Frontend nie sciaga dwoch pelnych wersji strony do przegladarki.
    /// </summary>
    private async Task<IReadOnlyList<string>> PoliczZmiany(string id, int numer, int? rodzic)
    {
        if (rodzic is not { } r) return [];

        var plikRodzica = Path.Combine(Snapshot(id, r), "index.html");
        var plikWersji = Path.Combine(Snapshot(id, numer), "index.html");
        if (!File.Exists(plikRodzica) || !File.Exists(plikWersji)) return [];

        return await ZmienioneBloki.Policz(
            await File.ReadAllTextAsync(plikRodzica),
            await File.ReadAllTextAsync(plikWersji));
    }

    /// <summary>
    /// Kontrakt 6.1. Te same odmowy co runda, ale bez „zadania" — atrapa musi je rzucac
    /// tymi samymi typami, inaczej galezie kolizji sa nieosiagalne pod atrapa.
    /// </summary>
    public Task SaveCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        var stan = _projects[id];
        if (stan.Status == "frozen") throw new ProjectFrozenException(id);
        if (stan.CurrentVersion == 0) throw new StaleVersionException(version, 0);
        if (version != stan.CurrentVersion) throw new StaleVersionException(version, stan.CurrentVersion);
        Wolne(id);

        Scal(id, version, comments);
        return Task.CompletedTask;
    }

    public async Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        var stan = _projects[id];

        // ProjectFrozenException, nie goly InvalidOperationException: `Editor.Kolizja`
        // wymienia typy, a goly wyjatek przez nia nie przechodzil — zamrozenie po
        // pietnastej rundzie trafia w KAZDEGO klienta, wiec pod atrapa kazdy z nich
        // dostawalby zerwany obwod zamiast zdania o wyczerpanym pakiecie.
        if (stan.Status == "frozen") throw new ProjectFrozenException(id);   // kontrakt 4.5

        // Kontrakt 5.1: uwagi ida do wersji, ktora klient WIDZI. Stara karta wysyla
        // je do v1, gdy biezaca jest juz v2 — atrapa przyjmowala to obojetnie, wiec
        // galezi „strona zmienila sie w miedzyczasie" nie dalo sie przejsc.
        // Zero wersji tez jest nieaktualnoscia: runda nie ma czego poprawiac.
        if (stan.CurrentVersion == 0 || version != stan.CurrentVersion)
            throw new StaleVersionException(version, stan.CurrentVersion);

        var jobId = Guid.NewGuid().ToString();
        Zajmij(id, jobId);

        Scal(id, version, comments);

        _jobs[jobId] = new JobView(jobId, "running", null);
        try
        {
            await Task.Delay(SimulatedDelay);
        }
        finally
        {
            // Rezerwacja konczy sie razem z wywolaniem: `DokonczPowtorke` chodzi
            // w tle i moze nigdy sie nie skonczyc (RetryResolvesAfter = null),
            // wiec trzymanie jej dluzej zamykaloby projekt na zawsze.
            Zwolnij(id, jobId);
        }

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
        Scal(id, version, [.. comments.Select(c => c with
        {
            Status = "applied",
            Note = $"Zrobione: {c.Text}",
        })]);

        var project = _projects[id];

        // Numer z maksimum, nie z liczby wersji: po powrocie do starszej wersji liczba
        // elementow przestaje odpowiadac najwyzszemu numerowi i doszloby do kolizji.
        var numer = project.Versions.Count == 0 ? 1 : project.Versions.Max(v => v.Number) + 1;

        // Rodzicem jest wersja, ktora klient WIDZIAL, gdy zglaszal uwagi — po rollbacku
        // to nie jest ostatnia w historii.
        var rodzic = project.CurrentVersion > 0 ? project.CurrentVersion : (int?)null;

        await ZapiszSnapshot(id, numer, comments, rodzic);
        var zmienione = await PoliczZmiany(id, numer, rodzic);

        _projects[id] = project with
        {
            RoundsUsed = project.RoundsUsed + 1,
            Versions = [.. project.Versions,
                new VersionView(numer, $"/preview/{id}/{numer}/", [], rodzic, zmienione)],
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
    private async Task ZapiszSnapshot(
        string id, int numer, IReadOnlyList<CommentDto> uwagi, int? rodzic)
    {
        var katalog = Snapshot(id, numer);
        Directory.CreateDirectory(katalog);

        // Dziedziczymy wyrozniki po RODZICU i dokladamy kotwice z tej rundy.
        var wyroznione = rodzic is { } r && _wyroznione.TryGetValue((id, r), out var poprzednie)
            ? new HashSet<string>(poprzednie, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var kotwica in uwagi.Select(u => u.Anchor).OfType<string>())
            wyroznione.Add(kotwica);
        _wyroznione[(id, numer)] = wyroznione;

        // Atrapa „poprawia" blok zmieniajac styl, a nie tekst — dokladnie tak, jak model
        // odpowiada na „powieksz telefon". Gdyby zmieniala tekst, porownanie po
        // TextContent tez by dzialalo i nie sprawdzalibysmy niczego trudnego.
        string Styl(string kotwica) =>
            wyroznione.Contains(kotwica) ? " style=\"font-size:1.25em\"" : "";

        var uwzglednione = uwagi.Count == 0
            ? "<!-- wersja poczatkowa -->"
            : string.Join("\n    ", uwagi.Select(u =>
                $"<!-- uwzgledniono: {System.Net.WebUtility.HtmlEncode(u.Text)} -->"));

        var html = $"""
        <html lang="pl">
        <head><meta charset="utf-8"><title>Fryzjer</title><link rel="stylesheet" href="styl.css"></head>
        <body>
            {uwzglednione}
            <h1 data-cmt-id="hero"{Styl("hero")}>Fryzjer Anna</h1>
            <section data-cmt-id="oferta-strzyzenie"{Styl("oferta-strzyzenie")}><h2>Strzyżenie</h2><p>60 zł</p></section>
            <section data-cmt-id="oferta-broda"{Styl("oferta-broda")}><h2>Broda</h2><p>40 zł</p></section>
            <ul data-cmt-id="cennik"{Styl("cennik")}><li>Strzyżenie 60 zł</li><li>Broda 40 zł</li></ul>
            <footer data-cmt-id="kontakt"{Styl("kontakt")}>tel. 600 100 200, ul. Krakowska 5</footer>
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
