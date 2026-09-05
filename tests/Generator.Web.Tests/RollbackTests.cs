using Generator.Engine.Versioning;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>Kontrakt 5.1 i 5.2.</summary>
public class RollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gen-rb-" + Guid.NewGuid().ToString("N"));

    private MockProjectApi Api() =>
        new() { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };

    private static CommentDto C(string anchor, string text) =>
        new(Guid.NewGuid().ToString(), anchor, text, "desktop", DateTimeOffset.UnixEpoch, "open", null);

    [Fact]
    public async Task Powrot_przestawia_wskaznik_a_historia_zostaje()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        await api.ApplyCommentsAsync(p.Id, 1, [C("hero", "nazwa większa")]);

        await api.RollbackAsync(p.Id, 1);

        var po = await api.GetAsync(p.Id);
        Assert.Equal(1, po.CurrentVersion);
        Assert.Equal(2, po.Versions.Count);            // nic nie usuniete
        Assert.Equal(1, po.Aktualna()!.Number);
    }

    [Fact]
    public async Task Powrot_nie_zuzywa_rundy()
    {
        // Nie uruchamia modelu, wiec nie ma za co pobierac oplaty (5.1, logika 4.4).
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        await api.ApplyCommentsAsync(p.Id, 1, [C("hero", "nazwa większa")]);
        var rundyPrzed = (await api.GetAsync(p.Id)).RoundsUsed;

        await api.RollbackAsync(p.Id, 1);

        Assert.Equal(rundyPrzed, (await api.GetAsync(p.Id)).RoundsUsed);
    }

    [Fact]
    public async Task Powrot_jest_dozwolony_przy_zamrozonym_projekcie()
    {
        // 4.5 blokuje NOWE rundy, nie ogladanie tego, co klient juz ma.
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        await api.ApplyCommentsAsync(p.Id, 1, [C("hero", "nazwa większa")]);
        api.ForceFrozen(p.Id);

        await api.RollbackAsync(p.Id, 1);

        Assert.Equal(1, (await api.GetAsync(p.Id)).CurrentVersion);
    }

    [Fact]
    public async Task Powrot_do_nieistniejacej_wersji_jest_odrzucany()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)

        await Assert.ThrowsAnyAsync<Exception>(() => api.RollbackAsync(p.Id, 7));
    }

    /// <summary>
    /// TEST ROZSTRZYGAJACY. Po powrocie do v1 kolejna runda tworzy v3, ktorej rodzicem
    /// jest v1 — nie v2. Implementacja liczaca „poprzednia" jako `number - 1` przechodzi
    /// KAZDY przebieg liniowy i klamie dokladnie tutaj, czyli tam, gdzie powrot ma sens.
    /// </summary>
    [Fact]
    public async Task Po_powrocie_rodzicem_nowej_wersji_jest_ta_do_ktorej_wrocono()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        await api.ApplyCommentsAsync(p.Id, 1, [C("hero", "nazwa większa")]);   // v2
        await api.RollbackAsync(p.Id, 1);
        await api.ApplyCommentsAsync(p.Id, 1, [C("kontakt", "telefon większy")]);   // v3

        var po = await api.GetAsync(p.Id);
        var v3 = po.Versions.Single(v => v.Number == 3);
        Assert.Equal(3, po.CurrentVersion);
        Assert.Equal(1, v3.BasedOn);
        Assert.Equal(1, po.Rodzic(v3)!.Number);
    }

    [Fact]
    public async Task Zmienione_bloki_licza_sie_wzgledem_rodzica_nie_wzgledem_numeru_minus_jeden()
    {
        // v3 powstaje z v1, wiec zmiany sa v3-vs-v1. Snapshot v2 nie ma tu nic do rzeczy.
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        await api.ApplyCommentsAsync(p.Id, 1, [C("hero", "nazwa większa")]);
        await api.RollbackAsync(p.Id, 1);
        await api.ApplyCommentsAsync(p.Id, 1, [C("kontakt", "telefon większy")]);

        var v3 = (await api.GetAsync(p.Id)).Versions.Single(v => v.Number == 3);

        Assert.NotNull(v3.ChangedAnchors);
        Assert.Contains("kontakt", v3.ChangedAnchors!);
        Assert.DoesNotContain("hero", v3.ChangedAnchors!);

        // Sama lista zmian tego nie rozstrzyga: przy blednej bazie kumulacja daje
        // przypadkiem ten sam wynik. Rozstrzyga TRESC v3 — powrot do v1 i nowa runda
        // nie moga po cichu wciagnac poprawki z porzuconej v2.
        var html = await File.ReadAllTextAsync(Path.Combine(
            new VersionStore(Path.Combine(_root, p.Id)).SnapshotPath(3), "index.html"));
        Assert.DoesNotContain("""data-cmt-id="hero" style""", html);
        Assert.Contains("""data-cmt-id="kontakt" style""", html);
    }

    [Fact]
    public async Task Pierwsza_wersja_nie_ma_zmienionych_blokow()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)

        var v1 = (await api.GetAsync(p.Id)).Versions.Single();
        Assert.Null(v1.BasedOn);
        Assert.Empty(v1.ChangedAnchors ?? []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
