using System.IO.Compression;
using System.Text;
using Generator.Web.Preview;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// ZIP, ktory klient dostaje na wlasnosc (decyzja 2). Budowanie przenioslem z
/// `ApiEndpoints` do `Paczka`, bo te same bajty musi oddawac trasa `/pobierz` dla
/// przycisku w edytorze — a refaktor bez testu na to, co w oryginale bylo najmniej
/// oczywiste, jest tylko przepisaniem kodu i nadzieja.
/// </summary>
public class PaczkaTests : IDisposable
{
    private readonly string _katalog =
        Path.Combine(Path.GetTempPath(), "gen-zip-" + Guid.NewGuid().ToString("N"));

    private void Plik(string wzgledna, string tresc)
    {
        var pelna = Path.Combine(_katalog, wzgledna.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(pelna)!);
        File.WriteAllText(pelna, tresc);
    }

    private ZipArchive Otworz() => new(Paczka.Zbuduj(_katalog));

    [Fact]
    public void Nazwy_wpisow_ida_z_ukosnikiem_w_przod_a_nie_z_systemowym()
    {
        // TO jest ta niewidoczna golym okiem rzecz z kodu Sebastiana: `GetRelativePath`
        // oddaje separator SYSTEMU, a obie nasze maszyny to Windows. Bez podmiany na „/"
        // archiwum ma wpis „css\styl.css" — po rozpakowaniu JEDEN PLIK o dziwnej nazwie
        // zamiast katalogu `css`, czyli strona klienta bez stylu. Zip to nie zglosi:
        // archiwum jest poprawne, tylko strona w srodku zepsuta.
        Plik("index.html", "<html></html>");
        Plik("css/styl.css", "body{}");

        var nazwy = Otworz().Entries.Select(e => e.FullName).ToList();

        Assert.Contains("css/styl.css", nazwy);
        Assert.DoesNotContain(nazwy, n => n.Contains('\\'));
    }

    [Fact]
    public void Paczka_ma_wszystkie_pliki_takze_z_podkatalogow()
    {
        Plik("index.html", "a");
        Plik("css/styl.css", "b");
        Plik("img/logo.svg", "c");

        Assert.Equal(3, Otworz().Entries.Count);
    }

    [Fact]
    public void Tresc_pliku_przezywa_spakowanie()
    {
        // Bez tego test sprawdzalby tylko nazwy — a klient rozpakowuje TRESC.
        Plik("index.html", "<h1>Fryzjer Anna</h1>");

        using var wpis = Otworz().GetEntry("index.html")!.Open();
        using var czytnik = new StreamReader(wpis, Encoding.UTF8);

        Assert.Equal("<h1>Fryzjer Anna</h1>", czytnik.ReadToEnd());
    }

    [Fact]
    public void Strumien_wraca_przewiniety_na_poczatek()
    {
        // `Results.File` czyta od biezacej pozycji. Strumien oddany na koncu dalby
        // klientowi plik ZEROWEJ dlugosci — i to bez zadnego bledu po drodze.
        Plik("index.html", "a");

        Assert.Equal(0, Paczka.Zbuduj(_katalog).Position);
    }

    [Fact]
    public void Nazwa_pliku_mowi_ktora_to_wersja()
    {
        // Klient pobiera kolejne wersje do tego samego katalogu „Pobrane".
        Assert.Equal("strona-v3.zip", Paczka.Nazwa(3));
    }

    public void Dispose()
    {
        if (Directory.Exists(_katalog)) Directory.Delete(_katalog, recursive: true);
        GC.SuppressFinalize(this);
    }
}
