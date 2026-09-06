using Generator.Engine.IO;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;
using Generator.Web.Api;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Dlug 4 z listy: kolejka zyje w pamieci procesu, wiec restart gubi zadanie
/// w locie. Na naszej maszynie to niedogodnosc; po wystawieniu publicznie KAZDY
/// DEPLOY gubi oplacona runde klienta, ktory zostaje z wiszacym ekranem.
///
/// Wybrana droga: zadanie NIE przezywa restartu, tylko po restarcie mowi prawde.
/// Wznowienie jest niemozliwe — proces `claude` zginal razem z aplikacja, wiec
/// „przezycie" znaczyloby URUCHOMIENIE RUNDY DRUGI RAZ, czyli drugi raz zaplacone,
/// przy ryzyku zdublowanej wersji. Kontrakt 4.3 traktuje nieudana runde jako
/// normalny wynik, a 4.4 gwarantuje, ze nie zabiera klientowi niczego — odzyskiwanie
/// wpina sie w to bez naciagania.
/// </summary>
public class OdzyskiwaniePoStarcieTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-odzysk-" + Guid.NewGuid().ToString("N"));
    private readonly JobQueue _kolejka = new();

    private ProjectPaths Sciezki() => new(_root, "claude");

    private OdzyskiwaniePoStarcie Odzyskiwanie() => new(Sciezki(), _kolejka);

    /// Projekt z jedna zatwierdzona wersja i zadaniem „w locie" — czyli dokladnie
    /// to, co zostaje na dysku, gdy aplikacja zginie w polowie rundy.
    private (string id, string jobId) ProjektWTrakcieRundy(string trescWork = "runda w polowie")
    {
        var id = Guid.NewGuid().ToString();
        var store = new ProjectStore(Path.Combine(_root, id));
        var wersje = new VersionStore(Path.Combine(_root, id));
        Directory.CreateDirectory(wersje.WorkDir);

        File.WriteAllText(Path.Combine(wersje.WorkDir, "index.html"), "wersja 1");
        var v1 = wersje.Commit(1, "s-1", 0.30m, [], null, []);

        // WorkDir po czesciowej rundzie: snapshot v1 zostal, ale work jest w stanie
        // posrednim — model zdazyl cos nadpisac, zanim proces zginal.
        File.WriteAllText(Path.Combine(wersje.WorkDir, "index.html"), trescWork);
        File.WriteAllText(Path.Combine(wersje.WorkDir, "smiec.txt"), "polprodukt");

        var jobId = Guid.NewGuid().ToString();
        store.Save(store.Create(SourceKind.Idea) with
        {
            Id = id,
            Status = ProjectStatus.Active,
            Versions = [v1],
            CurrentVersion = 1,
            ActiveJobId = jobId,
        });
        return (id, jobId);
    }

    [Fact]
    public void Przerwane_zadanie_odpowiada_klientowi_zamiast_udawac_ze_nie_istnialo()
    {
        // Bez tego `GET /api/jobs/{id}` po restarcie daje 404 — czyli „nie znam
        // takiego zadania" komus, kto wlasnie za nie zaplacil.
        var (_, jobId) = ProjektWTrakcieRundy();

        Odzyskiwanie().Wykonaj();

        var zadanie = _kolejka.Get(jobId);
        Assert.NotNull(zadanie);
        Assert.Equal("failed", zadanie!.Status);
        // `halted`, nie `retrying`: powtorki nie bedzie, bo nikt jej nie zamowil.
        Assert.Equal("halted", zadanie.Failure!.Handling);
    }

    [Fact]
    public void Przerwane_zadanie_zna_swoj_projekt_zeby_dalo_sie_sprawdzic_token()
    {
        // `GET /api/jobs/{id}` sprawdza token PROJEKTU zadania. Gdyby odzyskiwanie
        // odtworzylo sam status bez przypisania do projektu, trasa oddawalaby 404
        // mimo poprawnego odzyskania.
        var (id, jobId) = ProjektWTrakcieRundy();

        Odzyskiwanie().Wykonaj();

        Assert.Equal(id, _kolejka.Projekt(jobId));
    }

    [Fact]
    public void Katalog_roboczy_wraca_do_ostatniej_udanej_wersji()
    {
        // Kontrakt 4.4: nieudana runda nie zabiera klientowi niczego. Przerwana
        // runda to nieudana runda — polprodukt nie moze zostac w work/, bo to on
        // idzie do podgladu i do nastepnej rundy.
        var (id, _) = ProjektWTrakcieRundy();
        var work = new VersionStore(Path.Combine(_root, id)).WorkDir;

        Odzyskiwanie().Wykonaj();

        Assert.Equal("wersja 1", File.ReadAllText(Path.Combine(work, "index.html")));
        Assert.False(File.Exists(Path.Combine(work, "smiec.txt")));
    }

    [Fact]
    public void Projekt_przestaje_miec_zadanie_w_locie_wiec_mozna_zamowic_nastepna_runde()
    {
        // Bez wyczyszczenia znacznika projekt zostalby zablokowany na zawsze:
        // kazde nastepne zamowienie widzialoby „masz juz zadanie".
        var (id, _) = ProjektWTrakcieRundy();

        Odzyskiwanie().Wykonaj();

        Assert.Null(new ProjectStore(Path.Combine(_root, id)).Load().ActiveJobId);
        Assert.False(_kolejka.MaAktywne(id));
    }

    [Fact]
    public void Przerwana_wersja_pierwsza_nie_wywraca_odzyskiwania()
    {
        // NAJWAZNIEJSZY przypadek przy swiezym wdrozeniu: przerwana moze byc wersja
        // PIERWSZA, a wtedy `CurrentVersion` to 0 i snapshotu numer 0 nie ma.
        // Naiwne „przywroc wersje biezaca" rzuca tu `DirectoryNotFoundException`
        // — i to dokladnie na projektach, ktore najczesciej sa w locie na nowym
        // serwerze. Uczciwy stan koncowy: zadanie przerwane, projekt zostaje
        // szkicem, nic nie przepadlo, bo nic jeszcze nie istnialo.
        var id = Guid.NewGuid().ToString();
        var store = new ProjectStore(Path.Combine(_root, id));
        var jobId = Guid.NewGuid().ToString();
        store.Save(store.Create(SourceKind.Idea) with
        {
            Id = id, Status = ProjectStatus.Draft, Versions = [], CurrentVersion = 0,
            ActiveJobId = jobId,
        });

        Odzyskiwanie().Wykonaj();

        Assert.Equal("failed", _kolejka.Get(jobId)!.Status);
        var po = store.Load();
        Assert.Null(po.ActiveJobId);
        Assert.Equal(ProjectStatus.Draft, po.Status);
    }

    [Fact]
    public void Projekt_bez_zadania_w_locie_zostaje_nietkniety()
    {
        var id = Guid.NewGuid().ToString();
        var store = new ProjectStore(Path.Combine(_root, id));
        var wersje = new VersionStore(Path.Combine(_root, id));
        Directory.CreateDirectory(wersje.WorkDir);
        File.WriteAllText(Path.Combine(wersje.WorkDir, "index.html"), "spokojny stan");
        var v1 = wersje.Commit(1, "s", 0.1m, [], null, []);
        File.WriteAllText(Path.Combine(wersje.WorkDir, "index.html"), "spokojny stan, ale nowszy");
        store.Save(store.Create(SourceKind.Idea) with
        {
            Id = id, Status = ProjectStatus.Active, Versions = [v1], CurrentVersion = 1,
        });

        Odzyskiwanie().Wykonaj();

        // Zaden powrot do snapshotu: projekt bez zadania w locie nie byl przerwany,
        // wiec jego work/ jest prawda, a nie polproduktem.
        Assert.Equal("spokojny stan, ale nowszy",
            File.ReadAllText(Path.Combine(wersje.WorkDir, "index.html")));
    }

    [Fact]
    public void Jeden_uszkodzony_projekt_nie_zabiera_ze_soba_odzyskiwania_pozostalych()
    {
        // Ta sama zasada co w panelu: odzyskiwanie jest najbardziej potrzebne wtedy,
        // gdy cos jest zepsute. Wyjatek na jednym projekcie nie moze zablokowac
        // startu aplikacji ani zostawic pozostalych klientow z wiszacym ekranem.
        Directory.CreateDirectory(Path.Combine(_root, "zepsuty"));
        File.WriteAllText(Path.Combine(_root, "zepsuty", "project.json"), "{ to nie jest JSON");
        var (_, jobId) = ProjektWTrakcieRundy();

        Odzyskiwanie().Wykonaj();

        Assert.Equal("failed", _kolejka.Get(jobId)!.Status);
    }

    [Fact]
    public void Brak_katalogu_projektow_nie_jest_bledem()
    {
        // Swiezy klon albo pierwszy start na nowym serwerze: `projekty/` jeszcze
        // nie istnieje. Start aplikacji nie moze sie na tym wywrocic.
        new OdzyskiwaniePoStarcie(new ProjectPaths(Path.Combine(_root, "nie-ma"), "claude"), _kolejka)
            .Wykonaj();
    }

    [Fact]
    public void Odzyskane_zadanie_nie_jest_juz_w_toku_wiec_UI_przestaje_czekac()
    {
        // `JobViewExtensions.Trwa` decyduje, czy ekran klienta dalej krec i kolko.
        // Gdyby odzyskane zadanie wracalo jako `retrying`, klient czekalby w
        // nieskonczonosc na wynik, ktorego nikt nie policzy.
        var (_, jobId) = ProjektWTrakcieRundy();

        Odzyskiwanie().Wykonaj();

        var zadanie = _kolejka.Get(jobId);
        Assert.False(zadanie.WTokuDlaKlienta());
        Assert.True(zadanie.Zatrzymane());
    }

    public void Dispose()
    {
        _kolejka.Dispose();
        DirectoryOps.Delete(_root);
        GC.SuppressFinalize(this);
    }
}
