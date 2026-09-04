using Generator.Engine.Versioning;
using Xunit;

namespace Generator.Engine.Tests;

public class VersionStoreTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("gen-ver-").FullName;
    public void Dispose() => Directory.Delete(_project, recursive: true);

    private VersionStore NewStore()
    {
        var store = new VersionStore(_project);
        Directory.CreateDirectory(store.WorkDir);
        return store;
    }

    [Fact]
    public void Commit_kopiuje_katalog_roboczy_do_snapshotu()
    {
        var store = NewStore();
        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "<html></html>");
        Directory.CreateDirectory(Path.Combine(store.WorkDir, "img"));
        File.WriteAllText(Path.Combine(store.WorkDir, "img", "logo.png"), "x");

        var meta = store.Commit(1, "s-1", 0.42m, []);

        Assert.Equal(1, meta.Number);
        Assert.True(File.Exists(Path.Combine(meta.SnapshotDir, "index.html")));
        Assert.True(File.Exists(Path.Combine(meta.SnapshotDir, "img", "logo.png")));
        Assert.Equal(0.42m, meta.CostUsd);
    }

    [Fact]
    public void Snapshot_jest_niezalezny_od_dalszych_zmian_w_katalogu_roboczym()
    {
        // Decyzja 5: nowa wersja powstaje OBOK, poprzednia zostaje nietknieta.
        var store = NewStore();
        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "wersja 1");
        var v1 = store.Commit(1, "s-1", 0.1m, []);

        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "wersja 2");

        Assert.Equal("wersja 1", File.ReadAllText(Path.Combine(v1.SnapshotDir, "index.html")));
    }

    [Fact]
    public void Commit_zapisuje_osierocone_kotwice()
    {
        var store = NewStore();
        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "<html></html>");

        var meta = store.Commit(3, "s-3", 0.2m, ["cennik", "opinia-anna-k"]);

        Assert.Equal(["cennik", "opinia-anna-k"], meta.OrphanedAnchors);
    }

    [Fact]
    public void Powtorny_commit_tego_samego_numeru_nadpisuje_snapshot()
    {
        var store = NewStore();
        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "a");
        store.Commit(1, "s-1", 0.1m, []);
        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "b");

        var again = store.Commit(1, "s-1", 0.1m, []);

        Assert.Equal("b", File.ReadAllText(Path.Combine(again.SnapshotDir, "index.html")));
    }

    [Fact]
    public void Commit_z_nieistniejacym_WorkDir_tworzy_pusty_snapshot()
    {
        var store = new VersionStore(_project);
        // Nie tworzymy WorkDir - testujemy nieistniejący katalog roboczy

        var meta = store.Commit(1, "s-1", 0.1m, []);

        Assert.True(Directory.Exists(meta.SnapshotDir));
        Assert.Empty(Directory.EnumerateFiles(meta.SnapshotDir));
        Assert.Empty(Directory.EnumerateDirectories(meta.SnapshotDir));
    }

    [Fact]
    public void Nadpisanie_snapshotu_czyści_pliki_z_poprzedniej_próby()
    {
        // Gwarancja kontraktu §4.4: "odrzucona próba nie zostawia klientowi niczego"
        var store = NewStore();

        // Pierwsza próba: kilka plików i podkatalog
        File.WriteAllText(Path.Combine(store.WorkDir, "index.html"), "v1");
        File.WriteAllText(Path.Combine(store.WorkDir, "style.css"), "body { }");
        Directory.CreateDirectory(Path.Combine(store.WorkDir, "assets"));
        File.WriteAllText(Path.Combine(store.WorkDir, "assets", "logo.png"), "data");
        var v1 = store.Commit(1, "s-1", 0.1m, []);

        // Czyszczenie i druga próba: inny zestaw plików
        File.Delete(Path.Combine(store.WorkDir, "index.html"));
        File.Delete(Path.Combine(store.WorkDir, "style.css"));
        Directory.Delete(Path.Combine(store.WorkDir, "assets"), recursive: true);
        File.WriteAllText(Path.Combine(store.WorkDir, "new.html"), "v2");

        var v2 = store.Commit(1, "s-1", 0.2m, []);

        // Snapshot powinien zawierać TYLKO pliki z drugiej próby
        Assert.True(File.Exists(Path.Combine(v2.SnapshotDir, "new.html")));
        Assert.False(File.Exists(Path.Combine(v2.SnapshotDir, "index.html")));
        Assert.False(File.Exists(Path.Combine(v2.SnapshotDir, "style.css")));
        Assert.False(Directory.Exists(Path.Combine(v2.SnapshotDir, "assets")));
    }
}
