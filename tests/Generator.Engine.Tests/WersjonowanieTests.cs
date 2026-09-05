using System.Text.Json;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Xunit;

namespace Generator.Engine.Tests;

public class WersjonowanieTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "gen-wersje-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_project)) Directory.Delete(_project, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static VersionMeta V(int n, int? basedOn = null) =>
        new(n, $"s-{n}", $"/tmp/v{n}", 0.1m, [], basedOn);

    [Fact]
    public void Numer_nastepnej_wersji_to_max_plus_jeden_nie_liczba_wersji()
    {
        // Po powrocie do v1 obie liczby sie rozjezdzaja: wersji sa dwie, wiec
        // Count+1 dalby 3 — przypadkiem poprawnie. Dopiero PO trzeciej wersji
        // i drugim powrocie Count+1 zaczyna kolidowac. Dlatego trzy wersje.
        var meta = Pusty() with { Versions = [V(1), V(2), V(3)], CurrentVersion = 1 };
        Assert.Equal(4, meta.NextVersionNumber);

        // A tu Count+1 dalby 3 — numer, ktory JUZ ISTNIEJE.
        var zLuka = Pusty() with { Versions = [V(1), V(2), V(5)], CurrentVersion = 5 };
        Assert.Equal(6, zLuka.NextVersionNumber);
    }

    [Fact]
    public void Current_wskazuje_wersje_biezaca_a_nie_ostatnia()
    {
        var meta = Pusty() with { Versions = [V(1), V(2)], CurrentVersion = 1 };
        Assert.Equal(1, meta.Current!.Number);
    }

    [Fact]
    public void Current_jest_null_gdy_nie_ma_zadnej_wersji()
    {
        Assert.Null(Pusty().Current);
        Assert.Equal(1, Pusty().NextVersionNumber);
    }

    [Fact]
    public void Load_ustawia_CurrentVersion_w_starym_project_json()
    {
        // project.json sprzed tej zmiany nie ma pola currentVersion. Bez migracji
        // deserializuje sie na 0 i podglad pokazywalby "brak wersji" na projekcie,
        // ktory ma ich dwie. Smoke-projekt z Planu A jest takim plikiem.
        Directory.CreateDirectory(_project);
        var stary = """
            {
              "Id": "p1", "Token": "t1", "Source": "Idea",
              "WorkspaceDir": "/tmp/work", "Status": "Active",
              "RoundsUsed": 2, "RoundsLimit": 15,
              "SpentUsd": 0.4, "BudgetUsd": 8.0,
              "Versions": [
                { "Number": 1, "SessionId": "s-1", "SnapshotDir": "/tmp/v1",
                  "CostUsd": 0.2, "OrphanedAnchors": [] },
                { "Number": 2, "SessionId": "s-1", "SnapshotDir": "/tmp/v2",
                  "CostUsd": 0.2, "OrphanedAnchors": [] }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(_project, "project.json"), stary);

        var wczytany = new ProjectStore(_project).Load();

        Assert.Equal(2, wczytany.CurrentVersion);
        Assert.Null(wczytany.Versions[0].BasedOn);
    }

    [Fact]
    public void Load_nie_podnosi_CurrentVersion_gdy_projekt_nie_ma_wersji()
    {
        // Straznik przed "napraw wszystko na max": projekt bez wersji ma miec 0.
        var store = new ProjectStore(_project);
        store.Create(SourceKind.Idea);
        Assert.Equal(0, store.Load().CurrentVersion);
    }

    [Fact]
    public void Load_szanuje_zapisany_powrot()
    {
        // Migracja nie moze nadpisywac SWIADOMEGO powrotu. Bez tego testu
        // "CurrentVersion == 0 ? max : zapisane" mozna zwinac do "zawsze max"
        // i rollback cicho przestaje dzialac po kazdym odczycie z dysku.
        var store = new ProjectStore(_project);
        var meta = store.Create(SourceKind.Idea) with
        {
            Versions = [V(1), V(2)],
            CurrentVersion = 1,
        };
        store.Save(meta);

        Assert.Equal(1, store.Load().CurrentVersion);
    }

    [Fact]
    public void Opis_klienta_przezywa_zapis_i_odczyt()
    {
        var store = new ProjectStore(_project);
        var meta = store.Create(SourceKind.Url) with
        {
            Description = "salon fryzjerski, Kraków",
            SourceUrl = "https://example.test/stara",
        };
        store.Save(meta);

        var wczytany = store.Load();
        Assert.Equal("salon fryzjerski, Kraków", wczytany.Description);
        Assert.Equal("https://example.test/stara", wczytany.SourceUrl);
    }

    private ProjectMeta Pusty() => new(
        "p1", "t1", SourceKind.Idea, "/tmp/work", ProjectStatus.Active,
        RoundsUsed: 0, RoundsLimit: 15, SpentUsd: 0m, BudgetUsd: 8m, Versions: []);
}
