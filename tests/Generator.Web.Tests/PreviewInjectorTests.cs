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

    private static int Wystapienia(string s, string igla)
    {
        var n = 0;
        for (var i = s.IndexOf(igla, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(igla, i + igla.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
