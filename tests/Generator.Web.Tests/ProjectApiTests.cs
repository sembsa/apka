using Generator.Engine.Gates;
using Generator.Engine.Jobs;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;
using Generator.Web.Api;
using Generator.Web.Contracts;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Generator.Web.Tests;

/// Atrapa silnika: nie uruchamia modelu, ale zapisuje snapshoty i metadane tak,
/// jak zrobilby to GenerationService. Testy tej warstwy dotycza kolejki, straznikow
/// i mapowania na DTO — nie bramek, ktore maja wlasne 120 testow w silniku.
internal class FakeRunner(ProjectStore store, VersionStore versions) : IRoundRunner
{
    public List<string> Wywolania { get; } = [];
    public TaskCompletionSource? Wstrzymaj { get; set; }

    /// Awaria zwracana zamiast wersji. Kontrakt 4.3 wystawia klientowi `attempts`,
    /// wiec test musi umiec sprawdzic, ze idzie tam liczba z SILNIKA, a nie stala.
    public JobFailure? Awaria { get; set; }

    public async Task<GenerationOutcome> RunRoundAsync(
        ProjectMeta meta, IReadOnlyList<Comment> comments, CancellationToken ct = default)
    {
        Wywolania.Add("round");
        if (Wstrzymaj is not null) await Wstrzymaj.Task;
        if (Awaria is not null) return new GenerationOutcome(false, null, Awaria, [], 0.10m);
        return Commit(store.Load(), [.. comments.Select(c =>
            new CommentResult(c.Id, CommentStatus.Applied, "zrobione"))]);
    }

    public async Task<GenerationOutcome> RunFirstVersionAsync(
        ProjectMeta meta, CancellationToken ct = default)
    {
        Wywolania.Add("first");
        if (Wstrzymaj is not null) await Wstrzymaj.Task;
        return Commit(store.Load(), []);
    }

    public Task<ProposalsOutcome> RunProposalsAsync(ProjectMeta meta, CancellationToken ct = default)
    {
        Wywolania.Add("proposals");
        return Task.FromResult(new ProposalsOutcome(true,
            [new Proposal("a", "Ciepły", "<html><body>A</body></html>"),
             new Proposal("b", "Klasyka", "<html><body>B</body></html>"),
             new Proposal("c", "Kontrast", "<html><body>C</body></html>")], null, 0.05m));
    }

    private GenerationOutcome Commit(ProjectMeta meta, IReadOnlyList<CommentResult> wyniki)
    {
        var n = meta.NextVersionNumber;

        // Numer wersji W TRESCI pliku. Bez tego kazda wersja ma identyczne
        // work/index.html i nie da sie odroznic „work wrocil do v1" od „work
        // zostal przy v2" — powrot testowaloby sie samym istnieniem pliku,
        // czyli niczym.
        Directory.CreateDirectory(versions.WorkDir);
        File.WriteAllText(Path.Combine(versions.WorkDir, "index.html"),
            $"""<html><body><h1 data-cmt-id="hero">v{n}</h1></body></html>""");

        var v = versions.Commit(n, "s-1", 0.10m, [], meta.Current?.Number, []);
        store.Save(meta with
        {
            Status = ProjectStatus.Active,
            Versions = [.. meta.Versions, v],
            CurrentVersion = n,
            RoundsUsed = wyniki.Count > 0 ? meta.RoundsUsed + 1 : meta.RoundsUsed,
        });
        return new GenerationOutcome(true, v, null, wyniki, 0.10m);
    }
}

