using Generator.Web.Preview;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Kontrakt 5.3: porownujemy per `data-cmt-id`, po znormalizowanym outerHtml.
/// </summary>
public class ZmienioneBlokiTests
{
    private static string Strona(string cialo) =>
        $"<html lang=\"pl\"><head><title>x</title></head><body>{cialo}</body></html>";

    [Fact]
    public async Task Blok_o_zmienionej_tresci_jest_zmieniony()
    {
        var rodzic = Strona("""<h1 data-cmt-id="hero">Fryzjer Anna</h1>""");
        var dziecko = Strona("""<h1 data-cmt-id="hero">Salon Anny</h1>""");

        Assert.Equal(["hero"], await ZmienioneBloki.Policz(rodzic, dziecko));
    }

    [Fact]
    public async Task Zmiana_samego_stylu_przy_identycznym_tekscie_TEZ_jest_zmiana()
    {
        // Najczestszy przypadek w tym produkcie: „powieksz telefon". Tekst zostaje ten
        // sam, zmienia sie `style`. Porownanie po TextContent zglosiloby „nic sie nie
        // zmienilo" wlasnie tutaj — dlatego normalizujemy outerHtml, nie tekst.
        var rodzic = Strona("""<footer data-cmt-id="kontakt">tel. 600 100 200</footer>""");
        var dziecko = Strona(
            """<footer data-cmt-id="kontakt" style="font-size:1.5rem">tel. 600 100 200</footer>""");

        Assert.Equal(["kontakt"], await ZmienioneBloki.Policz(rodzic, dziecko));
    }

    [Fact]
    public async Task Samo_przeformatowanie_nie_jest_zmiana()
    {
        // Inaczej kazda wersja podswietlalaby sie cala i podswietlenie nie znaczyloby nic.
        var rodzic = Strona("""<h1 data-cmt-id="hero">Fryzjer Anna</h1>""");
        var dziecko = Strona("<h1    data-cmt-id=\"hero\" >\n   Fryzjer Anna\n</h1>");

        Assert.Empty(await ZmienioneBloki.Policz(rodzic, dziecko));
    }

    [Fact]
    public async Task Kolejnosc_atrybutow_nie_jest_zmiana()
    {
        var rodzic = Strona("""<p data-cmt-id="opis" class="a" id="b">tekst</p>""");
        var dziecko = Strona("""<p id="b" class="a" data-cmt-id="opis">tekst</p>""");

        Assert.Empty(await ZmienioneBloki.Policz(rodzic, dziecko));
    }

    [Fact]
    public async Task Nowy_blok_jest_zmiana_a_zniknięty_nie_trafia_na_liste()
    {
        // Zniknietym zajmuje sie orphanedAnchors (3.4) i ma inny komunikat —
        // „sprawdz, czy strona nadal zawiera to, co miala", nie „to sie zmienilo".
        var rodzic = Strona("""<h1 data-cmt-id="hero">A</h1><p data-cmt-id="stary">B</p>""");
        var dziecko = Strona("""<h1 data-cmt-id="hero">A</h1><p data-cmt-id="nowy">C</p>""");

        Assert.Equal(["nowy"], await ZmienioneBloki.Policz(rodzic, dziecko));
    }

    [Fact]
    public async Task Bez_rodzica_nic_nie_jest_podswietlone()
    {
        // Pierwsza wersja: nie ma z czym porownac, a podswietlenie wszystkiego
        // byloby dla klienta szumem.
        Assert.Empty(await ZmienioneBloki.Policz(null, Strona("""<h1 data-cmt-id="hero">A</h1>""")));
    }

    [Fact]
    public async Task Blok_bez_data_cmt_id_nie_wchodzi_do_porownania()
    {
        var rodzic = Strona("<div>losowy tekst</div>");
        var dziecko = Strona("<div>calkiem inny tekst</div>");

        Assert.Empty(await ZmienioneBloki.Policz(rodzic, dziecko));
    }
}
