using System.Net;
using Generator.Engine.Sources;
using Xunit;

namespace Generator.Engine.Tests;

/// <summary>
/// Adres podaje klient, pobiera nasz serwer — to SSRF. Tablica przypadkow jest
/// testowana bez sieci, bo polityka jest czysta funkcja: test ma sprawdzac regule,
/// a nie to, czy akurat cos odpowiada pod danym adresem.
/// </summary>
public class PolitykaAdresuTests
{
    [Theory]
    [InlineData("127.0.0.1")]          // petla zwrotna — nasz wlasny panel
    [InlineData("127.1.2.3")]          // cala 127/8, nie tylko .0.1
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]     // gorny koniec 172.16/12
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]    // metadane chmury — najgrozniejszy pojedynczy adres
    [InlineData("100.64.0.1")]         // CGNAT
    [InlineData("224.0.0.1")]          // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]            // link-local IPv6
    [InlineData("fc00::1")]            // unique local IPv6
    [InlineData("ff02::1")]            // multicast IPv6
    [InlineData("::ffff:10.0.0.1")]    // TO SAMO 10.0.0.1 zapisane jako IPv6
    [InlineData("::ffff:127.0.0.1")]   // i to samo dla petli zwrotnej
    public void Adresy_wewnetrzne_sa_odrzucane(string adres) =>
        Assert.False(PolitykaAdresu.CzyDozwolony(IPAddress.Parse(adres)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]         // TUZ pod 172.16/12 — granica z dobrej strony
    [InlineData("172.32.0.1")]         // TUZ nad 172.16/12
    [InlineData("100.63.255.255")]     // tuz pod CGNAT
    [InlineData("11.0.0.1")]           // tuz nad 10/8
    [InlineData("128.0.0.1")]          // tuz nad 127/8
    [InlineData("223.255.255.255")]    // tuz pod multicastem
    [InlineData("2606:4700::1111")]    // publiczny IPv6
    public void Adresy_publiczne_przechodza(string adres) =>
        Assert.True(PolitykaAdresu.CzyDozwolony(IPAddress.Parse(adres)));

    [Theory]
    [InlineData("http://przyklad.pl/")]
    [InlineData("https://przyklad.pl/")]
    public void Schematy_http_sa_dozwolone(string adres) =>
        Assert.True(PolitykaAdresu.CzyDozwolonySchemat(new Uri(adres)));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://przyklad.pl/plik")]
    [InlineData("gopher://przyklad.pl/")]
    public void Inne_schematy_sa_odrzucane(string adres) =>
        Assert.False(PolitykaAdresu.CzyDozwolonySchemat(new Uri(adres)));

    [Fact]
    public void Z_kilku_adresow_bierzemy_pierwszy_dozwolony()
    {
        // Realny uklad: host ma rekord A na adres wewnetrzny i drugi na publiczny.
        // Odrzucenie calego hosta byloby zbyt ostre, wziecie pierwszego z brzegu —
        // zbyt luzne.
        var wybrany = PolitykaAdresu.Wybierz(
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.8.8")], "przyklad.pl");

        Assert.Equal(IPAddress.Parse("8.8.8.8"), wybrany);
    }

    [Fact]
    public void Gdy_zaden_adres_nie_przechodzi_rzucamy_bez_zdradzania_ktory()
    {
        var ex = Assert.Throws<AdresNiedozwolonyException>(() => PolitykaAdresu.Wybierz(
            [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("169.254.169.254")], "wewnetrzny.local"));

        // Komunikat idzie do logu i (opakowany) do klienta. Konkretny adres wewnetrzny
        // jest informacja o naszej sieci dla kogos, kto wlasnie ja sonduje.
        Assert.Contains("wewnetrzny.local", ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
        Assert.DoesNotContain("169.254", ex.Message);
    }
}
