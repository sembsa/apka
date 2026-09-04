using Generator.Engine.Gates;
using Xunit;

namespace Generator.Engine.Tests;

public class AnchorExtractorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gen-anchor-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Wyciaga_id_zgodne_z_formatem_kontraktu()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), """
        <section data-cmt-id="hero"></section>
        <section data-cmt-id="oferta-strzyzenie"></section>
        <section data-cmt-id="opinia-anna-k"></section>
        """);

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Equal(3, ids.Count);
        Assert.Contains("hero", ids);
        Assert.Contains("oferta-strzyzenie", ids);
    }

    [Fact]
    public void Id_niezgodne_z_formatem_sa_pomijane_czyli_licza_sie_jako_brakujace()
    {
        // Format z kontraktu 3.1: [a-z][a-z0-9-]{0,39}
        File.WriteAllText(Path.Combine(_dir, "index.html"), """
        <section data-cmt-id="Hero"></section>
        <section data-cmt-id="2oferta"></section>
        <section data-cmt-id="ma spacje"></section>
        <section data-cmt-id="ok-id"></section>
        """);

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Single(ids);
        Assert.Contains("ok-id", ids);
    }

    [Fact]
    public void Osierocone_to_te_z_poprzedniej_wersji_nieobecne_w_nowej()
    {
        var previous = new[] { "hero", "cennik", "kontakt" };
        var current = new HashSet<string> { "hero", "kontakt", "opinie" };

        var orphaned = AnchorExtractor.Orphaned(previous, current);

        Assert.Equal(["cennik"], orphaned);
    }

    [Fact]
    public void Pierwsza_wersja_nie_ma_osieroconych()
    {
        Assert.Empty(AnchorExtractor.Orphaned([], new HashSet<string> { "hero" }));
    }

    [Fact]
    public void Wielkosc_liter_nazwy_atrybutu_nie_ma_znaczenia_ale_wartosci_owszem()
    {
        // Przegladarka normalizuje nazwy atrybutow HTML przy parsowaniu — DOM widzi
        // "DATA-CMT-ID" i "data-cmt-id" jako ten sam atrybut. Gdyby ekstraktor tego nie
        // uwzglednial, poprawnie renderujaca sie strona wygenerowalaby dla klienta
        // falszywy wpis w orphanedAnchors (widoczny w UI wg kontraktu 3.4) tylko dlatego,
        // ze model uzyl innej wielkosci liter w nazwie atrybutu. Format WARTOSCI id
        // zostaje bez zmian scisly (kontrakt 3.1) — "Cennik" dalej sie nie liczy.
        File.WriteAllText(Path.Combine(_dir, "index.html"), """
        <section DATA-CMT-ID="hero"></section>
        <section Data-Cmt-Id="cennik"></section>
        <section DATA-CMT-ID="Cennik-Zle"></section>
        """);

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Equal(2, ids.Count);
        Assert.Contains("hero", ids);
        Assert.Contains("cennik", ids);
    }

    [Fact]
    public void Atrybut_rozdzielony_na_wiele_linii_jest_wykrywany()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<section\n  data-cmt-id\n  =\n  \"hero\"\n></section>");

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Single(ids);
        Assert.Contains("hero", ids);
    }

    [Fact]
    public void Powtorzony_id_liczy_sie_raz()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), """
        <section data-cmt-id="hero"></section>
        <div data-cmt-id="hero"></div>
        """);

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Single(ids);
        Assert.Contains("hero", ids);
    }

    [Fact]
    public void Skanuje_wszystkie_pliki_html_w_podkatalogach()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), """<section data-cmt-id="hero"></section>""");
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "about.html"), """<section data-cmt-id="opinie"></section>""");

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Equal(2, ids.Count);
        Assert.Contains("hero", ids);
        Assert.Contains("opinie", ids);
    }

    [Fact]
    public void Komentarz_z_data_cmt_id_jest_pomijany()
    {
        // Anchor schowany w komentarzu HTML nie trafia do przegladarki — nie powinien
        // wiec liczyc sie jako zywy anchor tej wersji (i cichy false-positive przy
        // liczeniu osieroconych w kolejnej rundzie).
        File.WriteAllText(Path.Combine(_dir, "index.html"), """
        <!-- <section data-cmt-id="widmo"></section> -->
        <section data-cmt-id="hero"></section>
        """);

        var ids = AnchorExtractor.Extract(_dir);

        Assert.Single(ids);
        Assert.Contains("hero", ids);
    }

    [Fact]
    public void Orphaned_usuwa_duplikaty_z_poprzedniej_listy()
    {
        var previous = new[] { "cennik", "cennik", "hero" };
        var current = new HashSet<string> { "hero" };

        var orphaned = AnchorExtractor.Orphaned(previous, current);

        Assert.Equal(["cennik"], orphaned);
    }
}