public class ProjectApiTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-api-" + Guid.NewGuid().ToString("N"));
    private readonly JobQueue _queue = new();
    private readonly ZapisujacyLogger _log = new();

    public void Dispose()
    {
        _queue.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (ProjectApi api, Func<string, FakeRunner> runner) Zbuduj()
    {
        var runnery = new Dictionary<string, FakeRunner>();
        // Jedna atrapa NA PROJEKT, nie na wywolanie. `ProjectPaths.Engine` tworzy
        // silnik na zadanie (jest tani i bezstanowy), wiec bez tej pamieci test
        // ustawialby `Wstrzymaj` na egzemplarzu, ktorego juz nikt nie uzyje —
        // brama nigdy by nie zatrzymala rundy i zadanie konczyloby sie w milisekundy.
        var paths = new ProjectPaths(_root, id =>
        {
            if (runnery.TryGetValue(id, out var istniejacy)) return istniejacy;
            var r = new FakeRunner(new ProjectStore(Path.Combine(_root, id)),
                                   new VersionStore(Path.Combine(_root, id)));
            runnery[id] = r;
            return r;
        });
        return (new ProjectApi(paths, _queue, _log), id => runnery[id]);
    }

    private static async Task Poczekaj(ProjectApi api, string jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await api.GetJobAsync(jobId);
            if (job.Status is "succeeded" or "failed") return;
            await Task.Delay(25);
        }
        Assert.Fail($"zadanie {jobId} nie skonczylo sie w 5 s");
    }

    [Fact]
    public async Task Nowy_projekt_ma_token_zero_rund_i_zaden_budzet_w_DTO()
    {
        var (api, _) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia Zosia");

        Assert.NotEmpty(p.Token);
        Assert.Equal("draft", p.Status);
        Assert.Equal(0, p.RoundsUsed);
        Assert.Equal(15, p.RoundsLimit);
        Assert.Equal(0, p.CurrentVersion);
        Assert.Empty(p.Versions);

        // Kontrakt 1: budzet nie wychodzi zadnym endpointem klienta. To asercja
        // na KSZTALT typu — gdyby ktos dolozyl pole, tu zapali sie czerwone.
        Assert.DoesNotContain(typeof(ProjectView).GetProperties(),
            x => x.Name.Contains("Usd", StringComparison.OrdinalIgnoreCase));

        // Opis klienta musi dojsc do silnika — bez niego prompt nie ma tematu.
        Assert.Equal("kwiaciarnia Zosia", new ProjectStore(Path.Combine(_root, p.Id)).Load().Description);
    }

    [Fact]
    public async Task Runda_konczy_sie_wersja_z_previewUrl_zakonczonym_ukosnikiem()
    {
        var (api, _) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));
        await api.ChooseProposalAsync(p.Id, "b");
        await Poczekaj(api, await api.CreateFirstVersionAsync(p.Id));

        var stan = await api.GetAsync(p.Id);
        var v1 = Assert.Single(stan.Versions);
        Assert.Equal(1, stan.CurrentVersion);
        Assert.EndsWith("/", v1.PreviewUrl);          // kontrakt 5.4
        Assert.Contains(p.Id, v1.PreviewUrl);
    }

    [Fact]
    public async Task Data_powstania_wersji_dochodzi_do_DTO_a_koszt_nie()
    {
        // Kontrakt 6.2, obie polowy rozstrzygniecia naraz: data wchodzi, koszt wypada.
        // Mapowanie ToView jest jedynym miejscem, w ktorym mozna oba pomylic —
        // VersionMeta niesie i CostUsd, i CreatedAt.
        var (api, _) = Zbuduj();
        var przed = DateTimeOffset.UtcNow;
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));
        await api.ChooseProposalAsync(p.Id, "b");
        await Poczekaj(api, await api.CreateFirstVersionAsync(p.Id));

        var v1 = Assert.Single((await api.GetAsync(p.Id)).Versions);

        Assert.NotNull(v1.CreatedAt);
        Assert.InRange(v1.CreatedAt!.Value, przed, DateTimeOffset.UtcNow);

        // Kontrakt 4.1: koszt nie ma sie gdzie schowac w DTO wersji.
        Assert.DoesNotContain("Usd", string.Join(",",
            typeof(VersionView).GetProperties().Select(x => x.Name)));
    }

    [Fact]
    public async Task Uwagi_wracaja_ze_statusem_nadanym_przez_silnik()
    {
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);

        var uwagi = new[] { new CommentDto("k1", "hero", "za ciemno", "desktop",
            DateTimeOffset.UnixEpoch, "open", null) };
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, uwagi));

        var wczytane = Assert.Single(await api.GetCommentsAsync(p.Id, 1));
        Assert.Equal("applied", wczytane.Status);
        Assert.Equal("zrobione", wczytane.Note);
    }

    [Fact]
    public async Task Frozen_odrzuca_runde_ale_nie_odbiera_podgladu()
    {
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);

        var store = new ProjectStore(Path.Combine(_root, p.Id));
        store.Save(store.Load() with { Status = ProjectStatus.Frozen });

        await Assert.ThrowsAsync<ProjectFrozenException>(() =>
            api.ApplyCommentsAsync(p.Id, 1, []));

        // Kontrakt 4.5: podglad i lista wersji dzialaja dalej, nic nie znika.
        var stan = await api.GetAsync(p.Id);
        Assert.Equal("frozen", stan.Status);
        Assert.Single(stan.Versions);
    }

    [Fact]
    public async Task Uwagi_do_wersji_innej_niz_biezaca_sa_odrzucane()
    {
        // Kontrakt 5.1: klient z otwarta stara karta wyslalby uwagi do v1, gdy
        // biezaca jest v2. Bez tego straznika runda poszlaby po plikach v2 z
        // uwagami napisanymi do v1 — i klient dostalby zmiany, o ktore nie prosil.
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));

        await Assert.ThrowsAsync<StaleVersionException>(() =>
            api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k2")]));
    }

    [Fact]
    public async Task Uwagi_do_projektu_bez_zadnej_wersji_sa_odrzucane()
    {
        // Bez tej strazy runda idzie promptem „popraw obecna strone" na pusty
        // WorkDir i zabiera klientowi jedna z 15 poprawek za postawienie strony,
        // ktora poprawka nie jest. `version: 0` przechodzi przez sam warunek
        // `version != CurrentVersion`, bo przy braku wersji CurrentVersion to 0.
        var (api, _) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");

        await Assert.ThrowsAsync<StaleVersionException>(() =>
            api.ApplyCommentsAsync(p.Id, 0, [Uwaga("k1")]));
        await Assert.ThrowsAsync<StaleVersionException>(() =>
            api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));
    }

    [Fact]
    public async Task Przyczyna_awarii_idzie_do_logu_a_do_klienta_nie()
    {
        // Kontrakt 4.3 projektowal `cause` jako „szczegol dla naszych logow, NIE do
        // UI". Logow nie bylo — w calej aplikacji nie bylo ani jednego loggera —
        // wiec precyzyjny komunikat RunGate („brak `claude` na PATH",
        // permission_denials, stop_reason) ginal na tej granicy bez sladu.
        var (api, runner) = Zbuduj();
        var p = await Gotowy(api);
        runner(p.Id).Awaria = new JobFailure(
            FailureHandling.Halted, "nie znaleziono `claude` na PATH", Attempts: 2);

        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]);
        await Poczekaj(api, jobId);

        // Klient dostaje galaz i liczbe prob — i nic wiecej.
        var job = await api.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status);
        Assert.Equal("halted", job.Failure!.Handling);
        Assert.Equal(2, job.Failure.Attempts);
        Assert.DoesNotContain(typeof(JobFailureView).GetProperties(),
            x => x.Name.Contains("Cause", StringComparison.OrdinalIgnoreCase));

        // My dostajemy przyczyne — razem z tym, KOGO dotyczy. Bez projectId i jobId
        // wpis mowilby tylko, ze cos gdzies padlo.
        var wpis = Assert.Single(_log.Bledy);
        Assert.Contains("nie znaleziono `claude` na PATH", wpis);
        Assert.Contains(p.Id, wpis);
        Assert.Contains(jobId, wpis);
    }

    [Fact]
    public async Task Drugie_zamowienie_szkicow_jest_odrzucane_zamiast_wydawac_pol_dolara()
    {
        // Komplet szkicow to jedno wywolanie modelu za okolo pol dolara. Wejscie
        // na /proposals/{id} zamawialo go BEZWARUNKOWO, wiec powrot strzalka
        // „wstecz" z edytora placil drugi raz — a `RunProposalsAsync` zaczyna od
        // skasowania `proposals/` i wyzerowania `ChosenProposal`, wiec przy okazji
        // wycieral klientowi wybor. Straz nalezy do API, bo komponent nie jest
        // jedyna droga: `POST /api/projects/{id}/proposals` jest w §1.
        var (api, runner) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));
        await api.ChooseProposalAsync(p.Id, "b");

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.RequestProposalsAsync(p.Id));

        // Dowod, ze odmowa jest PRZED wydatkiem: silnik dostal dokladnie jedno
        // zlecenie propozycji, a wybor klienta stoi nietkniety.
        Assert.Equal(1, runner(p.Id).Wywolania.Count(w => w == "proposals"));
        Assert.Equal("b", new ProjectStore(Path.Combine(_root, p.Id)).Load().ChosenProposal);
    }

    [Fact]
    public async Task Projekt_z_wersja_nie_zamawia_juz_szkicow_kierunku()
    {
        // Szkice sa krokiem SPRZED wersji pierwszej. Projekt, ktory ma juz strone,
        // zamawiajac je placi za material, ktorego nie da sie do niczego uzyc —
        // wersji pierwszej i tak drugi raz nie bedzie (`CreateFirstVersionAsync`).
        var (api, runner) = Zbuduj();
        var p = await Gotowy(api);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.RequestProposalsAsync(p.Id));
        Assert.Equal(1, runner(p.Id).Wywolania.Count(w => w == "proposals"));
    }

    [Fact]
    public async Task Niepelny_komplet_szkicow_wolno_zamowic_ponownie()
    {
        // Kontrapunkt do dwoch testow wyzej i powod, dla ktorego warunkiem jest
        // KOMPLET, a nie „cokolwiek na dysku": nieudane zadanie zostawia 2 z 3
        // szkicow (silnik oddaje wtedy `failed` z „model zapisal 2 z 3 szkicow").
        // Warunek `> 0` zamykalby taki projekt na zawsze — klient ogladalby dwie
        // karty i nie mial zadnej drogi po trzecia.
        var (api, _) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));

        File.Delete(Path.Combine(_root, p.Id, "proposals", "c.html"));

        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));
        Assert.Equal(3, (await api.GetProposalsAsync(p.Id)).Count);
    }

    [Theory]
    [InlineData("../../inny/versions/003/index")]
    [InlineData("/etc/passwd")]
    [InlineData("d")]
    [InlineData("")]
    public async Task Wybor_propozycji_spoza_listy_jest_odrzucany_glosno(string zle)
    {
        // `proposalId` idzie prosto z URL-a. Silnik ma wlasna biala liste, ale jako
        // OSTATNIA linia obrony i traktuje zly wybor cicho jak brak wyboru — tutaj
        // musi byc glosno, inaczej literowka daje wersje 1 bez kierunku za pieniadze.
        var (api, _) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => api.ChooseProposalAsync(p.Id, zle));
        Assert.Null(new ProjectStore(Path.Combine(_root, p.Id)).Load().ChosenProposal);
    }

    [Fact]
    public async Task Wybor_wskazujacy_istniejacy_plik_spoza_bialej_listy_jest_odrzucany()
    {
        // Sama straz „plik istnieje" NIE wystarcza i wpisy z Theory wyzej tego nie
        // pokazuja: wszystkie wskazuja na pliki, ktorych nie ma, wiec przechodza
        // przez File.Exists. Tutaj plik ISTNIEJE — jedyne, co dzieli klienta od
        // wciagniecia dowolnego pliku spod katalogu projektu do promptu wersji 1,
        // to biala lista `a|b|c`. `Path.Combine` przepuszcza „../" bez slowa.
        var (api, _) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));

        File.WriteAllText(Path.Combine(_root, p.Id, "sekret.html"), "<html>tajne</html>");

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => api.ChooseProposalAsync(p.Id, "../sekret"));
        Assert.Null(new ProjectStore(Path.Combine(_root, p.Id)).Load().ChosenProposal);
    }

    [Fact]
    public async Task Rollback_przestawia_biezaca_wersje_nie_kasujac_historii()
    {
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));

        await api.RollbackAsync(p.Id, 1);

        var stan = await api.GetAsync(p.Id);
        Assert.Equal(1, stan.CurrentVersion);
        Assert.Equal(2, stan.Versions.Count);        // historia zostaje
        Assert.Equal(1, stan.RoundsUsed);            // powrot nie zuzywa rundy

        // Podglad idzie ZA CurrentVersion: work/ ma wrocic do zawartosci v1.
        var work = new VersionStore(Path.Combine(_root, p.Id)).WorkDir;
        Assert.True(File.Exists(Path.Combine(work, "index.html")));
    }

    [Fact]
    public async Task Runda_po_powrocie_tworzy_wersje_3_a_nie_nadpisuje_2()
    {
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));
        await api.RollbackAsync(p.Id, 1);
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k2")]));

        var stan = await api.GetAsync(p.Id);
        Assert.Equal([1, 2, 3], stan.Versions.Select(v => v.Number));
        Assert.Equal(3, stan.CurrentVersion);
        Assert.Equal(1, stan.Versions.First(v => v.Number == 3).BasedOn);
    }

    [Fact]
    public async Task Drugie_zadanie_na_tym_samym_projekcie_jest_odrzucane()
    {
        // Plan produktu, sekcja 7: jedno zadanie na projekt. Dwa rownolegle
        // przebiegi pisalyby do tego samego work/.
        var (api, runner) = Zbuduj();
        var p = await Gotowy(api);

        var brama = new TaskCompletionSource();
        runner(p.Id).Wstrzymaj = brama;

        var pierwsze = await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]);
        Assert.Equal("running", (await Trwa(api, pierwsze)).Status);

        await Assert.ThrowsAsync<JobRunningException>(() =>
            api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k2")]));

        // Dwie rzeczy naraz, obie o kolejnosci zapisu wzgledem kolejki:
        //
        // (a) k1 JEST juz na dysku, mimo ze zadanie dopiero stoi na bramie. To
        //     dowodzi, ze zapis uwag dzieje sie PRZED wrzuceniem do kolejki — gdyby
        //     siedzial w lambdzie, brama trzymalaby go i lista bylaby pusta.
        //     Na tym stoi gwarancja 4.4: awaria rundy nie zabiera uwag.
        //
        // (b) k2 NIE zostalo zapisane. Odrzucone zadanie to nie jest nieudana
        //     runda: klient dostal 409, tekst ma dalej w obwodzie Blazora, a runda
        //     z tymi uwagami nigdy nie ruszyla. Zapis w tym miejscu nic nie dawal,
        //     a scieral sie z ApplyResults konczacej sie rundy — obie metody to
        //     czytaj-zmodyfikuj-zapisz na tym samym pliku.
        var poOdrzuceniu = await api.GetCommentsAsync(p.Id, 1);
        Assert.Contains(poOdrzuceniu, u => u.Id == "k1" && u.Status == "open");
        Assert.DoesNotContain(poOdrzuceniu, u => u.Id == "k2");

        // Rollback w trakcie zadania tez odpada: podmienialby work/ pod modelem.
        await Assert.ThrowsAsync<JobRunningException>(() => api.RollbackAsync(p.Id, 1));

        brama.SetResult();
        await Poczekaj(api, pierwsze);
    }

    [Fact]
    public async Task Nieudana_runda_oddaje_liczbe_prob_z_silnika_a_nie_stala()
    {
        // Kontrakt 4.3: UI rozgalezia sie po `handling`, ale `attempts` tez wychodzi
        // do klienta w Job.failure. Wczesniej awaria wracala wyjatkiem i kolejka
        // wpisywala 1 na sztywno — API klamalo w polu, ktore samo wystawia.
        var (api, runner) = Zbuduj();
        var p = await Gotowy(api);
        runner(p.Id).Awaria = new JobFailure(FailureHandling.Halted, "model padl dwa razy", Attempts: 2);

        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]);
        await Poczekaj(api, jobId);

        var job = await api.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status);
        Assert.Equal("halted", job.Failure!.Handling);
        Assert.Equal(2, job.Failure.Attempts);

        // Gwarancja 4.4 przy okazji: uwaga zostaje `open`, licznik rund stoi.
        Assert.Contains(await api.GetCommentsAsync(p.Id, 1), u => u.Id == "k1" && u.Status == "open");
    }

    [Fact]
    public async Task Sprzatanie_po_zakonczonym_zadaniu_nie_zdejmuje_rezerwacji_nastepnego()
    {
        // Kolejka zwalnia rezerwacje projektu PRZED ogloszeniem stanu koncowego, a
        // `finally` zostaje jako siatka. Gdyby ta siatka kasowala po samym KLUCZU,
        // zdjelaby rezerwacje zadania, ktore zdazylo ja w miedzyczasie zalozyc —
        // i dwie rundy pojechalyby rownolegle po tym samym work/, kazda liczac
        // `version != CurrentVersion` na stanie sprzed zatwierdzenia drugiej.
        // Klient traci dwie rundy z pietnastu i dwa razy placi.
        var (api, runner) = Zbuduj();
        var p = await Gotowy(api);

        // Pierwsza runda przechodzi do konca — jej sprzatanie jest juz za nami.
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));

        // Druga staje na bramie i TRZYMA rezerwacje.
        var brama = new TaskCompletionSource();
        runner(p.Id).Wstrzymaj = brama;
        var drugie = await api.ApplyCommentsAsync(p.Id, 2, [Uwaga("k2")]);
        Assert.Equal("running", (await Trwa(api, drugie)).Status);

        Assert.True(_queue.MaAktywne(p.Id));
        await Assert.ThrowsAsync<JobRunningException>(() =>
            api.ApplyCommentsAsync(p.Id, 2, [Uwaga("k3")]));

        brama.SetResult();
        await Poczekaj(api, drugie);
    }

    [Fact]
    public void Zamknieta_kolejka_nie_oddaje_zadania_ktorego_nikt_nie_podniesie()
    {
        // Po Dispose kanal jest zamkniety i TryWrite zwraca `false`. Zignorowanie
        // tego oddawalo klientowi `jobId` zadania, ktorego nikt nigdy nie podniesie:
        // `queued` na zawsze i rezerwacja projektu zajeta na zawsze, wiec projekt
        // do konca zycia procesu odmawia kazdej nastepnej rundy.
        //
        // Wlasna kolejka, nie `_queue` z fixture'a: ta jest zamykana w Dispose,
        // a drugi Dispose wola Cancel na juz zwolnionym CancellationTokenSource.
        var kolejka = new JobQueue();
        kolejka.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => kolejka.Enqueue("p1", (_, _) => Task.CompletedTask));
        Assert.False(kolejka.MaAktywne("p1"));
    }

    [Fact]
    public async Task Wybor_propozycji_w_trakcie_zadania_jest_odrzucany()
    {
        // `ChooseProposalAsync` robi ten sam czytaj-zmodyfikuj-zapisz na project.json
        // co koniec rundy. Bez tej strazy kto zapisze drugi, wygrywa calym plikiem —
        // a `RollbackAsync` i `ApplyCommentsAsync` juz sie tak bronily. Asymetria
        // byla przypadkiem, nie decyzja.
        var (api, runner) = Zbuduj();
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));
        await api.ChooseProposalAsync(p.Id, "b");

        var brama = new TaskCompletionSource();
        runner(p.Id).Wstrzymaj = brama;
        var jobId = await api.CreateFirstVersionAsync(p.Id);
        Assert.Equal("running", (await Trwa(api, jobId)).Status);

        await Assert.ThrowsAsync<JobRunningException>(() => api.ChooseProposalAsync(p.Id, "a"));
        Assert.Equal("b", new ProjectStore(Path.Combine(_root, p.Id)).Load().ChosenProposal);

        brama.SetResult();
        await Poczekaj(api, jobId);
    }

    [Fact]
    public async Task Druga_wersja_pierwsza_jest_odmawiana_od_reki_a_nie_zadaniem_failed()
    {
        // Straz jest tez w silniku i pieniadze sa przez nia bezpieczne, ale silnik
        // odrzuca dopiero W ZADANIU — klient dostawal `failed` zamiast odmowy od reki,
        // jak przy kazdej innej strazy tej warstwy. Atrapa silnikowej strazy NIE ma,
        // wiec bez tej w ProjectApi powstalaby pelnoprawna wersja 2 ZA DARMO:
        // `first` nie zuzywa rundy klienta (ruling 7).
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.CreateFirstVersionAsync(p.Id));

        var stan = await api.GetAsync(p.Id);
        Assert.Single(stan.Versions);
        Assert.Equal(0, stan.RoundsUsed);
    }

    [Fact]
    public async Task Nieznany_projekt_to_404_a_nie_500_o_uszkodzonym_pliku()
    {
        // `ProjectStore.Load` na nieistniejacym pliku rzuca InvalidDataException
        // („project.json uszkodzony") — czyli 500 z komunikatem o zepsutych danych
        // tam, gdzie danych po prostu nie ma. Obie te metody czytaly store wprost,
        // z pominieciem `Wczytaj`.
        var (api, _) = Zbuduj();
        var nieistniejacy = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => api.ChooseProposalAsync(nieistniejacy, "b"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => api.RollbackAsync(nieistniejacy, 1));
    }

    [Fact]
    public async Task Powrot_do_wersji_bez_snapshotu_to_404_a_nie_500()
    {
        // Wersja jest na liscie, ale katalogu na dysku nie ma (reczne sprzatanie,
        // przerwany zapis). `VersionStore.Restore` rzucal wtedy
        // DirectoryNotFoundException — 500 „cos padlo" zamiast 404 „nie ma czego
        // przywrocic". Dla klienta to ta sama sytuacja co zly numer wersji.
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));

        var versions = new VersionStore(Path.Combine(_root, p.Id));
        Directory.Delete(versions.SnapshotPath(1), recursive: true);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => api.RollbackAsync(p.Id, 1));
    }

    [Fact]
    public async Task Nieudany_zapis_przy_powrocie_cofa_work_zamiast_zostawic_rozjazd()
    {
        // Restore i Save musza sie udac albo obie, albo zadna. Rozjazd jest grozny
        // CICHO: work/ z plikami v1 i project.json mowiacy `CurrentVersion = 2` nie
        // psuje podgladu (ten serwuje snapshoty), ale NASTEPNA runda pojedzie po
        // plikach v1, zapisze `BasedOn = 2` i policzy zmiany wzgledem v2 — klient
        // zobaczy „zmienilo sie wszystko" i poprawki naniesione na tresc, ktorej
        // nie ogladal. Obie kolejnosci zostawiaja niespojnosc, wiec rozstrzyga
        // tylko cofniecie.
        var (api, _) = Zbuduj();
        var p = await Gotowy(api);
        await Poczekaj(api, await api.ApplyCommentsAsync(p.Id, 1, [Uwaga("k1")]));

        // `ProjectStore.Save` pisze najpierw project.json.tmp, potem Move. Katalog
        // o tej nazwie sprawia, ze WriteAllText rzuca, a Move nigdy nie leci —
        // czyli dokladnie „Restore przeszlo, Save padlo".
        Directory.CreateDirectory(Path.Combine(_root, p.Id, "project.json.tmp"));

        await Assert.ThrowsAnyAsync<Exception>(() => api.RollbackAsync(p.Id, 1));

        var work = new VersionStore(Path.Combine(_root, p.Id)).WorkDir;
        Assert.Contains("v2", File.ReadAllText(Path.Combine(work, "index.html")));
        Assert.Equal(2, new ProjectStore(Path.Combine(_root, p.Id)).Load().CurrentVersion);
    }

    private static CommentDto Uwaga(string id) =>
        new(id, "hero", "tekst " + id, "desktop", DateTimeOffset.UnixEpoch, "open", null);

    private static async Task<JobView> Trwa(ProjectApi api, string jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await api.GetJobAsync(jobId);
            if (job.Status == "running") return job;
            await Task.Delay(25);
        }
        Assert.Fail("zadanie nie weszlo w running");
        return null!;
    }

    private async Task<ProjectView> Gotowy(ProjectApi api)
    {
        var p = await api.CreateAsync("idea", "kwiaciarnia");
        await Poczekaj(api, await api.RequestProposalsAsync(p.Id));
        await api.ChooseProposalAsync(p.Id, "b");
        await Poczekaj(api, await api.CreateFirstVersionAsync(p.Id));
        return p;
    }
}

/// <summary>
/// Logger z notatnikiem. Formatuje wpis dokladnie tak, jak zrobilby to prawdziwy
/// dostawca logow, wiec test sprawdza TRESC, ktora ktos przeczyta o 2 w nocy —
/// a nie sam fakt, ze cos zawolano.
/// </summary>
internal sealed class ZapisujacyLogger : ILogger<ProjectApi>
{
    public List<string> Bledy { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error) Bledy.Add(formatter(state, exception));
    }
}
