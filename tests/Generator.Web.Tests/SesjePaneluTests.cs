using Generator.Web.Panel;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Czas jest tu PODSTAWIONY, a nie odczekany. Test wygasniecia oparty na `Thread.Sleep`
/// byloby trzeba pisac na 12 h albo skracac zycie sesji tylko po to, zeby dalo sie je
/// zmierzyc — a testy zalezne od zegara i maszyny juz raz kosztowaly to repo dzien
/// szukania (patrz pamiec: testy niezalezne od maszyny).
/// </summary>
public class SesjePaneluTests
{
    private sealed class ZegarSterowany(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _teraz = start;
        public override DateTimeOffset GetUtcNow() => _teraz;
        public void Przesun(TimeSpan o) => _teraz += o;
    }

    private static (SesjePanelu, ZegarSterowany) Nowe()
    {
        var zegar = new ZegarSterowany(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        return (new SesjePanelu(zegar), zegar);
    }

    [Fact]
    public void Swiezo_utworzona_sesja_jest_wazna()
    {
        var (sesje, _) = Nowe();

        Assert.True(sesje.Wazna(sesje.Utworz()));
    }

    [Fact]
    public void Sesja_wygasa_po_okresie_bezczynnosci()
    {
        var (sesje, zegar) = Nowe();
        var id = sesje.Utworz();

        zegar.Przesun(SesjePanelu.Zycie + TimeSpan.FromMinutes(1));

        Assert.False(sesje.Wazna(id));
    }

    [Fact]
    public void Uzycie_sesji_przedluza_jej_zycie()
    {
        // Waznosc liczona od OSTATNIEGO uzycia, nie od utworzenia: panel nie ma prawa
        // wylogowac czlowieka w srodku pracy tylko dlatego, ze siedzi w nim od rana.
        var (sesje, zegar) = Nowe();
        var id = sesje.Utworz();

        // Dwa kroki, kazdy krotszy niz zycie sesji, ale razem dluzsze niz ono.
        zegar.Przesun(SesjePanelu.Zycie - TimeSpan.FromMinutes(1));
        Assert.True(sesje.Wazna(id));
        zegar.Przesun(SesjePanelu.Zycie - TimeSpan.FromMinutes(1));

        Assert.True(sesje.Wazna(id));
    }

    [Fact]
    public void Zakonczona_sesja_przestaje_byc_wazna()
    {
        var (sesje, _) = Nowe();
        var id = sesje.Utworz();

        sesje.Zakoncz(id);

        Assert.False(sesje.Wazna(id));
    }

    [Fact]
    public void Zakonczenie_jednej_sesji_nie_rusza_drugiej()
    {
        // Dwie przegladarki (albo dwoje nas) to dwie sesje. Wylogowanie w jednej
        // nie moze wyrzucac z panelu drugiej.
        var (sesje, _) = Nowe();
        var pierwsza = sesje.Utworz();
        var druga = sesje.Utworz();

        sesje.Zakoncz(pierwsza);

        Assert.True(sesje.Wazna(druga));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zmyslony-identyfikator")]
    public void Nieznany_identyfikator_nigdy_nie_jest_wazny(string? id)
    {
        var (sesje, _) = Nowe();
        sesje.Utworz();   // istnieje INNA, wazna sesja — to nie ma pomagac

        Assert.False(sesje.Wazna(id));
    }

    [Fact]
    public void Dwie_sesje_maja_rozne_identyfikatory()
    {
        // Gdyby identyfikator byl przewidywalny albo staly, ciastko jednej osoby
        // otwieraloby panel kazdemu. To jest sekret, nie numer porzadkowy.
        var (sesje, _) = Nowe();

        Assert.NotEqual(sesje.Utworz(), sesje.Utworz());
    }

    [Fact]
    public void Identyfikator_jest_dosc_dlugi_zeby_nie_dalo_sie_go_zgadnac()
    {
        // 256 bitow w Base64Url to 43 znaki. Asercja pilnuje rzedu wielkosci, nie
        // dokladnej postaci — skrocenie do „wygodniejszej" dlugosci ma tu padac.
        var (sesje, _) = Nowe();

        Assert.True(sesje.Utworz().Length >= 40);
    }
}
