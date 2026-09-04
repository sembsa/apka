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

    /// <summary>
    /// Znalezione przeklikaniem. Po powrocie do wersji, z ktorej juz raz zglaszano uwagi,
    /// `Reload` wciaga te uwagi z powrotem na liste — razem z tymi ZASTOSOWANYMI. Runda
    /// wysylala cala liste, wiec model „poprawial" ponownie to, co bylo juz zrobione:
    /// klient placi runde za prace wykonana, a podswietlenie pokazuje zmiany, o ktore
    /// nikt tym razem nie prosil. Testy tego nie widzialy, bo wolaly API z czysta lista.
    /// </summary>
    [Fact]
    public async Task Runda_po_powrocie_wysyla_tylko_nowe_uwagi()
    {
        var (api, id) = await DwieWersje();      // v2 powstala z uwagi do `hero`
        await api.RollbackAsync(id, 1);

        // Stan po powrocie: wersja 1 niesie uwage ZASTOSOWANA (hero, z pierwszej rundy)
        // i jedna nowa, jeszcze nie wykonana (kontakt).
        api.SeedComment(id, 1, "hero", "nazwa za mało wyróżniona", "applied");
        api.SeedComment(id, 1, "kontakt", "telefon za mało widoczny", "open");

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.False(
            cut.Find("[data-test='popraw']").HasAttribute("disabled")));

        await cut.Find("[data-test='popraw']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(
            $"/preview/{id}/3", cut.Find("iframe").GetAttribute("src")!));

        var v3 = (await api.GetAsync(id)).Versions.Single(v => v.Number == 3);
        Assert.Contains("kontakt", v3.ChangedAnchors!);
        Assert.DoesNotContain("hero", v3.ChangedAnchors!);

        // Nie wyslac czegos to jedno, a skasowac to drugie: raport o `hero` musi zostac
        // w wersji 1, bo to on jest dowodem, ze prosba klienta zostala wykonana.
        var uwagiV1 = await api.GetCommentsAsync(id, 1);
        Assert.Contains(uwagiV1, c => c.Anchor == "hero" && c.Status == "applied");
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


    /// <summary>
    /// Model „bez kont, token w linku" (decyzja 3 planu) znaczy, ze klient MOZE wejsc na
    /// link, ktorego serwer nie zna — zly token, usuniety projekt, u nas restart aplikacji.
    /// To normalna sytuacja klienta, a nie awaria, wiec nie moze konczyc sie 500.
    /// </summary>
    [Fact]
    public void Nieznany_link_daje_komunikat_a_nie_wyjatek()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProjectApi>(new MockProjectApi { SimulatedDelay = TimeSpan.Zero });

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, "nie-ma-takiego"));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='nie-znaleziono']")));
        Assert.Empty(cut.FindAll("iframe"));
    }
    public new void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
