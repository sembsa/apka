using Generator.Engine.Jobs;
using Generator.Engine.Gates;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Web.Contracts;

namespace Generator.Web.Api;

/// Kontrakt 1 w procesie. Blazor Server wola to bezposrednio (postep idzie
/// obwodem SignalR), a ApiEndpoints jest cienka warstwa HTTP nad tym samym.
public class ProjectApi(ProjectPaths paths, JobQueue queue, ILogger<ProjectApi> logger) : IProjectApi
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


    /// <summary>
    /// Kolejkuje zadanie i ZOSTAWIA PO NIM SLAD NA DYSKU. Jedyna droga do kolejki
    /// z tej klasy — trzy sciezki (propozycje, wersja pierwsza, runda uwag) roznia
    /// sie praca, a nie tym, jak zadanie ma przezyc awarie procesu.
    ///
    /// Kolejnosc jest tu cala trescia: identyfikator powstaje PRZED kolejka, zapis
    /// na dysk wyprzedza `Enqueue`, a `Enqueue` wyprzedza oddanie identyfikatora
    /// klientowi. Odwrotna kolejnosc zostawialaby klienta z identyfikatorem, ktorego
    /// dysk nigdy nie widzial — i po restarcie znowu bylaby czterysta czwórka
    /// (patrz `OdzyskiwaniePoStarcie`).
    /// </summary>
    private string Zakolejkuj(string id, Func<string, CancellationToken, Task> praca)
    {
        var store = paths.Store(id);
        var jobId = Guid.NewGuid().ToString();
        store.Save(store.Load() with { ActiveJobId = jobId });

        try
        {
            return queue.Enqueue(id, jobId, async (j, ct) =>
            {
                try
                {
                    await praca(j, ct);
                }
                finally
                {
                    // Wczytujemy PONOWNIE, a nie zapisujemy zapamietanego rekordu:
                    // praca wlasnie zapisala wersje, koszt i status, wiec `meta`
                    // sprzed niej jest juz nieaktualna i nadpisanie nia cofneloby
                    // efekt calej rundy.
                    var po = store.Load();
                    if (po.ActiveJobId == jobId) store.Save(po with { ActiveJobId = null });
                }
            });
        }
        catch (Exception)
        {
            // `Enqueue` odmowil (projekt ma juz zadanie). Znacznik nie moze zostac,
            // bo zablokowalby projekt na zawsze — i to za zadanie, ktore nigdy nie
            // ruszylo. Sprzatanie nie moze przeslonic prawdziwego bledu.
            try
            {
                var po = store.Load();
                if (po.ActiveJobId == jobId) store.Save(po with { ActiveJobId = null });
            }
            catch (Exception) { }
            throw;
        }
    }

    public Task<ProjectView> GetAsync(string id) => Task.FromResult(ToView(Wczytaj(id)));

    public Task<string> RequestProposalsAsync(string id)
    {
        var meta = Wczytaj(id);
        if (meta.Status == ProjectStatus.Frozen) throw new ProjectFrozenException(id);

        // Straz na WYDATEK, nie na porzadek. Komplet szkicow to jedno wywolanie
        // modelu za okolo pol dolara, a `RunProposalsAsync` zaczyna od skasowania
        // `proposals/` i wyzerowania `ChosenProposal` — czyli powtorne zamowienie
        // nie tylko placi drugi raz, ale i kasuje wybor, ktory klient juz zrobil.
        // Straznik siedzi TUTAJ, a nie w `Proposals.razor`: komponent nie jest
        // jedyna droga (sa endpointy §1), a pieniedzy nie broni sie w widoku.
        //
        // Komplet, nie „cokolwiek na dysku": nieudane zadanie potrafi zostawic
        // 2 z 3 szkicow (silnik zwraca wtedy `failed` z „model zapisal 2 z 3”).
        // Warunek `> 0` zamykalby wtedy projekt na zawsze — klient mialby dwie
        // karty do wyboru i zadnej drogi po trzecia.
        if (meta.Versions.Count > 0)
            throw new InvalidOperationException(
                $"projekt {id} ma juz {meta.Versions.Count} wersji — szkice kierunku " +
                "zamawia sie tylko przed wersja pierwsza");

        if (Komplet(id))
            throw new InvalidOperationException(
                $"projekt {id} ma juz komplet szkicow — pokaz istniejace zamiast zamawiac nowe");

        return Task.FromResult(Zakolejkuj(id, async (jobId, ct) =>
        {
            var wynik = await paths.Engine(id).RunProposalsAsync(paths.Store(id).Load(), ct);
            if (!wynik.Succeeded) { Awaria(jobId, id, wynik.Failure!); return; }
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

    /// Czy na dysku lezy PELNA trojka szkicow. Ten sam odczyt co `GetProposalsAsync`,
    /// wiec straz i widok nie moga sie rozjechac.
    private bool Komplet(string id) =>
        SzkiceZDysku(Path.Combine(paths.Dir(id), "proposals")).Count == DozwoloneId.Length;

    /// Dozwolone identyfikatory propozycji. Ta sama trojka co `GenerationService.SketchIds`
    /// — swiadome powtorzenie, bo to granica zaufania: silnik ma swoja biala liste jako
    /// OSTATNIA linie obrony, a ta jest PIERWSZA. Wspolna stala kusi, ale zrobilaby
    /// z dwoch niezaleznych warstw jedna.
    private static readonly string[] DozwoloneId = ["a", "b", "c"];

    public Task ChooseProposalAsync(string id, string proposalId)
    {
        // Ta sama straz co w RollbackAsync i ApplyCommentsAsync. Wybor to
        // czytaj-zmodyfikuj-zapisz na project.json, a trwajace zadanie zapisuje
        // ten sam plik na koniec rundy — kto zapisze drugi, wygrywa calym plikiem.
        // Brak tej strazy byl przypadkowa asymetria, nie decyzja.
        if (queue.MaAktywne(id)) throw new JobRunningException(id);

        // Przez `Wczytaj`, nie `paths.Store(id).Load()` wprost: dla nieistniejacego
        // projektu `Load` czyta nieistniejacy plik i rzuca InvalidDataException
        // („project.json uszkodzony"), czyli 500 z komunikatem o zepsutych danych
        // tam, gdzie danych po prostu nie ma. Klientowi nalezy sie 404.
        var meta = Wczytaj(id);
        var store = paths.Store(id);

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

        // Silnik ma te sama straz i pieniadze sa przez nia bezpieczne — ale odrzuca
        // dopiero W ZADANIU, wiec klient dostaje zadanie w stanie `failed` zamiast
        // odmowy od reki, jak przy kazdej innej strazy tej warstwy. Silnikowa
        // ZOSTAJE jako ostatnia linia obrony (Generator.Cli tez przez nia chodzi);
        // ta jest pierwsza i mowi klientowi prawde synchronicznie.
        if (meta.Versions.Count > 0)
            throw new InvalidOperationException(
                $"projekt {id} ma juz {meta.Versions.Count} wersji — wersje pierwsza generuje sie " +
                "tylko raz; kolejne zmiany ida przez runde uwag");

        if (meta.ChosenProposal is null)
            throw new InvalidOperationException($"projekt {id}: klient nie wybral propozycji");

        return Task.FromResult(Zakolejkuj(id, async (jobId, ct) =>
        {
            var wynik = await paths.Engine(id).RunFirstVersionAsync(paths.Store(id).Load(), ct);
            if (!wynik.Succeeded) { Awaria(jobId, id, wynik.Failure!); return; }
        }));
    }

    /// <summary>
    /// Kontrakt 6.1. Te same straze co przy rundzie, poza jedna roznica: NIE uruchamia
    /// modelu, wiec nic nie kosztuje i nie zuzywa rundy. `frozen` blokuje zapis tak samo
    /// jak runde — projekt zamrozony nie przyjmuje nowych uwag (4.5) — a trwajace zadanie
    /// blokuje, bo `Uzgodnij` i `ApplyResults` to dwie sekwencje czytaj-zmodyfikuj-zapisz
    /// na tym samym pliku i zapis z UI potrafil scierac raport konczacej sie rundy.
    /// </summary>
    public Task SaveCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        var meta = Wczytaj(id);
        if (meta.Status == ProjectStatus.Frozen) throw new ProjectFrozenException(id);
        if (meta.CurrentVersion == 0) throw new StaleVersionException(version, 0);
        if (version != meta.CurrentVersion) throw new StaleVersionException(version, meta.CurrentVersion);
        if (queue.MaAktywne(id)) throw new JobRunningException(id);

        paths.Comments(id).Uzgodnij(version, [.. comments.Select(ToComment)]);
        return Task.CompletedTask;
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

        // Odmowa PRZED zapisem. Kontrakt 4.4 chroni NIEUDANA RUNDE, a nie odrzucone
        // zadanie: klient dostaje 409, jego tekst siedzi dalej w obwodzie Blazora,
        // a runda z tymi uwagami nigdy nie ruszyla. Zapis w tym miejscu nie dawal
        // wiec nic, a szkodzil realnie — to wlasnie ten zapis scieral sie z
        // `ApplyResults` konczacej sie rundy (obie metody to czytaj-zmodyfikuj-zapisz
        // na tym samym pliku) i zaobserwowano, ze uwaga po zapisie znikala.
        // `Enqueue` i tak sprawdza to atomowo przez TryAdd — ta straz jest po to,
        // zeby nie dotknac dysku, zanim tam dojdziemy.
        if (queue.MaAktywne(id)) throw new JobRunningException(id);

        // Zapis PRZED kolejka, nie w zadaniu: kontrakt 4.4 gwarantuje, ze po
        // nieudanej rundzie uwagi zostaja `open` i klient nie musi ich pisac
        // od nowa. Gdyby zapis siedzial w zadaniu, awaria zabralaby mu je razem
        // z runda.
        // `Uzgodnij`, nie „dopisz": przychodzaca lista JEST pelna lista uwag `open`
        // tej wersji. Uwaga wycofana przez klienta (kontrakt 3.3) nie ma innej drogi
        // na dysk — bez tego zostawala `open` w comments.json na zawsze, wracala przy
        // kazdym odswiezeniu i zabierala klientowi runde za rzecz, ktora sam odwolal.
        var store = paths.Comments(id);
        var silnikowe = comments.Select(ToComment).ToList();
        store.Uzgodnij(version, silnikowe);

        return Task.FromResult(Zakolejkuj(id, async (jobId, ct) =>
        {
            var wynik = await paths.Engine(id).RunRoundAsync(paths.Store(id).Load(), silnikowe, ct);
            if (!wynik.Succeeded) { Awaria(jobId, id, wynik.Failure!); return; }

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

        // `Wczytaj`, nie `Load()` wprost — nieistniejacy projekt ma dac 404, a nie
        // 500 z komunikatem „project.json uszkodzony".
        var meta = Wczytaj(id);
        var store = paths.Store(id);
        var versions = paths.Versions(id);

        if (meta.Versions.All(v => v.Number != version))
            throw new KeyNotFoundException($"projekt {id} nie ma wersji {version}");

        // Snapshot sprawdzamy PRZED czymkolwiek i wlasnym wyjatkiem. Wersja obecna
        // na liscie, ale bez katalogu na dysku (reczne sprzatanie, przerwany zapis),
        // dawala z `Restore` DirectoryNotFoundException — czyli 500 „cos padlo"
        // zamiast 404 „nie ma czego przywrocic". Dla klienta to ta sama sytuacja co
        // zly numer wersji, wiec ten sam typ wyjatku.
        if (!Directory.Exists(versions.SnapshotPath(version)))
            throw new KeyNotFoundException(
                $"projekt {id}: wersja {version} jest na liscie, ale nie ma jej snapshotu na dysku");

        // Kompensacja, nie zmiana kolejnosci. Obie kolejnosci (Restore→Save i
        // Save→Restore) zostawiaja niespojnosc, gdy druga operacja padnie, wiec
        // rozstrzyga tylko cofniecie. Rozjazd jest grozny cicho: work/ z plikami v1
        // i project.json mowiacy `CurrentVersion = 2` nie psuje podgladu (ten serwuje
        // snapshoty), ale NASTEPNA runda pojedzie po plikach v1, zapisze `BasedOn = 2`
        // i policzy ChangedAnchors wzgledem v2 — klient zobaczy „zmienilo sie
        // wszystko" i poprawki naniesione na tresc, ktorej nie ogladal.
        var poprzednia = meta.CurrentVersion;
        versions.Restore(version);
        try
        {
            store.Save(meta with { CurrentVersion = version });
        }
        catch (Exception)
        {
            // Najlepszy wysilek: wracamy work/ tam, gdzie bylo, i przepuszczamy
            // oryginalny wyjatek. Gdy i to padnie, nie mamy juz czym naprawiac —
            // ale nie wolno zgubic pierwotnej przyczyny pod wtorna awaria.
            try { if (poprzednia > 0) versions.Restore(poprzednia); } catch (Exception) { }
            throw;
        }

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

    /// `DozwoloneId`, nie druga kopia listy: komentarz nad ta stala uzasadnia
    /// powtorzenie wzgledem SILNIKA (granica zaufania), a nie wzgledem samego siebie.
    /// Dwie listy w jednym pliku rozjechalyby sie przy pierwszej zmianie liczby kierunkow.
    private static List<ProposalView> SzkiceZDysku(string katalog) =>
        [.. DozwoloneId
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

    /// <summary>
    /// Jedyna droga awarii zadania na tej granicy. Kontrakt 4.3 projektowal `cause`
    /// jako „szczegol dla naszych logow, NIE do UI" — logow nie bylo: `JobFailure`
    /// wchodzil tu z precyzyjnym komunikatem `RunGate` (brak `claude` na PATH,
    /// niepuste `permission_denials`, `stop_reason` inny niz `end_turn`), a wychodzil
    /// jako para `handling` + `attempts`. Przyczyna gubila sie bezpowrotnie i nie
    /// zostawalo NIC, co ktos moglby przeczytac o 2 w nocy.
    ///
    /// `cause` nadal NIE wychodzi do klienta — `JobFailureView` go nie ma i to
    /// mapowanie jest jedynym miejscem, w ktorym ktos moglby go dolozyc.
    /// `projectId` i `jobId` sa w logu obowiazkowo: bez nich wpis mowi, ze cos padlo,
    /// ale nie u kogo.
    /// </summary>
    private void Awaria(string jobId, string id, JobFailure f)
    {
        logger.LogError(
            "zadanie {JobId} projektu {ProjectId} nie powiodlo sie: handling={Handling}, attempts={Attempts}, cause={Cause}",
            jobId, id, Handling(f), f.Attempts, f.Cause);

        queue.Fail(jobId, Handling(f), f.Attempts);
    }

    /// Kontrakt 4.3: UI rozgalezia sie po `handling`, a `attempts` idzie do niego
    /// w `Job.failure`. Wczesniej awaria wracala wyjatkiem i kolejka wpisywala
    /// `attempts: 1` na sztywno — czyli API klamalo w polu, ktore samo wystawia.
    /// Silnik zna prawdziwa liczbe prob, wiec ja przekazujemy.
    private static string Handling(JobFailure f) =>
        f.Handling == FailureHandling.Retrying ? "retrying" : "halted";

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
            v.ChangedAnchors ?? [],
            v.CreatedAt))],
        m.CurrentVersion);
}
