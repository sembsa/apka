using Bunit;
using Generator.Web.Components;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Ekran czekania powstal ze zmierzonej liczby: generacja trzech propozycji zajela
/// 275 s na prawdziwym `claude`. Testy pilnuja tego, co ma trzymac klienta przy
/// stronie przez te niemal piec minut — nie wygladu, tylko trzech obietnic.
/// </summary>
public class CzekanieTests : BunitContext
{
    private static readonly string[] Etapy = ["Czytam opis", "Szkicuję kierunki", "Składam podglądy"];

    private IRenderedComponent<Czekanie> Render(TimeSpan? tik = null, TimeSpan? krok = null) =>
        Render<Czekanie>(ps => ps
            .Add(c => c.Naglowek, "Przygotowuję trzy propozycje")
            .Add(c => c.Etapy, Etapy)
            .Add(c => c.Szacunek, "około 4–5 minut")
            .Add(c => c.Tik, tik ?? TimeSpan.FromMilliseconds(20))
            .Add(c => c.KrokEtapu, krok ?? TimeSpan.FromHours(1)));

    [Fact]
    public void Mowi_ile_to_potrwa_zanim_klient_zacznie_sie_martwic()
    {
        var cut = Render();

        Assert.Contains("około 4–5 minut", cut.Find("[data-test='czas']").TextContent);
    }

    [Fact]
    public void Pokazuje_uplyw_czasu_bo_ruch_jest_dowodem_zycia()
    {
        var cut = Render();

        // Bez tego ekran jest nieodrozninalny od zawieszonej aplikacji.
        cut.WaitForAssertion(
            () => Assert.DoesNotContain("Mija 0 s", cut.Find("[data-test='czas']").TextContent),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Czas_pokazujemy_w_minutach_a_nie_w_setkach_sekund()
    {
        // „185 s" nic klientowi nie mowi; „3 min 5 s" mowi.
        var cut = Render(tik: TimeSpan.FromMilliseconds(1));

        cut.WaitForAssertion(
            () => Assert.Contains("min", cut.Find("[data-test='czas']").TextContent),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Mowi_wprost_ze_mozna_zamknac_karte()
    {
        // Przy modelu „bez kont, link z tokenem" to jest prawda — i jedyna rzecz,
        // ktora ratuje klienta niecierpliwego po czterech minutach.
        var cut = Render();

        var tekst = cut.Find("[data-test='mozesz-wyjsc']").TextContent;
        Assert.Contains("zamknąć", tekst);
        Assert.Contains("link", tekst);
    }

    [Fact]
    public void Pierwszy_etap_jest_wyrozniony_a_dalsze_czekaja()
    {
        var cut = Render();

        Assert.Contains("trwa", cut.Find("[data-test='etap-0']").GetAttribute("class"));
        Assert.Contains("przed", cut.Find("[data-test='etap-2']").GetAttribute("class"));
    }

    [Fact]
    public void Etapy_przesuwaja_sie_z_czasem()
    {
        var cut = Render(tik: TimeSpan.FromMilliseconds(1), krok: TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(
            () => Assert.Contains("zrobione", cut.Find("[data-test='etap-0']").GetAttribute("class")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Ostatni_etap_nie_znika_gdy_czas_leci_dalej()
    {
        // Zegar nie wie, kiedy model skonczy. Gdyby przesuwal sie poza liste, ekran
        // pokazalby „gotowe", zanim cokolwiek jest gotowe — czyli sklamalby.
        var cut = Render(tik: TimeSpan.FromMilliseconds(1), krok: TimeSpan.FromMilliseconds(20));

        cut.WaitForAssertion(
            () => Assert.Contains("trwa", cut.Find("[data-test='etap-2']").GetAttribute("class")),
            TimeSpan.FromSeconds(10));

        Assert.Equal(Etapy.Length, cut.FindAll("[data-test^='etap-']").Count);
    }

    [Fact]
    public void Nie_udajemy_ze_znamy_procent_postepu()
    {
        // Zmyslony procent stanalby na 90% i klient by to wychwycil.
        var cut = Render();

        Assert.DoesNotContain("%", cut.Markup);
        Assert.Empty(cut.FindAll("progress"));
    }
}
