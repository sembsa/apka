using Generator.Web.Preview;
using Xunit;

namespace Generator.Web.Tests;

public class PreviewInjectorTests
{
    private const string Url = "/js/preview-click.js";

    [Fact]
    public void Wstrzykuje_przed_zamknieciem_body()
    {
        var html = """<html><body><h1 data-cmt-id="hero">Fryzjer</h1></body></html>""";
        var wynik = PreviewInjector.Inject(html, Url);

        Assert.Contains($"""<script src="{Url}"></script></body>""", wynik);
        Assert.Contains("""data-cmt-id="hero" """.Trim(), wynik);   // tresc klienta nietknieta
    }

    [Fact]
    public void Bez_body_doklejamy_na_koniec()
    {
        // Twarda bramka z 5.2 wymaga tylko <html i </html>, wiec HTML bez </body>
        // moze przejsc walidacje. Podglad musi wtedy dzialac tak samo.
        var wynik = PreviewInjector.Inject("<html><p>bez body</p></html>", Url);
        Assert.EndsWith($"""<script src="{Url}"></script>""", wynik);
    }

    [Fact]
    public void Wstrzykuje_dokladnie_raz_przy_wielu_body()
    {
        var html = "<html><body>a</body><body>b</body></html>";
        Assert.Equal(1, Wystapienia(PreviewInjector.Inject(html, Url), "preview-click.js"));
    }

    [Fact]
    public void Nie_modyfikuje_pliku_ktory_nie_jest_html()
    {
        const string css = "body { color: #222 }";
        Assert.Equal(css, PreviewInjector.Inject(css, Url));
    }

    [Fact]
    public void Podswietla_wskazane_bloki_outlinem_ktory_nie_rusza_ukladu()
    {
        var wynik = PreviewInjector.Inject(
            "<html><body><h1 data-cmt-id=\"hero\">A</h1></body></html>", Url, ["hero", "kontakt"]);

        Assert.Contains("""[data-cmt-id="hero"]""", wynik);
        Assert.Contains("""[data-cmt-id="kontakt"]""", wynik);
        Assert.Contains("outline", wynik);
        Assert.DoesNotContain("border", wynik);   // border przestawialby uklad strony
    }

    [Fact]
    public void Bez_listy_zmian_nie_dokleja_zadnego_stylu()
    {
        var wynik = PreviewInjector.Inject("<html><body>a</body></html>", Url);
        Assert.DoesNotContain("<style", wynik);
    }

    /// <summary>
    /// Kotwice pochodza z HTML-a wygenerowanego przez model z danych klienta. Gdyby
    /// szly do selektora bez sprawdzenia, tresc strony pisalaby wlasny arkusz —
    /// a stad juz blisko do przykrycia calego podgladu.
    /// </summary>
    [Theory]
    [InlineData("""hero"]{display:none}body{background:red}[x=""")]
    [InlineData("</style><script>alert(1)</script>")]
    [InlineData("HERO")]                  // wielkie litery lamia format 3.1
    [InlineData("1-oferta")]              // musi zaczynac sie litera
    [InlineData("")]
    public void Kotwica_niezgodna_z_formatem_nie_trafia_do_css(string paskudztwo)
    {
        var wynik = PreviewInjector.Inject("<html><body>a</body></html>", Url, [paskudztwo]);

        Assert.DoesNotContain("<style", wynik);
        Assert.DoesNotContain("display:none", wynik);
        Assert.DoesNotContain("<script>alert", wynik);
    }

    [Fact]
    public void Poprawne_kotwice_przechodza_mimo_jednej_paskudnej()
    {
        // Jedna zla kotwica nie moze wygasic podswietlenia dla pozostalych.
        var wynik = PreviewInjector.Inject(
            "<html><body>a</body></html>", Url, ["hero", "</style><script>x</script>"]);

        Assert.Contains("""[data-cmt-id="hero"]""", wynik);
        Assert.DoesNotContain("<script>x", wynik);
    }

    private static int Wystapienia(string s, string igla)
    {
        var n = 0;
        for (var i = s.IndexOf(igla, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(igla, i + igla.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
