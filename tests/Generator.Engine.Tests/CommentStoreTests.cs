using Generator.Engine.Model;
using Generator.Engine.Storage;
using Xunit;

namespace Generator.Engine.Tests;

public class CommentStoreTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "gen-kom-" + Guid.NewGuid().ToString("N"));

    public CommentStoreTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        if (Directory.Exists(_project)) Directory.Delete(_project, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static Comment C(string id, string text) =>
        new(id, "hero", text, "desktop", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Zapisane_uwagi_wracaja_ze_statusem_open()
    {
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "za ciemno"), C("k2", "telefon maly")]);

        var wczytane = new CommentStore(_project).ForVersion(1);

        Assert.Equal(2, wczytane.Count);
        Assert.All(wczytane, k => Assert.Equal(CommentStatus.Open, k.Status));
        Assert.Equal("za ciemno", wczytane[0].Text);
    }

    [Fact]
    public void Uwagi_sa_odrebne_per_wersja()
    {
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "do v1")]);
        store.Uzgodnij(2, [C("k2", "do v2")]);

        Assert.Equal("k1", Assert.Single(store.ForVersion(1)).Id);
        Assert.Equal("k2", Assert.Single(store.ForVersion(2)).Id);
        Assert.Empty(store.ForVersion(7));
    }

    [Fact]
    public void Powtorzony_zapis_tego_samego_id_nie_duplikuje_i_nie_gubi_statusu()
    {
        // Kontrakt 3.2: podwojne klikniecie „Popraw to" wysyla te same Id.
        // Bez uzgadniania po Id dostalibysmy dwie kopie uwagi, a druga skasowalaby
        // status ustawiony przez silnik przy pierwszej rundzie.
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "za ciemno")]);
        store.ApplyResults(1, [new CommentResult("k1", CommentStatus.Applied, "Rozjasnilem.")]);

        store.Uzgodnij(1, [C("k1", "za ciemno")]);

        var k = Assert.Single(store.ForVersion(1));
        Assert.Equal(CommentStatus.Applied, k.Status);
        Assert.Equal("Rozjasnilem.", k.Note);
    }

    [Fact]
    public void Uwaga_bez_wpisu_w_raporcie_zostaje_open()
    {
        // Kontrakt 3.3 / 4.4.2: nie zgadujemy za model.
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "raz"), C("k2", "dwa")]);
        store.ApplyResults(1, [new CommentResult("k1", CommentStatus.Rejected, "Nie da sie.")]);

        var wczytane = store.ForVersion(1);
        Assert.Equal(CommentStatus.Rejected, wczytane.First(x => x.Id == "k1").Status);
        Assert.Equal(CommentStatus.Open, wczytane.First(x => x.Id == "k2").Status);
        Assert.Null(wczytane.First(x => x.Id == "k2").Note);
    }

    [Fact]
    public void ApplyResults_na_wersji_bez_zapisanych_uwag_nic_nie_robi()
    {
        // Runda moze pojsc na samych uwagach globalnych albo zostac uruchomiona
        // ponownie po awarii — wtedy raport przychodzi do wersji, dla ktorej
        // Uzgodnij nigdy nie byl wolany. Ma to byc cichy no-op, nie wyjatek:
        // bez tego straznika `lista` jest null i leci NullReferenceException
        // w srodku zadania, juz PO tym jak model zostal oplacony.
        var store = new CommentStore(_project);

        store.ApplyResults(7, [new CommentResult("k1", CommentStatus.Applied, "x")]);

        Assert.Empty(store.ForVersion(7));
        Assert.False(File.Exists(Path.Combine(_project, "comments.json")));
    }

    [Fact]
    public void ApplyResults_nie_rusza_innych_wersji()
    {
        // Druga polowa tego samego straznika: raport do wersji 7 nie moze
        // skasowac ani przepisac uwag wersji 1.
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "raz")]);

        store.ApplyResults(7, [new CommentResult("k1", CommentStatus.Applied, "x")]);

        var k = Assert.Single(store.ForVersion(1));
        Assert.Equal(CommentStatus.Open, k.Status);
    }

    [Fact]
    public void Rownolegle_zapisy_nie_gubia_uwag()
    {
        // Uzgodnij i ApplyResults to obie sekwencje czytaj-zmodyfikuj-zapisz na tym
        // samym pliku, a CommentStore powstaje na kazde wywolanie — bez blokady
        // per SCIEZKA (nie per egzemplarz) zapisy sie zjadaja. Zaobserwowane
        // w Zadaniu 5: uwaga zapisana i po chwili nadpisana przez raport
        // konczacej sie rundy.
        var store = new CommentStore(_project);

        // Kazde wywolanie do INNEJ wersji, bo `Uzgodnij` z definicji kasuje uwagi
        // `open` spoza swojej listy — czterdziesci uzgodnien tej samej wersji
        // zostawiloby jedna uwage i test nie mowilby nic o blokadzie. Plik jest
        // jeden (comments.json), wiec czterdziesci czytaj-zmodyfikuj-zapisz nadal
        // bije w te sama sciezke: to wlasnie ona jest kluczem zamka.
        Parallel.For(0, 40, i => store.Uzgodnij(i + 1, [C($"k{i}", $"uwaga {i}")]));

        var wczytany = new CommentStore(_project);
        for (var i = 0; i < 40; i++)
            Assert.Equal($"k{i}", Assert.Single(wczytany.ForVersion(i + 1)).Id);
    }

    [Fact]
    public void Wycofana_uwaga_znika_z_dysku_zamiast_wracac_przy_kazdym_odczycie()
    {
        // Frontend umie wycofac uwage, dopoki jest `open` (kontrakt 3.3) — ale robil
        // to tylko w pamieci obwodu, a zapis umial wylacznie dopisywac. Uwaga
        // wycofana po NIEUDANEJ rundzie zostawala wiec `open` w comments.json na
        // zawsze: wracala przy kazdym Reload, odblokowywala przycisk „Popraw to"
        // i wchodzila do nastepnej rundy. Klient placil runde za rzecz, ktora odwolal.
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "raz"), C("k2", "dwa")]);

        store.Uzgodnij(1, [C("k1", "raz")]);

        Assert.Equal("k1", Assert.Single(store.ForVersion(1)).Id);
    }

    [Fact]
    public void Uzgodnienie_nie_rusza_uwag_rozliczonych_przez_silnik()
    {
        // Druga polowa tej samej reguly: `applied` i `rejected` sa wlasnoscia silnika
        // (4.4.2) i stanowia raport „co zrobilismy z Twoimi uwagami". Frontend wysyla
        // tylko `open`, wiec rozliczonych NIGDY nie ma na przychodzacej liscie —
        // kasowanie po samej nieobecnosci wycieraloby klientowi caly raport.
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "raz"), C("k2", "dwa")]);
        store.ApplyResults(1, [
            new CommentResult("k1", CommentStatus.Applied, "Zrobione."),
            new CommentResult("k2", CommentStatus.Rejected, "Nie da sie.")]);

        store.Uzgodnij(1, [C("k3", "trzy")]);

        var wczytane = store.ForVersion(1);
        Assert.Equal(3, wczytane.Count);
        Assert.Equal(CommentStatus.Applied, wczytane.First(x => x.Id == "k1").Status);
        Assert.Equal("Zrobione.", wczytane.First(x => x.Id == "k1").Note);
        Assert.Equal(CommentStatus.Rejected, wczytane.First(x => x.Id == "k2").Status);
        Assert.Equal(CommentStatus.Open, wczytane.First(x => x.Id == "k3").Status);
    }

    [Fact]
    public void Raport_o_nieznanym_id_jest_ignorowany()
    {
        // Ochrona przed wstrzyknieciem: raport przychodzi z tekstu modelu, ktory
        // widzial tresc pisana przez klienta. Nieznane Id nie moze zalozyc uwagi.
        var store = new CommentStore(_project);
        store.Uzgodnij(1, [C("k1", "raz")]);
        store.ApplyResults(1, [new CommentResult("obcy", CommentStatus.Applied, "hm")]);

        Assert.Equal("k1", Assert.Single(store.ForVersion(1)).Id);
    }
}
