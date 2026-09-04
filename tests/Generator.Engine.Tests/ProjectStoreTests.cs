using Generator.Engine.Model;
using Generator.Engine.Storage;
using Xunit;

namespace Generator.Engine.Tests;

public class ProjectStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gen-proj-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Create_zapisuje_projekt_z_wartosciami_startowymi()
    {
        var store = new ProjectStore(_dir);
        var meta = store.Create(SourceKind.Idea);

        Assert.Equal(ProjectStatus.Draft, meta.Status);
        Assert.Equal(15, meta.RoundsLimit);      // kontrakt 4.2
        Assert.Equal(8.00m, meta.BudgetUsd);     // kontrakt 4.2
        Assert.Equal(0, meta.RoundsUsed);
        Assert.NotEmpty(meta.Token);
        Assert.True(File.Exists(Path.Combine(_dir, "project.json")));

        // Kontrakt z Taskiem 7: katalog work musi istniec
        var workDir = Path.Combine(_dir, "work");
        Assert.True(Directory.Exists(workDir), $"Katalog {workDir} nie istnieje");
        Assert.Equal(workDir, meta.WorkspaceDir);
    }

    [Fact]
    public void Load_odtwarza_to_co_zapisal_Save()
    {
        var store = new ProjectStore(_dir);
        var created = store.Create(SourceKind.Url);

        // Dodaj версии z prawdziwymi VersionMeta
        var version1 = new VersionMeta(
            Number: 1,
            SessionId: "sess-001",
            SnapshotDir: "/snapshots/v1",
            CostUsd: 0.5m,
            OrphanedAnchors: ["anchor-1", "anchor-2"]);

        var updated = created with
        {
            Status = ProjectStatus.Active,
            SpentUsd = 1.25m,
            Versions = [version1]
        };
        store.Save(updated);

        var loaded = new ProjectStore(_dir).Load();

        // Sprawdzaj wszystkie pola
        Assert.Equal(ProjectStatus.Active, loaded.Status);
        Assert.Equal(1.25m, loaded.SpentUsd);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal(SourceKind.Url, loaded.Source);
        Assert.Equal(created.Token, loaded.Token);
        Assert.Equal(15, loaded.RoundsLimit);
        Assert.Equal(8.00m, loaded.BudgetUsd);

        // Sprawdzaj zagniezdzone Versions
        Assert.Single(loaded.Versions);
        var loadedVersion = loaded.Versions[0];
        Assert.Equal(1, loadedVersion.Number);
        Assert.Equal("sess-001", loadedVersion.SessionId);
        Assert.Equal("/snapshots/v1", loadedVersion.SnapshotDir);
        Assert.Equal(0.5m, loadedVersion.CostUsd);
        Assert.Equal(["anchor-1", "anchor-2"], loadedVersion.OrphanedAnchors);
    }

    [Fact]
    public void Powtorka_zuzywa_budzet_ale_nie_runde()
    {
        // kontrakt 4.1: asymetria licznikow
        var meta = new ProjectStore(_dir).Create(SourceKind.Idea);

        var afterRetry = ProjectStore.WithSpend(meta, 0.20m);
        Assert.Equal(0, afterRetry.RoundsUsed);
        Assert.Equal(0.20m, afterRetry.SpentUsd);

        var afterRound = ProjectStore.WithRoundConsumed(ProjectStore.WithSpend(afterRetry, 0.20m));
        Assert.Equal(1, afterRound.RoundsUsed);
        Assert.Equal(0.40m, afterRound.SpentUsd);
    }

    [Fact]
    public void WithRoundConsumed_nie_zmienia_SpentUsd()
    {
        // Runda zmienia RoundsUsed, ale wcale nie rusza SpentUsd
        var meta = new ProjectStore(_dir).Create(SourceKind.Idea);
        var afterSpend = ProjectStore.WithSpend(meta, 0.5m);

        var afterRound = ProjectStore.WithRoundConsumed(afterSpend);

        Assert.Equal(1, afterRound.RoundsUsed);
        Assert.Equal(0.5m, afterRound.SpentUsd); // bez zmian
    }

    [Fact]
    public void Load_pliku_obciętego_lub_uszkodzonego_rzuca_zrozumialY_blad()
    {
        var store = new ProjectStore(_dir);
        var meta = store.Create(SourceKind.Idea);

        // Podłóż obcięty JSON
        var projectJsonPath = Path.Combine(_dir, "project.json");
        File.WriteAllText(projectJsonPath, "{\"Id\":\"test\",\"incomplete");

        // Load powinien rzucić InvalidDataException z MetaPath w treści
        var ex = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Contains(projectJsonPath, ex.Message);
        Assert.Contains("uszkodzony", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
