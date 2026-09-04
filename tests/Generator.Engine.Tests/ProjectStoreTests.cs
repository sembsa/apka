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
    }

    [Fact]
    public void Load_odtwarza_to_co_zapisal_Save()
    {
        var store = new ProjectStore(_dir);
        var created = store.Create(SourceKind.Url);
        var updated = created with { Status = ProjectStatus.Active, SpentUsd = 1.25m };
        store.Save(updated);

        var loaded = new ProjectStore(_dir).Load();

        Assert.Equal(ProjectStatus.Active, loaded.Status);
        Assert.Equal(1.25m, loaded.SpentUsd);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal(SourceKind.Url, loaded.Source);
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
}
