using Generator.Engine.Jobs;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;
using Generator.Web.Api;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

/// Atrapa silnika: nie uruchamia modelu, ale zapisuje snapshoty i metadane tak,
/// jak zrobilby to GenerationService. Testy tej warstwy dotycza kolejki, straznikow
/// i mapowania na DTO — nie bramek, ktore maja wlasne 120 testow w silniku.
internal class FakeRunner(ProjectStore store, VersionStore versions) : IRoundRunner
{
    public List<string> Wywolania { get; } = [];
    public TaskCompletionSource? Wstrzymaj { get; set; }

    public async Task<GenerationOutcome> RunRoundAsync(
        ProjectMeta meta, IReadOnlyList<Comment> comments, CancellationToken ct = default)
    {
        Wywolania.Add("round");
        if (Wstrzymaj is not null) await Wstrzymaj.Task;
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
        Directory.CreateDirectory(versions.WorkDir);
        File.WriteAllText(Path.Combine(versions.WorkDir, "index.html"),
            """<html><body><h1 data-cmt-id="hero">x</h1></body></html>""");

        var n = meta.NextVersionNumber;
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
        return (new ProjectApi(paths, _queue), id => runnery[id]);
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

        // Kontrakt 4.4: uwagi zapisuje sie PRZED kolejka, wiec odrzucone zadanie
        // nie zabiera klientowi tekstu — k2 lezy na dysku jako `open`, mimo ze
        // zadna runda go nie widziala. Ta asercja nie zalezy od tego, GDZIE
        // w zadaniu ktos przeniesie zapis: przy zapisie w zadaniu k2 nie
        // powstanie w ogole, bo zadanie nie ruszylo.
        var poOdrzuceniu = await api.GetCommentsAsync(p.Id, 1);
        Assert.Contains(poOdrzuceniu, u => u.Id == "k2" && u.Status == "open");

        // Rollback w trakcie zadania tez odpada: podmienialby work/ pod modelem.
        await Assert.ThrowsAsync<JobRunningException>(() => api.RollbackAsync(p.Id, 1));

        brama.SetResult();
        await Poczekaj(api, pierwsze);
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
