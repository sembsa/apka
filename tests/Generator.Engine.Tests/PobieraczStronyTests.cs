using System.Net;
using System.Text;
using Generator.Engine.Sources;
using Xunit;

namespace Generator.Engine.Tests;

/// <summary>
/// Testy tresci ida przez podstawiony `HttpMessageHandler`, wiec bez sieci. Polityka
/// adresow ma osobny test NA ZYWYM handlerze — atrapa omija `ConnectCallback`,
/// wiec testowana atrapa nie powiedzialaby nic o tym, na co naprawde sie laczymy.
/// </summary>
public class PobieraczStronyTests
{
    private sealed class Atrapa(Func<HttpRequestMessage, HttpResponseMessage> odpowiedz)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Zapytania { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Zapytania.Add(request);
            return Task.FromResult(odpowiedz(request));
        }
    }

    private static HttpResponseMessage Html(string tresc, string typ = "text/html; charset=utf-8") =>
        Bajty(Encoding.UTF8.GetBytes(tresc), typ);

    private static HttpResponseMessage Bajty(byte[] dane, string typ)
    {
        var odp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dane) };
        odp.Content.Headers.Remove("Content-Type");
        odp.Content.Headers.TryAddWithoutValidation("Content-Type", typ);
        return odp;
    }

    [Fact]
    public async Task Pobiera_html_i_oddaje_go_w_calosci()
    {
        using var p = new PobieraczStrony(new Atrapa(_ => Html("<html><body>Fryzjer Ola</body></html>")));

        Assert.Contains("Fryzjer Ola", await p.PobierzAsync("https://przyklad.pl"));
    }

    [Fact]
    public async Task Adres_bez_schematu_dostaje_https_zamiast_odmowy()
    {
        // Nietechniczny klient wpisuje „mojafirma.pl" rownie czesto jak pelny adres.
        var atrapa = new Atrapa(_ => Html("<html>ok</html>"));
        using var p = new PobieraczStrony(atrapa);

        await p.PobierzAsync("mojafirma.pl");

        Assert.Equal("https://mojafirma.pl/", atrapa.Zapytania[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Przedstawiamy_sie_naglowkiem_bo_puste_user_agent_dostaje_403()
    {
        var atrapa = new Atrapa(_ => Html("<html>ok</html>"));
        using var p = new PobieraczStrony(atrapa);

        await p.PobierzAsync("https://przyklad.pl");

        Assert.Contains("GeneratorStron", atrapa.Zapytania[0].Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://przyklad.pl/plik")]
    public async Task Schemat_inny_niz_http_jest_odrzucany_przed_wyslaniem_czegokolwiek(string adres)
    {
        var atrapa = new Atrapa(_ => Html("nie powinno dojsc"));
        using var p = new PobieraczStrony(atrapa);

        await Assert.ThrowsAsync<AdresNiepoprawnyException>(() => p.PobierzAsync(adres));
        Assert.Empty(atrapa.Zapytania);
    }

    [Fact]
    public async Task Blad_http_konczy_sie_wyjatkiem_a_nie_pusta_trescia()
    {
        using var p = new PobieraczStrony(new Atrapa(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }));

        var ex = await Assert.ThrowsAsync<PobranieNieudaneException>(
            () => p.PobierzAsync("https://przyklad.pl"));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Plik_ktory_nie_jest_strona_jest_odrzucany()
    {
        // Klient wkleja link do PDF-a albo do obrazka rownie chetnie co do strony.
        using var p = new PobieraczStrony(new Atrapa(
            _ => Bajty([1, 2, 3], "application/pdf")));

        var ex = await Assert.ThrowsAsync<PobranieNieudaneException>(
            () => p.PobierzAsync("https://przyklad.pl/cennik.pdf"));
        Assert.Contains("application/pdf", ex.Message);
    }

    [Fact]
    public async Task Zadeklarowana_dlugosc_ponad_limit_odcina_zanim_cokolwiek_przeczytamy()
    {
        using var p = new PobieraczStrony(new Atrapa(_ =>
        {
            var odp = Html("<html>malutka</html>");
            odp.Content.Headers.ContentLength = PobieraczStrony.MaxBajtow + 1;
            return odp;
        }));

        await Assert.ThrowsAsync<PobranieNieudaneException>(() => p.PobierzAsync("https://przyklad.pl"));
    }

    [Fact]
    public async Task Strona_wieksza_niz_limit_jest_ucinana_wyjatkiem_a_nie_wczytywana()
    {
        // Bez naglowka Content-Length, czyli przypadek, w ktorym deklaracja nie ratuje:
        // liczy petla czytajaca. `ReadAsStringAsync` nie ma gornej granicy i strona-pulapka
        // zjadlaby pamiec procesu.
        var duze = new byte[PobieraczStrony.MaxBajtow + 4096];
        Array.Fill(duze, (byte)'x');

        using var p = new PobieraczStrony(new Atrapa(_ =>
        {
            var odp = Bajty(duze, "text/html");
            odp.Content.Headers.ContentLength = null;
            return odp;
        }));

        var ex = await Assert.ThrowsAsync<PobranieNieudaneException>(
            () => p.PobierzAsync("https://przyklad.pl"));
        Assert.Contains("limit", ex.Message);
    }

    // --- kodowanie: realny problem tego rynku, nie przypadek teoretyczny ---

    [Fact]
    public async Task Windows_1250_z_naglowka_HTTP_wraca_jako_poprawny_polski_tekst()
    {
        var dane = Encoding.GetEncoding("windows-1250").GetBytes("<html>Zażółć gęślą jaźń</html>");
        using var p = new PobieraczStrony(new Atrapa(
            _ => Bajty(dane, "text/html; charset=windows-1250")));

        Assert.Contains("Zażółć gęślą jaźń", await p.PobierzAsync("https://przyklad.pl"));
    }

    [Fact]
    public async Task Iso_8859_2_zadeklarowane_TYLKO_w_meta_tez_wraca_poprawnie()
    {
        // Stara strona malej firmy: serwer nie podaje charsetu wcale, jest tylko
        // <meta>. To wlasnie ten przypadek jest na tym rynku najczestszy.
        var kodowanie = Encoding.GetEncoding("iso-8859-2");
        var dane = kodowanie.GetBytes(
            """<html><head><meta http-equiv="Content-Type" content="text/html; charset=iso-8859-2"></head>""" +
            "<body>Świętochłowice, ul. Łąkowa</body></html>");

        using var p = new PobieraczStrony(new Atrapa(_ => Bajty(dane, "text/html")));

        Assert.Contains("Świętochłowice, ul. Łąkowa", await p.PobierzAsync("https://przyklad.pl"));
    }

    [Fact]
    public void Naglowek_HTTP_wygrywa_z_meta_bo_serwer_wie_o_sobie_wiecej()
    {
        // Dokument bywa kopiowany miedzy hostami razem ze starym <meta>; naglowek
        // pochodzi od tego serwera, ktory go WLASNIE wyslal.
        var dane = Encoding.UTF8.GetBytes(
            """<html><head><meta charset="windows-1250"></head><body>Zażółć</body></html>""");

        Assert.Contains("Zażółć", PobieraczStrony.Dekoduj(dane, "utf-8"));
    }

    [Fact]
    public void Wymyslona_nazwa_kodowania_nie_wywala_pobierania_tylko_schodzi_nizej()
    {
        var dane = Encoding.UTF8.GetBytes("<html>Zażółć</html>");

        Assert.Contains("Zażółć", PobieraczStrony.Dekoduj(dane, "utf-8-bis-mocniejsze"));
    }

    // --- polityka adresow na ZYWYM handlerze, bez atrapy i bez sieci ---

    [Theory]
    [InlineData("http://127.0.0.1:1/")]        // nasz wlasny proces
    [InlineData("http://169.254.169.254/")]    // metadane chmury
    [InlineData("http://10.0.0.1/")]
    public async Task Adres_wewnetrzny_jest_odrzucany_przy_zestawianiu_polaczenia(string adres)
    {
        // Bez podstawionego handlera: to jest test `ConnectCallback`, czyli miejsca,
        // w ktorym sprawdzamy adres TUZ PRZED polaczeniem. Zadne gniazdo nie powstaje,
        // wiec test nie potrzebuje sieci. Przekierowanie na adres wewnetrzny przechodzi
        // przez ten sam callback (kazdy skok laczy sie na nowo), wiec jest zamkniete
        // ta sama regula — tego akurat nie da sie tu pokazac bez zywego serwera.
        using var p = new PobieraczStrony(limit: TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<AdresNiedozwolonyException>(() => p.PobierzAsync(adres));
    }
}
