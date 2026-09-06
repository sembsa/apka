using Generator.Engine.Sources;
using Xunit;

namespace Generator.Engine.Tests;

/// <summary>
/// Wejsciem jest HTML CUDZY — pisany latami przez nieznane narzedzia. Testy sa wiec
/// pisane pod ksztalty, ktore realnie wystepuja na stronach malych polskich firm,
/// a nie pod czysty HTML, ktory sami byśmy napisali.
/// </summary>
public class EkstraktorTresciTests
{
    [Fact]
    public async Task Fakty_klienta_przezywaja_w_calosci()
    {
        // To jest CEL calego trybu: model ma odtworzyc fakty, nie wymyslic firme.
        var html = """
            <html><head><title>Fryzjer Ola — Nowy Sącz</title></head>
            <body>
              <h1>Salon Ola</h1>
              <p>Strzyżenie damsko-męskie od 2003 roku.</p>
              <h2>Kontakt</h2>
              <address>ul. Krakowska 5, 33-300 Nowy Sącz, tel. 600 100 200</address>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("Fryzjer Ola — Nowy Sącz", tekst);
        Assert.Contains("Salon Ola", tekst);
        Assert.Contains("ul. Krakowska 5", tekst);
        Assert.Contains("600 100 200", tekst);
    }

    [Fact]
    public async Task Struktura_zostaje_widoczna_a_nie_zamienia_sie_w_zupe_slow()
    {
        // „Oferta" i pozycje cennika musza dac sie odroznic. Bez tego model dostaje
        // ciag slow, w ktorym naglowek waży tyle samo co przypadkowe zdanie.
        var html = """
            <html><body>
              <h2>Oferta</h2>
              <ul><li>Strzyżenie damskie 60 zł</li><li>Koloryzacja 150 zł</li></ul>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("H2: Oferta", tekst);
        Assert.Contains("- Strzyżenie damskie 60 zł", tekst);

        // Kolejnosc niesie znaczenie: naglowek PRZED swoja lista.
        Assert.True(tekst.IndexOf("H2: Oferta", StringComparison.Ordinal)
                  < tekst.IndexOf("- Koloryzacja", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skrypty_i_style_nie_wchodza_do_promptu()
    {
        // Za kazdy token placimy, a JavaScript analityki nie mowi nic o firmie.
        var html = """
            <html><head><style>.naglowek{color:#c0ffee}</style></head>
            <body>
              <script>window.dataLayer=[{sklep:"tajne"}];</script>
              <noscript>Wlacz JavaScript</noscript>
              <p>Piekarnia Zaczyn</p>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("Piekarnia Zaczyn", tekst);
        Assert.DoesNotContain("dataLayer", tekst);
        Assert.DoesNotContain("c0ffee", tekst);
        Assert.DoesNotContain("Wlacz JavaScript", tekst);
    }

    [Fact]
    public async Task Stopka_i_nawigacja_ZOSTAJA_bo_tam_siedzi_adres()
    {
        // Odruch „wywal stopke i menu" wyrzucilby na tych stronach material
        // najcenniejszy. Celowo tego NIE robimy.
        var html = """
            <html><body>
              <nav><a href="/oferta">Oferta</a><a href="/kontakt">Kontakt</a></nav>
              <footer>Zakład Ślusarski Kowalski, ul. Łąkowa 7, Świętochłowice</footer>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("ul. Łąkowa 7", tekst);
        Assert.Contains("Oferta", tekst);
    }

    [Fact]
    public async Task Tekst_lezacy_luzem_miedzy_znacznikami_nie_przepada()
    {
        // Ksztalt ze starego HTML-u: tresc bez akapitu, lamana <br>. Ekstraktor
        // patrzacy wylacznie na elementy zgubilby tu wszystko.
        var html = "<html><body><div>Tel. 600 100 200<br>Czynne pon-pt 9-17</div></body></html>";

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("600 100 200", tekst);
        Assert.Contains("pon-pt 9-17", tekst);
    }

    [Fact]
    public async Task Twarda_spacja_i_lamanie_linii_nie_rozbijaja_numeru_telefonu()
    {
        // `&nbsp;` miedzy grupami cyfr to standard w polskich szablonach.
        var html = "<html><body><p>tel.&nbsp;600&nbsp;100&nbsp;200</p></body></html>";

        Assert.Contains("tel. 600 100 200", await EkstraktorTresci.Wyciagnij(html));
    }

    [Fact]
    public async Task Gdy_strona_ma_main_pomijamy_menu_ale_stopke_bierzemy()
    {
        // Zmierzone na zywej stronie (pl.wikipedia.org): bez tego caly limit 12 tys.
        // znakow schodzil na menu, zanim ekstraktor dotarl do tresci. Stopka zostaje
        // mimo to — tam siedzi adres i telefon.
        var html = """
            <html><body>
              <nav><a href="/losuj">Losuj artykuł</a><a href="/zasady">Zasady</a></nav>
              <main><h1>Cukiernia Malwa</h1><p>Torty na zamówienie od 1998 roku.</p></main>
              <footer>ul. Łąkowa 7, Świętochłowice, tel. 600 100 200</footer>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("Cukiernia Malwa", tekst);
        Assert.Contains("600 100 200", tekst);
        Assert.DoesNotContain("Losuj artykuł", tekst);
    }

    [Fact]
    public async Task Strona_bez_main_idzie_po_calosci_jak_dotad()
    {
        // Typowa strona malej firmy nie ma `main`. Tam nawigacja jest krotka i tez
        // cos znaczy („Cennik", „Dojazd"), wiec nic nie odcinamy.
        var html = """
            <html><body>
              <nav><a href="/cennik">Cennik</a></nav>
              <div><h1>Zakład Ślusarski</h1></div>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Contains("Cennik", tekst);
        Assert.Contains("Zakład Ślusarski", tekst);
    }

    [Fact]
    public async Task Stopka_w_srodku_main_nie_jest_liczona_dwa_razy()
    {
        var html = """
            <html><body>
              <main><h1>Piekarnia</h1><footer>tel. 600 100 200</footer></main>
            </body></html>
            """;

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Equal(1, tekst.Split("600 100 200").Length - 1);
    }

    [Fact]
    public async Task Bardzo_duza_strona_jest_przycinana_do_limitu()
    {
        // Duzy serwis firmowy potrafi miec 200 kB tekstu. Placimy za kazdy token.
        var akapit = "<p>" + new string('x', 500) + "</p>";
        var html = "<html><body>" + string.Concat(Enumerable.Repeat(akapit, 200)) + "</body></html>";

        var tekst = await EkstraktorTresci.Wyciagnij(html);

        Assert.Equal(EkstraktorTresci.MaxZnakow, tekst.Length);
    }

    [Fact]
    public async Task Zepsute_wejscie_nie_wywala_calej_sciezki()
    {
        // Klient wkleil adres, pod ktorym lezy cokolwiek. To nie moze rzucic —
        // pusty wynik jest sygnalem dla warstwy wyzej, wyjatek byl by awaria.
        Assert.Equal(string.Empty, await EkstraktorTresci.Wyciagnij(""));
        Assert.NotNull(await EkstraktorTresci.Wyciagnij("<html><body"));
    }
}
