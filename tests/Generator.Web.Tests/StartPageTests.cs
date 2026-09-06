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
    public async Task Zly_adres_wraca_komunikatem_na_ekran_a_nie_wywala_obwodu()
    {
        // Od kiedy zalozenie projektu POBIERA strone klienta, ten formularz moze sie
        // nie udac z powodu lezacego po jego stronie: literowka, strona wylaczona,
        // link do PDF-a. Nieobsluzony wyjatek zrywa obwod Blazora i klient dostaje
        // pusty ekran — czyli karę za wlasna literowke.
        var api = Register();
        api.BladZrodla = "strona odpowiedziala kodem 404";
        var cut = Render<Start>();

        cut.Find("[data-test='tryb-url']").Click();
        cut.Find("input[type='url']").Change("https://nie-ma-takiej.example/");
        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=alert]")));
        var komunikat = cut.Find("[role=alert]").TextContent;

        Assert.Contains("adresu", komunikat);

        // Powod NIE idzie na ekran: „404" i „siec niedostepna" mowia o naszej
        // infrastrukturze, a klientowi nie podpowiadaja, co zrobic.
        Assert.DoesNotContain("404", komunikat);

        // Przycisk musi wrocic do uzywalnosci — inaczej klient widzi komunikat
        // i nie ma jak poprawic adresu.
        Assert.False(cut.Find("button[type=submit]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Poprawny_adres_prowadzi_dalej_do_propozycji()
    {
        var api = Register();
        var cut = Render<Start>();

        cut.Find("[data-test='tryb-url']").Click();
        cut.Find("input[type='url']").Change("https://konwalia.example/");
        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.Contains(
            "/proposals/", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri));
        Assert.Empty(cut.FindAll("[role=alert]"));
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
