using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Trasa, ktora zamyka droge klienta: pobranie strony KLIKNIECIEM W LINK. Endpoint ZIP
/// pod `/api` istnial wczesniej, ale za naglowkiem `X-Project-Token`, ktorego zwykly
/// `<a href>` nie wysle — istnial wiec dla integracji, a nie dla klienta.
/// </summary>
public class PobierzEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-pobierz-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _klient;
    private readonly string _id = Guid.NewGuid().ToString();

    public PobierzEndpointTests(WebApplicationFactory<Program> factory)
    {
        // Snapshot pisany recznie w miejscu, ktore zna `VersionStore`: „<id>/versions/001".
        var wersja1 = Path.Combine(_root, _id, "versions", "001");
        Directory.CreateDirectory(Path.Combine(wersja1, "css"));
        File.WriteAllText(Path.Combine(wersja1, "index.html"), "<h1>Fryzjer Anna</h1>");
        File.WriteAllText(Path.Combine(wersja1, "css", "styl.css"), "body{}");

        _klient = factory.WithWebHostBuilder(b => b.UseSetting("Projects:Root", _root))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Klient_pobiera_swoja_strone_bez_zadnego_naglowka()
    {
        // Sedno tej trasy: zwykly GET, bez tokenu — dokladnie to, co robi przegladarka
        // po klignieciu w link w edytorze.
        var odpowiedz = await _klient.GetAsync($"/pobierz/{_id}/1");

        Assert.Equal(HttpStatusCode.OK, odpowiedz.StatusCode);
        Assert.Equal("application/zip", odpowiedz.Content.Headers.ContentType?.MediaType);
        Assert.Equal("strona-v1.zip", odpowiedz.Content.Headers.ContentDisposition?.FileNameStar
            ?? odpowiedz.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task W_paczce_jest_cala_strona_a_nie_sam_index()
    {
        var zip = new ZipArchive(await _klient.GetStreamAsync($"/pobierz/{_id}/1"));

        Assert.Equal(["css/styl.css", "index.html"], zip.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public async Task Nieznany_projekt_to_404_a_nie_awaria()
    {
        // Stary albo uciety link jest normalna sytuacja klienta (decyzja 3: bez kont).
        var odpowiedz = await _klient.GetAsync($"/pobierz/{Guid.NewGuid()}/1");

        Assert.Equal(HttpStatusCode.NotFound, odpowiedz.StatusCode);
    }

    [Fact]
    public async Task Wersja_ktorej_nie_ma_to_tez_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _klient.GetAsync($"/pobierz/{_id}/7")).StatusCode);
    }

    public void Dispose()
    {
        _klient.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
