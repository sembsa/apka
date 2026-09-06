using Generator.Engine.IO;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;
using Generator.Web.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Dowod na calej drodze, nie na samej klasie odzyskiwania: przegladarka klienta
/// odpytuje `jobId` sprzed awarii i ma dostac PRAWDE, a nie czterysta czwórke.
///
/// Projekt jest zapisywany na dysk PRZED zbudowaniem klienta HTTP, bo host startuje
/// dopiero przy pierwszym `CreateClient` — i to wtedy `Program.cs` uruchamia
/// odzyskiwanie. Odwrotna kolejnosc testowalaby aplikacje, ktora wstala nad pustym
/// katalogiem, czyli nic.
/// </summary>
public class OdzyskiwanieHttpTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-odzysk-http-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _fabryka;

    public OdzyskiwanieHttpTests(WebApplicationFactory<Program> factory) => _fabryka = factory;

    private (string id, string token, string jobId) ProjektPrzerwanyNaDysku()
    {
        var id = Guid.NewGuid().ToString();
        var store = new ProjectStore(Path.Combine(_root, id));
        var wersje = new VersionStore(Path.Combine(_root, id));
        Directory.CreateDirectory(wersje.WorkDir);
        File.WriteAllText(Path.Combine(wersje.WorkDir, "index.html"), "wersja 1");
        var v1 = wersje.Commit(1, "s-1", 0.30m, [], null, []);

        var jobId = Guid.NewGuid().ToString();
        var meta = store.Create(SourceKind.Idea) with
        {
            Id = id,
            Status = ProjectStatus.Active,
            Versions = [v1],
            CurrentVersion = 1,
            ActiveJobId = jobId,
        };
        store.Save(meta);
        return (id, meta.Token, jobId);
    }

    private HttpClient Klient() =>
        _fabryka.WithWebHostBuilder(b => b.UseSetting("Projects:Root", _root))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Po_restarcie_zadanie_przerwane_odpowiada_failed_a_nie_404()
    {
        var (_, token, jobId) = ProjektPrzerwanyNaDysku();

        var odpowiedz = await Klient().GetAsync($"/api/jobs/{jobId}?token={token}");

        Assert.Equal(HttpStatusCode.OK, odpowiedz.StatusCode);
        var zadanie = await odpowiedz.Content.ReadFromJsonAsync<JobView>();
        Assert.Equal("failed", zadanie!.Status);
        // `halted` mowi UI „klikanie ponownie nic nie zmieni" — a to prawda, bo
        // rundy nikt nie wznowi.
        Assert.Equal("halted", zadanie.Failure!.Handling);
    }

    [Fact]
    public async Task Odzyskane_zadanie_dalej_wymaga_tokenu_wlasciwego_projektu()
    {
        // Odzyskiwanie nie moze byc furtka: zadanie odtworzone z dysku podlega tej
        // samej ochronie co kazde inne (ruling 8).
        var (_, _, jobId) = ProjektPrzerwanyNaDysku();

        var odpowiedz = await Klient().GetAsync($"/api/jobs/{jobId}?token=nie-ten-token");

        Assert.Equal(HttpStatusCode.Unauthorized, odpowiedz.StatusCode);
    }

    public void Dispose()
    {
        DirectoryOps.Delete(_root);
        GC.SuppressFinalize(this);
    }
}
