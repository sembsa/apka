using Generator.Engine.Versioning;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Generator.Web.Preview;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Mock musi zapisywac snapshot na dysk, bo /preview serwuje PLIKI, nie metadane.
/// Bez tego cala sciezka klienta konczy sie pustym iframe'em, a Task 8 planu
/// ("kliknij w naglowek") jest niewykonalny.
/// </summary>
public class MockSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gen-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Udana_runda_zapisuje_index_html_w_katalogu_wersji()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        var p = await api.CreateAsync("idea", "Fryzjer");

        await api.ApplyCommentsAsync(p.Id, 1, []);

        var wersja = (await api.GetAsync(p.Id)).Versions[^1].Number;
        var plik = Path.Combine(Snapshot(p.Id, wersja), "index.html");
        Assert.True(File.Exists(plik), $"brak snapshotu: {plik}");
    }

    [Fact]
    public async Task Snapshot_ma_bloki_z_data_cmt_id_w_formacie_z_kontraktu()
    {
        // Bez data-cmt-id nie ma w co kliknac, a format musi przejsc AnchorFormat z 3.1.
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ApplyCommentsAsync(p.Id, 1, []);

        var wersja = (await api.GetAsync(p.Id)).Versions[^1].Number;
        var html = await File.ReadAllTextAsync(Path.Combine(Snapshot(p.Id, wersja), "index.html"));

        Assert.Contains("data-cmt-id=\"hero\"", html);
        Assert.Contains("data-cmt-id=\"kontakt\"", html);
        Assert.DoesNotContain("data-cmt-id=\"oferta-1\"", html);   // id pozycyjne to blad (3.1)
    }

    [Fact]
    public async Task Snapshot_na_dysku_nie_zawiera_naszego_skryptu()
    {
        // To jest cala istota Taska 4a: skrypt dokleja PreviewInjector przy serwowaniu.
        // Snapshot i ZIP klienta zostaja czyste.
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ApplyCommentsAsync(p.Id, 1, []);

        var wersja = (await api.GetAsync(p.Id)).Versions[^1].Number;
        var sciezka = Path.Combine(Snapshot(p.Id, wersja), "index.html");
        var html = await File.ReadAllTextAsync(sciezka);

        Assert.DoesNotContain("preview-click.js", html);
        Assert.Contains("preview-click.js", PreviewInjector.Inject(html, "/js/preview-click.js"));
    }

    [Fact]
    public async Task Wybor_propozycji_tworzy_wersje_1_z_podgladem()
    {
        // Znalezione przeklikaniem, nie testem: edytor otwieral sie bez iframe'a, bo
        // wybor propozycji ustawial tylko status. Klient nie mial w co kliknac, a wiec
        // nie mial jak zglosic uwagi — czyli produkt nie istnial.
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        var p = await api.CreateAsync("idea", "Fryzjer");

        await api.ChooseProposalAsync(p.Id, "p-1");

        var po = await api.GetAsync(p.Id);
        var wersja = Assert.Single(po.Versions);
        Assert.Equal(1, wersja.Number);
        // Ukosnik na koncu jest istotny, nie kosmetyczny — patrz test nizej.
        Assert.Equal($"/preview/{p.Id}/1/", wersja.PreviewUrl);
        Assert.True(File.Exists(Path.Combine(Snapshot(p.Id, 1), "index.html")));
    }

    /// <summary>
    /// Znalezione przy pokazie w przegladarce. `previewUrl` bez koncowego ukosnika
    /// („/preview/{id}/1") powoduje, ze przegladarka rozwiazuje relatywne `styl.css`
    /// O KATALOG WYZEJ — czyli 404. Klient oceniał wtedy swoja strone w podgladzie
    /// BEZ jej wlasnego arkusza: inny font, inny uklad. Testy tego nie widzialy, bo
    /// zadny nie sprawdzal, co przegladarka zrobi z adresem wzglednym.
    /// </summary>
    [Fact]
    public async Task Adres_podgladu_konczy_sie_ukosnikiem_bo_strona_ma_adresy_wzgledne()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero, SnapshotRoot = _root };
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "p-1");
        await api.ApplyCommentsAsync(p.Id, 1, []);

        var projekt = await api.GetAsync(p.Id);
        Assert.All(projekt.Versions, w => Assert.EndsWith("/", w.PreviewUrl));

        // I dowod, ze to ma znaczenie: snapshot faktycznie odwoluje sie relatywnie.
        var html = await File.ReadAllTextAsync(
            Path.Combine(Snapshot(p.Id, 1), "index.html"));
        Assert.Contains("""href="styl.css""", html);
    }

    /// Sciezke snapshotu zna wylacznie VersionStore — takze w tescie atrapy.
    /// Wpisana tu na sztywno „v{n}" byla drugim zrodlem prawdy: zgadzala sie
    /// z atrapa i rozjezdzala z silnikiem, ktory pisze do „versions/001".
    private string Snapshot(string id, int numer) =>
        new VersionStore(Path.Combine(_root, id)).SnapshotPath(numer);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
