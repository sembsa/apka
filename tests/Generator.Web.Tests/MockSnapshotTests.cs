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
        var plik = Path.Combine(_root, p.Id, $"v{wersja}", "index.html");
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
        var html = await File.ReadAllTextAsync(Path.Combine(_root, p.Id, $"v{wersja}", "index.html"));

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
        var sciezka = Path.Combine(_root, p.Id, $"v{wersja}", "index.html");
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
        Assert.Equal($"/preview/{p.Id}/1", wersja.PreviewUrl);
        Assert.True(File.Exists(Path.Combine(_root, p.Id, "v1", "index.html")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
