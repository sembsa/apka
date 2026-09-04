using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

public class EditorPageTests : BunitContext
{
    /// <summary>
    /// Zawsze zaklada projekt z JEDNA udana wersja, a dopiero potem przestawia wynik
    /// kolejnych rund. Plan mial tu Setup(outcome) uzywane od razu — przy Halted
    /// pierwsze ApplyCommentsAsync nie tworzylo wersji, wiec Versions[^1] wybuchalo.
    /// </summary>
    private async Task<(MockProjectApi api, string id)> Setup(MockJobOutcome dalsze = MockJobOutcome.Success)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");
        await api.ApplyCommentsAsync(p.Id, 1, []);      // wersja 1 istnieje
        api.NextJobOutcome = dalsze;
        return (api, p.Id);
    }

    [Fact]
    public async Task Klient_nie_ma_zadnego_pola_do_edycji_strony()
    {
        // Sekcja 1 planu: w edytorze klient NIE poprawia sam.
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.Empty(cut.FindAll("[contenteditable='true']"));
        Assert.Empty(cut.FindAll("input[type='color']"));
        Assert.Empty(cut.FindAll("select[name='font']"));
    }

    [Fact]
    public async Task Pokazuje_licznik_rund_ale_nigdy_budzetu()
    {
        // Kontrakt 4.1: budzet widzimy tylko my.
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.Contains("poprawk", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("$", cut.Markup);
        Assert.DoesNotContain("budżet", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usd", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kontrakt 4.4 — GWARANCJA, nie konsekwencja. Skladaja sie na nia trzy niezalezne
    /// mechanizmy: snapshot zostawia poprzednia wersje nietknieta, status komentarza
    /// zmienia tylko silnik, licznik rund podbija sie tylko przy udanej rundzie.
    /// Dlatego jeden test i trzy asercje razem: gdy ktos kiedys zdecyduje, ze "powtorka
    /// zjada runde dla ograniczenia kosztu", ma zlamac te regule wprost, a nie rozmyc
    /// ja po cichu w trzech osobnych plikach.
    /// </summary>
    [Fact]
    public async Task Nieudana_runda_nie_zabiera_klientowi_niczego()
    {
        var (api, id) = await Setup(MockJobOutcome.Halted);
        var przed = await api.GetAsync(id);
        var wersjaPrzed = przed.Versions[^1].Number;
        var rundyPrzed = przed.RoundsUsed;
        api.SeedComments(id, wersjaPrzed, "telefon za mało widoczny", "cennik nad opiniami");

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));

        await cut.Find("[data-test='popraw']").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("u nas", cut.Markup));

        var po = await api.GetAsync(id);
        // 1. podglad nietkniety - klient nadal oglada te sama wersje
        Assert.Equal(wersjaPrzed, po.Versions[^1].Number);
        Assert.Contains($"/preview/{id}/{wersjaPrzed}", cut.Find("iframe").GetAttribute("src")!);
        // 2. komentarze zostaja, i zostaja open - silnik nie wydal raportu per commentId,
        //    wiec nikt nie mial prawa zmienic statusu (kontrakt 3.3)
        var uwagi = await api.GetCommentsAsync(id, wersjaPrzed);
        Assert.Equal(2, uwagi.Count);
        Assert.All(uwagi, k => Assert.Equal("open", k.Status));
        // 3. licznik rund sie nie ruszyl (kontrakt 4.1: budzet tak, rundy nie)
        Assert.Equal(rundyPrzed, po.RoundsUsed);
    }

    /// <summary>
    /// Decyzja Przemka 2026-09-04: klient MA widziec, co zrobilismy z jego uwagami.
    /// Wczesniej raport `applied` + Note, ktory silnik uczciwie wystawia, nie trafial mu
    /// przed oczy — uwagi sa zakotwiczone w wersji, a po rundzie klient patrzy juz na
    /// nastepna. Czytamy wiec uwagi poprzedniej wersji, nie tylko biezacej.
    /// </summary>
    [Fact]
    public async Task Po_udanej_rundzie_klient_widzi_co_zrobilismy_z_uwagami()
    {
        var (api, id) = await Setup();
        var wersjaPrzed = (await api.GetAsync(id)).Versions[^1].Number;
        api.SeedComments(id, wersjaPrzed, "telefon za mało widoczny");

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='popraw']")));
        await cut.Find("[data-test='popraw']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='zrobione']")));
        var raport = cut.Find("[data-test='zrobione']").TextContent;
        Assert.Contains("telefon za mało widoczny", raport);
        Assert.Contains("Zrobione", raport);
    }

    [Fact]
    public async Task Raport_zostaje_po_odswiezeniu_strony()
    {
        // Gdyby zyl tylko w polu komponentu, pierwszy F5 kasowalby klientowi
        // potwierdzenie. Renderujemy od zera, bez klikania.
        var (api, id) = await Setup();
        var wersjaPrzed = (await api.GetAsync(id)).Versions[^1].Number;
        api.SeedComments(id, wersjaPrzed, "cennik nad opiniami");
        await api.ApplyCommentsAsync(id, wersjaPrzed,
            await api.GetCommentsAsync(id, wersjaPrzed));

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='zrobione']")));
        Assert.Contains("cennik nad opiniami", cut.Find("[data-test='zrobione']").TextContent);
    }

    [Fact]
    public async Task Bez_historii_nie_ma_pustej_sekcji_raportu()
    {
        // Wersja 1: nie bylo jeszcze zadnej rundy, wiec nie ma o czym raportowac.
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");

        var cut = Render<Editor>(ps => ps.Add(x => x.ProjectId, p.Id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.Empty(cut.FindAll("[data-test='zrobione']"));
    }

    [Fact]
    public async Task Osierocone_kotwice_sa_widoczne_nigdy_wyciszane()
    {
        // Kontrakt 3.4: niepusta orphanedAnchors[] JEST widoczna w UI.
        var (api, id) = await Setup();
        var p = await api.GetAsync(id);
        api.ForceOrphaned(id, p.Versions[^1].Number, ["cennik"]);

        var cut = Render<Editor>(ps => ps.Add(x => x.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='osierocone']")));
        Assert.Contains("cennik", cut.Find("[data-test='osierocone']").TextContent);
    }

    [Fact]
    public async Task Frozen_zostawia_podglad_ale_blokuje_nowa_runde()
    {
        // Kontrakt 4.5: zamrozenie, nie odciecie.
        var (api, id) = await Setup();
        api.ForceFrozen(id);

        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        Assert.NotNull(cut.Find("[data-test='zamrozone']"));
        Assert.True(cut.Find("[data-test='popraw']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Przycisk_popraw_jest_nieaktywny_bez_uwag()
    {
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='popraw']")));
        Assert.True(cut.Find("[data-test='popraw']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Mostek_dostaje_id_iframe_z_ktorego_wolno_przyjmowac_klikniecia()
    {
        // Shim odrzuca wiadomosci z innych okien po event.source, a zna nasz iframe
        // tylko przez to id. Bez przekazania go zaden klik nie dotarlby do .NET.
        var (_, id) = await Setup();
        var cut = Render<Editor>(ps => ps.Add(p => p.ProjectId, id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("iframe")));
        var iframeId = cut.Find("iframe").GetAttribute("id");
        Assert.False(string.IsNullOrWhiteSpace(iframeId));
    }
}
