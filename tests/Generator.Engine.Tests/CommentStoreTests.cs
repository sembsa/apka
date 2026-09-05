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
        store.Upsert(1, [C("k1", "za ciemno"), C("k2", "telefon maly")]);

        var wczytane = new CommentStore(_project).ForVersion(1);

        Assert.Equal(2, wczytane.Count);
        Assert.All(wczytane, k => Assert.Equal(CommentStatus.Open, k.Status));
        Assert.Equal("za ciemno", wczytane[0].Text);
    }

    [Fact]
    public void Uwagi_sa_odrebne_per_wersja()
    {
        var store = new CommentStore(_project);
        store.Upsert(1, [C("k1", "do v1")]);
        store.Upsert(2, [C("k2", "do v2")]);

        Assert.Equal("k1", Assert.Single(store.ForVersion(1)).Id);
        Assert.Equal("k2", Assert.Single(store.ForVersion(2)).Id);
        Assert.Empty(store.ForVersion(7));
    }

    [Fact]
    public void Powtorzony_zapis_tego_samego_id_nie_duplikuje_i_nie_gubi_statusu()
    {
        // Kontrakt 3.2: podwojne klikniecie „Popraw to" wysyla te same Id.
        // Bez upsertu po Id dostalibysmy dwie kopie uwagi, a druga skasowalaby
        // status ustawiony przez silnik przy pierwszej rundzie.
        var store = new CommentStore(_project);
        store.Upsert(1, [C("k1", "za ciemno")]);
        store.ApplyResults(1, [new CommentResult("k1", CommentStatus.Applied, "Rozjasnilem.")]);

        store.Upsert(1, [C("k1", "za ciemno")]);

        var k = Assert.Single(store.ForVersion(1));
        Assert.Equal(CommentStatus.Applied, k.Status);
        Assert.Equal("Rozjasnilem.", k.Note);
    }

    [Fact]
    public void Uwaga_bez_wpisu_w_raporcie_zostaje_open()
    {
        // Kontrakt 3.3 / 4.4.2: nie zgadujemy za model.
        var store = new CommentStore(_project);
        store.Upsert(1, [C("k1", "raz"), C("k2", "dwa")]);
        store.ApplyResults(1, [new CommentResult("k1", CommentStatus.Rejected, "Nie da sie.")]);

        var wczytane = store.ForVersion(1);
        Assert.Equal(CommentStatus.Rejected, wczytane.First(x => x.Id == "k1").Status);
        Assert.Equal(CommentStatus.Open, wczytane.First(x => x.Id == "k2").Status);
        Assert.Null(wczytane.First(x => x.Id == "k2").Note);
    }

    [Fact]
    public void Raport_o_nieznanym_id_jest_ignorowany()
    {
        // Ochrona przed wstrzyknieciem: raport przychodzi z tekstu modelu, ktory
        // widzial tresc pisana przez klienta. Nieznane Id nie moze zalozyc uwagi.
        var store = new CommentStore(_project);
        store.Upsert(1, [C("k1", "raz")]);
        store.ApplyResults(1, [new CommentResult("obcy", CommentStatus.Applied, "hm")]);

        Assert.Equal("k1", Assert.Single(store.ForVersion(1)).Id);
    }
}
