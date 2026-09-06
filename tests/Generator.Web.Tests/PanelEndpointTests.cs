using Generator.Engine.IO;
using System.Net;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Panel wypisuje na jednej stronie opisy WSZYSTKICH klientow (a te zawieraja adresy
/// i telefony) oraz koszty, ktorych kontrakt 4.1 zabrania pokazywac klientowi.
/// Testy ochrony sa wiec czescia funkcji, nie dodatkiem do niej — i sprawdzaja
/// mocniejsza wlasnosc niz „zle haslo odmawia": ze bez skonfigurowanego klucza
/// TRASY W OGOLE NIE MA.
/// </summary>
public class PanelEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Klucz = "tajne-haslo-panelu";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-panel-http-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _fabryka;
    private readonly ProjectMeta _projekt;

    public PanelEndpointTests(WebApplicationFactory<Program> factory)
    {
        _fabryka = factory;
        var store = new ProjectStore(Path.Combine(_root, Guid.NewGuid().ToString()));
        _projekt = store.Create(SourceKind.Idea) with
        {
            Description = "Kwiaciarnia Anna, ul. Polna 3, tel. 600-100-200",
            SpentUsd = 2.50m,
            Versions = [new VersionMeta(1, "s", "d", 2.50m, [])],
            CurrentVersion = 1,
        };
        store.Save(_projekt);
    }

    private HttpClient Klient(string? adminToken) =>
        _fabryka.WithWebHostBuilder(b =>
        {
            b.UseSetting("Projects:Root", _root);
            if (adminToken is not null) b.UseSetting("Admin:Token", adminToken);
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static FormUrlEncodedContent Formularz(string klucz) =>
        new([new KeyValuePair<string, string>("klucz", klucz)]);

    [Fact]
    public async Task Bez_skonfigurowanego_klucza_panelu_po_prostu_nie_ma()
    {
        // Nie „formularz logowania" — 404. Uruchomienie „z pudelka" nie ma prawa
        // wystawic listy klientow dlatego, ze ktos nie doczytal README.
        var klient = Klient(adminToken: null);

        Assert.Equal(HttpStatusCode.NotFound, (await klient.GetAsync("/panel")).StatusCode);

        // POST sprawdzamy przez NIEODROZNIALNOSC od adresu, ktorego na pewno nie ma,
        // a nie przez goly kod 404: formularzowy POST przechodzi najpierw przez
        // `UseAntiforgery`, ktore odsyla 400 jeszcze przed routingiem. Wazna jest
        // wlasnosc, nie liczba — z odpowiedzi nie da sie wyczytac, czy panel istnieje.
        var naPanel = await klient.PostAsync("/panel", Formularz(Klucz));
        var naZmyslony = await klient.PostAsync("/zmyslona-trasa", Formularz(Klucz));

        Assert.Equal(naZmyslony.StatusCode, naPanel.StatusCode);
        Assert.DoesNotContain(_projekt.Id, await naPanel.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Pusty_klucz_w_konfiguracji_liczy_sie_jako_brak_klucza(string token)
    {
        // Realna pomylka: `"Token": ""` w appsettings. Przy zwyklym porownaniu
        // wpuscilaby kazdego, kto wysle puste pole formularza.
        Assert.Equal(HttpStatusCode.NotFound, (await Klient(token).GetAsync("/panel")).StatusCode);
    }

    [Fact]
    public async Task Zly_klucz_nie_pokazuje_ani_jednego_projektu()
    {
        var odpowiedz = await Klient(Klucz).PostAsync("/panel", Formularz("nie-ten-klucz"));

        Assert.Equal(HttpStatusCode.Unauthorized, odpowiedz.StatusCode);
        var tresc = await odpowiedz.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_projekt.Id, tresc);
        Assert.DoesNotContain("Kwiaciarnia", tresc);
    }

    [Fact]
    public async Task Puste_pole_formularza_nie_jest_kluczem()
    {
        var odpowiedz = await Klient(Klucz).PostAsync("/panel", Formularz(""));

        Assert.Equal(HttpStatusCode.Unauthorized, odpowiedz.StatusCode);
    }

    [Fact]
    public async Task Wejscie_bez_klucza_pokazuje_formularz_a_nie_dane()
    {
        var tresc = await Klient(Klucz).GetStringAsync("/panel");

        Assert.Contains("name=\"klucz\"", tresc);
        Assert.DoesNotContain(_projekt.Id, tresc);
    }

    [Fact]
    public async Task Z_kluczem_widac_projekt_klienta_z_kosztem_i_podgladem()
    {
        var tresc = await (await Klient(Klucz).PostAsync("/panel", Formularz(Klucz)))
            .Content.ReadAsStringAsync();

        Assert.Contains("Kwiaciarnia", tresc);
        Assert.Contains("2.50 USD", tresc);
        // Ukosnik na koncu jest czescia kontraktu 5.4, nie ozdoba.
        Assert.Contains($"/preview/{_projekt.Id}/1/", tresc);
    }

    [Fact]
    public async Task Panel_nie_drukuje_tokenow_klientow()
    {
        // Lista wszystkich projektow z tokenami byloby PEKIEM KLUCZY: token jest
        // jedynym uwierzytelnieniem `/api/*` (decyzja 3: bez kont, token w linku)
        // i nie rotuje sie. Podglad go nie potrzebuje — kontrakt 1 mowi, ze
        // `/preview/*` w v1 tokenu nie wymaga.
        var tresc = await (await Klient(Klucz).PostAsync("/panel", Formularz(Klucz)))
            .Content.ReadAsStringAsync();

        Assert.Contains(_projekt.Id, tresc);
        Assert.DoesNotContain(_projekt.Token, tresc);
    }

    public void Dispose()
    {
        DirectoryOps.Delete(_root);
        GC.SuppressFinalize(this);
    }
}
