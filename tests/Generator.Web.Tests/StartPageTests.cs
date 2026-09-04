using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

public class StartPageTests : BunitContext
{
    private MockProjectApi Register()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        Services.AddSingleton<IProjectApi>(api);
        return api;
    }

    [Fact]
    public void Domyslnie_pyta_o_opis_pomyslu()
    {
        Register();
        var cut = Render<Start>();

        Assert.Contains("Opowiedz", cut.Markup);
        Assert.NotNull(cut.Find("textarea"));
    }

    [Fact]
    public void Przelacznik_pokazuje_pole_na_adres_strony()
    {
        Register();
        var cut = Render<Start>();

        cut.Find("[data-test='tryb-url']").Click();

        Assert.NotNull(cut.Find("input[type='url']"));
    }

    [Fact]
    public void Nie_pyta_o_technologie_fonty_ani_kolory()
    {
        // Sekcja 1 planu: klient NIC nie robi sam. Te pytania to nie jego praca.
        Register();
        var cut = Render<Start>();

        foreach (var slowo in new[] { "font", "kolor", "framework", "CSS", "szablon" })
            Assert.DoesNotContain(slowo, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pusty_formularz_nie_tworzy_projektu()
    {
        Register();
        var cut = Render<Start>();

        cut.Find("form").Submit();

        Assert.Contains("Napisz", cut.Markup);   // komunikat, nie wyjątek
    }
}
