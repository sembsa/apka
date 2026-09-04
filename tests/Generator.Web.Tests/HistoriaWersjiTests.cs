using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>Kontrakt 5.1 i 5.3 widziane oczami klienta.</summary>
public class HistoriaWersjiTests : BunitContext, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gen-hist-" + Guid.NewGuid().ToString("N"));

    private static CommentDto C(string anchor, string text) =>
        new(Guid.NewGuid().ToString(), anchor, text, "desktop", DateTimeOffset.UnixEpoch, "open", null);

    /// Projekt z dwiema wersjami: v1 wyjsciowa, v2 z poprawionym blokiem `hero`.
    private async Task<(MockProjectApi api, string id)> DwieWersje()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");
        await api.ApplyCommentsAsync(p.Id, 1, [C("hero", "nazwa większa")]);
        return (api, p.Id);
    }

    [Fact]
    public async Task Historia_pokazuje_wszystkie_wersje_i_znaczy_ogladana()
    {
        var (_, id) = await DwieWersje();

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='historia']")));
        Assert.NotNull(cut.Find("[data-test='wersja-1']"));
        Assert.NotNull(cut.Find("[data-test='wersja-2']"));
        Assert.Contains("oglądasz", cut.Find("[data-test='wersja-2']").TextContent);
    }

    [Fact]
    public async Task Przy_jednej_wersji_nie_ma_pustej_historii()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");

        var cut = Render<Editor>(ps => ps.Add(x => x.ProjectId, p.Id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.Empty(cut.FindAll("[data-test='historia']"));
    }

    [Fact]
    public async Task Powrot_przestawia_podglad_na_wybrana_wersje()
    {
        var (_, id) = await DwieWersje();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='wroc-1']")));

        await cut.Find("[data-test='wroc-1']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(
            $"/preview/{id}/1", cut.Find("iframe").GetAttribute("src")!));
    }

    /// <summary>
    /// Powrot nie moze byc pulapka bez wyjscia: wersje nowsze od ogladanej sa PORZUCONE,
    /// nie usuniete (5.1), wiec klient musi widziec droge powrotna do nich.
    /// </summary>
    [Fact]
    public async Task Po_powrocie_da_sie_wrocic_do_nowszej_wersji()
    {
        var (_, id) = await DwieWersje();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='wroc-1']")));
        await cut.Find("[data-test='wroc-1']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='wroc-2']")));
        Assert.NotNull(cut.Find("[data-test='porzucona-2']"));

        await cut.Find("[data-test='wroc-2']").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains(
            $"/preview/{id}/2", cut.Find("iframe").GetAttribute("src")!));
    }

    [Fact]
    public async Task Powrot_jest_zablokowany_gdy_runda_trwa()
    {
        // Konczaca sie runda przestawi wskaznik i cicho uniewaznilaby powrot (5.1).
        // Ten sam predykat, ktory blokuje „Popraw to" — nie czwarte miejsce liczenia.
        var (api, id) = await DwieWersje();
        api.NextJobOutcome = MockJobOutcome.Retrying;
        api.SeedComments(id, 2, "telefon za mało widoczny");

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='popraw']")));
        await cut.Find("[data-test='popraw']").ClickAsync(new());

        cut.WaitForAssertion(() =>
            Assert.True(cut.Find("[data-test='wroc-1']").HasAttribute("disabled")));
    }

    [Fact]
    public async Task Podglad_wersji_ze_zmianami_wlacza_podswietlenie_flaga()
    {
        var (_, id) = await DwieWersje();

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='zmiany']")));
        Assert.Contains("zmienione=true", cut.Find("iframe").GetAttribute("src")!);
    }

    [Fact]
    public async Task Klient_moze_wylaczyc_podswietlenie()
    {
        // Obramowania zaslaniaja wyglad — klient musi umiec zobaczyc strone „na czysto".
        var (_, id) = await DwieWersje();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='pokaz-zmiany']")));

        cut.Find("[data-test='pokaz-zmiany']").Change(false);

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("zmienione", cut.Find("iframe").GetAttribute("src")!));
    }

    [Fact]
    public async Task Pierwsza_wersja_nie_proponuje_pokazania_zmian()
    {
        // Nie ma z czym porownac, a pusty przelacznik byloby dla klienta szumem.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");

        var cut = Render<Editor>(ps => ps.Add(x => x.ProjectId, p.Id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.Empty(cut.FindAll("[data-test='zmiany']"));
    }

    public new void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
