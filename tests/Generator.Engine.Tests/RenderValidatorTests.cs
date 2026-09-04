using Generator.Engine.Gates;
using Xunit;

namespace Generator.Engine.Tests;

public class RenderValidatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gen-render-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    [Fact]
    public void Kompletna_strona_przechodzi()
    {
        Write("index.html", """
        <html><head><link rel="stylesheet" href="style.css"></head>
        <body><h1 data-cmt-id="hero">Fryzjer</h1><img src="foto.png"></body></html>
        """);
        Write("style.css", "h1{color:#222}");
        Write("foto.png", "x");

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Katalog_z_koncowym_separatorem_nie_psuje_sprawdzania_linkow()
    {
        // Path.GetFullPath zachowuje koncowy separator, jesli byl obecny w wejsciu.
        // Bez TrimEndingDirectorySeparator w Check, "siteRoot + separator" mialby
        // podwojny separator i KAZDY prawidlowy lokalny link zostalby odrzucony jako
        // "poza katalogiem wersji" — wywolujacy (np. Task 9) moze przekazac sciezke
        // z koncowym "/", wiec to nie jest teoretyczny przypadek.
        Write("index.html", """
        <html><head><link rel="stylesheet" href="style.css"></head><body>x</body></html>
        """);
        Write("style.css", "h1{color:#222}");

        var check = RenderValidator.Check(_dir + Path.DirectorySeparatorChar);

        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Link_na_katalog_uzywa_jego_index_html()
    {
        // href="/" , href="galeria/" adresuja KATALOG — serwer statyczny serwuje dla
        // nich "<katalog>/index.html". Bez tego kroku File.Exists na samym katalogu
        // zwraca false i bramka odrzucalaby strone, ktora u klienta renderuje sie
        // normalnie.
        Directory.CreateDirectory(Path.Combine(_dir, "galeria"));
        File.WriteAllText(Path.Combine(_dir, "galeria", "index.html"), "<html><body>g</body></html>");
        Write("index.html", """
        <html><body><a href="/">start</a><a href="galeria/">galeria</a></body></html>
        """);

        var check = RenderValidator.Check(_dir);

        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Link_na_katalog_bez_index_html_to_problem()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "brak"));
        Write("index.html", """<html><body><a href="brak/">x</a></body></html>""");

        var check = RenderValidator.Check(_dir);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("brak/"));
    }

    [Fact]
    public void Brak_index_html_to_problem()
    {
        var check = RenderValidator.Check(_dir);
        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("index.html"));
    }

    [Fact]
    public void Pusty_index_html_to_problem()
    {
        Write("index.html", "   ");

        var check = RenderValidator.Check(_dir);

        // Samo Assert.False(check.Ok) tu nie wystarcza: "   " nie zawiera "<html", wiec
        // nawet po usunieciu sprawdzenia IsNullOrWhiteSpace ten test dalej by przechodzil
        // (padlby na innym warunku). Sprawdzamy tresc komunikatu, zeby test rzeczywiscie
        // pilnowal galezi "pusty plik", a nie przypadkiem innej.
        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("pusty"));
    }

    [Fact]
    public void Brak_domkniecia_html_to_problem()
    {
        Write("index.html", "<html><body>bez domkniecia");
        var check = RenderValidator.Check(_dir);
        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("</html>"));
    }

    [Fact]
    public void Wskazanie_na_nieistniejacy_lokalny_plik_to_problem()
    {
        Write("index.html", """
        <html><head><link rel="stylesheet" href="style.css"></head><body>x</body></html>
        """);
        // style.css celowo nie istnieje
        var check = RenderValidator.Check(_dir);
        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("style.css"));
    }

    [Fact]
    public void Linki_zewnetrzne_i_kotwice_sa_pomijane()
    {
        Write("index.html", """
        <html><body><a href="https://example.com">x</a><a href="#kontakt">y</a>
        <a href="mailto:a@b.pl">z</a></body></html>
        """);
        Assert.True(RenderValidator.Check(_dir).Ok);
    }

    [Fact]
    public void Tel_data_i_protokol_wzgledny_tez_sa_pomijane()
    {
        // Brief wprost wymienia tel: i data: obok mailto: i kotwic — brakowalo dla nich
        // osobnego testu (dany w briefie test pokrywa tylko https:// i mailto:).
        Write("index.html", """
        <html><body>
        <a href="tel:+48123456789">t</a>
        <img src="data:image/png;base64,iVBORw0KGgo=">
        <script src="//cdn.example.com/lib.js"></script>
        </body></html>
        """);
        Assert.True(RenderValidator.Check(_dir).Ok);
    }

    [Fact]
    public void Dowolny_schemat_uri_jest_pomijany_nie_tylko_wymienione_z_nazwy()
    {
        // Fix round 1: model potrafi wygenerowac dowolny schemat URI (typowy wzorzec dla
        // rozwijanego menu: href="javascript:void(0)"), nie tylko mailto/tel/data z listy.
        // Rozpoznajemy KAZDY schemat wedlug skladni z RFC 3986 (ALPHA *(ALPHA/DIGIT/+/-/.) ":"),
        // wiec test celowo bierze schematy spoza jakiejkolwiek listy: javascript:, sms:,
        // about:, blob:.
        Write("index.html", """
        <html><body>
        <a href="javascript:void(0)" onclick="toggle()">menu</a>
        <a href="sms:+48123456789">sms</a>
        <a href="about:blank">o</a>
        <a href="blob:https://example.com/1234-5678">b</a>
        </body></html>
        """);

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Atrybut_data_src_nie_jest_traktowany_jako_src()
    {
        // Fix round 1, Minor 2: "(?:href|src)" bez granicy lapal tez sufiks "data-src"/
        // "data-href" (standard przy leniwym ladowaniu obrazkow) — bramka sprawdzalaby
        // plik, ktorego model swiadomie jeszcze nie dolaczyl, i odrzucala dobra strone.
        // "thumb.jpg" celowo NIE istnieje w _dir: gdyby regex zlapal "data-src", ten test
        // by padl (Ok=false, problem z "thumb.jpg").
        Write("index.html", """
        <html><body><img data-src="thumb.jpg" data-href="cos.html" alt="leniwe ladowanie"></body></html>
        """);

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Prawdziwy_src_obok_data_src_jest_nadal_sprawdzany()
    {
        // Odwrotna strona Minor 2: obcinanie sufiksu nie moze wylaczyc sprawdzania
        // prawdziwego "src" stojacego tuz obok "data-src" w tym samym tagu.
        Write("index.html", """
        <html><body><img data-src="thumb.jpg" src="brak.png" alt="x"></body></html>
        """);

        var check = RenderValidator.Check(_dir);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("brak.png"));
    }

    [Fact]
    public void Link_na_katalog_z_kropka_tez_uzywa_jego_index_html()
    {
        // Fix round 1, Minor 3: komentarz w kodzie wymienial href="./" jako obslugiwany
        // obok "/" i "galeria/", ale brakowalo dla niego osobnego testu.
        Write("index.html", """<html><body><a href="./">start</a></body></html>""");

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Sciezka_zakorzeniona_po_windowsowsku_nie_jest_skladana_z_katalogiem_wersji()
    {
        // Fix round 1, Minor 1: na Windows "\x" jest zakorzenione (backslash to separator
        // katalogow), wiec Path.Combine(siteRoot, "\x") odrzucilby siteRoot dokladnie tak
        // samo, jak wczesniej robil to wiodacy "/" na Unix.
        //
        // Sama asercja Assert.False(check.Ok) TU nie wystarcza: na Unix "\x" i tak jest
        // (poprawnie) zglaszane jako problem przez zwykla galaz "plik nie istnieje" —
        // Path.Combine traktuje tam backslash jako zwykly znak nazwy pliku, wiec Ok=false
        // wyszedlby nawet bez tego fixa (empirycznie sprawdzone przy mutacji). Zeby test
        // naprawde pilnowal DODANEJ galezi (a nie przypadkiem tej samej co zawsze), sprawdzam
        // tresc konkretnego komunikatu "zakorzeniona", ktory pojawia sie WYLACZNIE z nowego
        // sprawdzenia `Path.IsPathRooted(relative) || relative.StartsWith('\\')`.
        //
        // To ostatnie (StartsWith('\\')) jest jawnie niezalezne od platformy — dodane celowo,
        // bo samo Path.IsPathRooted("\x") zwraca na Unix false (backslash nie jest tu
        // separatorem), wiec bez tego dopisku ten test bylby scenografia na kazdej maszynie
        // innej niz Windows. Prawdziwa ochrona samego Path.IsPathRooted uruchamia sie dopiero
        // na Windows wspoltworcy, gdzie zwraca true rowniez dla "\x".
        Write("index.html", """<html><body><a href="\x">x</a></body></html>""");

        var check = RenderValidator.Check(_dir);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("zakorzeniona"));
    }

    [Fact]
    public void Index_html_w_podkatalogu_nie_wystarcza()
    {
        // Serwer statyczny serwuje "/" z korzenia katalogu wersji — index.html schowany
        // w podkatalogu nigdy tam nie trafi, wiec strona efektywnie sie nie renderuje,
        // mimo ze plik o tej nazwie gdzies w drzewie istnieje. Podkatalogowy plik jest
        // celowo w pelni poprawny (ma <html>, </html>, brak zepsutych linkow), zeby test
        // odroznial "szukam tylko w korzeniu" od "szukam rekurencyjnie".
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "index.html"),
            "<html><body>ok</body></html>");

        var check = RenderValidator.Check(_dir);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("index.html"));
    }

    [Fact]
    public void Link_z_parametrem_zapytania_i_fragmentem_wskazuje_istniejacy_plik()
    {
        Write("index.html", """
        <html><head><link rel="stylesheet" href="style.css?v=2"></head>
        <body><img src="foto.png#x"></body></html>
        """);
        Write("style.css", "h1{color:#222}");
        Write("foto.png", "x");

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Link_z_parametrem_do_brakujacego_pliku_zglasza_czysta_nazwe()
    {
        Write("index.html", """
        <html><body><link rel="stylesheet" href="brak.css?v=3"></body></html>
        """);

        var check = RenderValidator.Check(_dir);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("brak.css") && !p.Contains("?v=3"));
    }

    [Fact]
    public void Sciezka_z_wiodacym_slashem_jest_wzgledna_do_katalogu_wersji()
    {
        // "/style.css" adresuje korzen STRONY, nie korzen dysku hosta. Path.Combine
        // z wiodacym "/" na Unixie zwraca sciezke bezwzgledna i ignoruje pierwszy
        // argument — bez wlasnej obslugi ten test wykrylby plik jako brakujacy,
        // mimo ze fizycznie istnieje w katalogu wersji.
        Write("index.html", """
        <html><head><link rel="stylesheet" href="/style.css"></head><body>x</body></html>
        """);
        Write("style.css", "h1{color:#222}");

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Link_wychodzacy_poza_katalog_wersji_to_problem()
    {
        // Plik istnieje NA DYSKU, ale poza katalogiem wersji (_dir) — nie wejdzie do
        // snapshotu/ZIP-a tej wersji, wiec u klienta i tak sie nie wyrenderuje. Samo
        // File.Exists po zlaczeniu sciezek dalby tu falszywy "Ok", bo plik naprawde jest.
        var parent = Directory.GetParent(_dir)!.FullName;
        var escaped = Path.Combine(parent, "poza-katalogiem-" + Guid.NewGuid().ToString("N") + ".css");
        File.WriteAllText(escaped, "h1{color:#222}");

        try
        {
            Write("index.html", $"""
            <html><head><link rel="stylesheet" href="../{Path.GetFileName(escaped)}"></head><body>x</body></html>
            """);

            var check = RenderValidator.Check(_dir);

            Assert.False(check.Ok);
            Assert.Contains(check.Problems, p => p.Contains(Path.GetFileName(escaped)));
        }
        finally
        {
            File.Delete(escaped);
        }
    }

    [Fact]
    public void Komentarz_z_zepsutym_linkiem_jest_pomijany()
    {
        // Link w komentarzu HTML nie trafi do przegladarki klienta — nie powinien wiec
        // wywalac bramki twardej. Bez zdejmowania komentarzy ten test by padl.
        Write("index.html", """
        <html><body>
        <!-- <link rel="stylesheet" href="nie-istnieje.css"> -->
        <p>tresc</p>
        </body></html>
        """);

        var check = RenderValidator.Check(_dir);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
    }

    [Fact]
    public void Zakomentowany_html_nie_liczy_sie_jako_obecny()
    {
        // Odwrotna strona tego samego mechanizmu: <html>/</html> schowane w komentarzu
        // nie sa prawdziwymi znacznikami strony. Bez zdejmowania komentarzy substring
        // "<html" zostalby znaleziony i test bledny falszywie przeszedlby jako Ok.
        Write("index.html", "<!-- <html> --><body>tresc</body><!-- </html> -->");

        var check = RenderValidator.Check(_dir);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("<html"));
        Assert.Contains(check.Problems, p => p.Contains("</html>"));
    }
}
