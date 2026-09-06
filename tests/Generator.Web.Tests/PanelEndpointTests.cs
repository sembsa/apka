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

    /// Wejscie do panelu w calosci: POST z kluczem, a potem GET, na ktory ten POST
    /// przekierowuje. Tresc listy jest ODPOWIEDZIA NA GET, nie na POST — i o to
    /// wlasnie chodzi, bo dzieki temu F5 nie wysyla formularza ponownie.
    private static async Task<string> Wejdz(HttpClient klient, string klucz)
    {
        var odpowiedz = await klient.PostAsync("/panel", Formularz(klucz));
        Assert.Equal(HttpStatusCode.SeeOther, odpowiedz.StatusCode);
        return await klient.GetStringAsync(odpowiedz.Headers.Location!.ToString());
    }

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
        var tresc = await Wejdz(Klient(Klucz), Klucz);

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
        var tresc = await Wejdz(Klient(Klucz), Klucz);

        Assert.Contains(_projekt.Id, tresc);
        Assert.DoesNotContain(_projekt.Token, tresc);
    }

    [Fact]
    public async Task Dobry_klucz_przekierowuje_na_GET_zamiast_odpowiadac_lista()
    {
        // Sedno naprawy odswiezania. Gdyby POST odpowiadal trescia, w pasku adresu
        // zostawaloby zadanie POST i kazde F5 witaloby pytaniem „wyslac formularz
        // ponownie?". Po przekierowaniu stoi tam GET, ktory mozna powtarzac do woli.
        var odpowiedz = await Klient(Klucz).PostAsync("/panel", Formularz(Klucz));

        Assert.Equal(HttpStatusCode.SeeOther, odpowiedz.StatusCode);
        Assert.Equal("/panel", odpowiedz.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Po_wejsciu_sam_GET_pokazuje_liste_bez_ponownego_klucza()
    {
        // To jest ta wlasnosc, dla ktorej sesja w ogole powstala: powrot z podgladu
        // albo z edytora nie moze wymagac wklejania klucza od nowa.
        var klient = Klient(Klucz);
        await Wejdz(klient, Klucz);

        var tresc = await klient.GetStringAsync("/panel");

        Assert.Contains("Kwiaciarnia", tresc);
        Assert.DoesNotContain("name=\"klucz\"", tresc);
    }

    [Fact]
    public async Task Ciastko_nie_zawiera_klucza_panelu()
    {
        // W przegladarce ma lezec identyfikator sesji, ktory mozna uniewaznic —
        // a nie sekret otwierajacy dane wszystkich klientow, ktorego uniewaznic
        // sie nie da bez zmiany konfiguracji.
        var odpowiedz = await Klient(Klucz).PostAsync("/panel", Formularz(Klucz));

        var ciastko = Assert.Single(odpowiedz.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain(Klucz, ciastko);
        Assert.Contains("httponly", ciastko, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", ciastko, StringComparison.OrdinalIgnoreCase);
        // Sciezka ogranicza ciastko do panelu: zadania klienta o /preview i /editor
        // nie maja powodu go ze soba wozic.
        Assert.Contains("path=/panel", ciastko, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Zmyslone_ciastko_nie_wpuszcza()
    {
        var klient = Klient(Klucz);
        klient.DefaultRequestHeaders.Add("Cookie", "panel=zmyslony-identyfikator-sesji");

        var tresc = await klient.GetStringAsync("/panel");

        Assert.Contains("name=\"klucz\"", tresc);
        Assert.DoesNotContain(_projekt.Id, tresc);
    }

    [Fact]
    public async Task Wylogowanie_zamyka_dostep_takze_gdy_ciastko_zostalo_skopiowane()
    {
        // Sesja ginie PO STRONIE SERWERA, dlatego test nie polega na tym, ze klient
        // grzecznie skasowal swoje ciastko: wysylamy je jawnie po wylogowaniu.
        var klient = Klient(Klucz);
        var wejscie = await klient.PostAsync("/panel", Formularz(Klucz));
        var ciastko = wejscie.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        var wylogowanie = await klient.PostAsync("/panel/wyloguj", null);
        Assert.Equal(HttpStatusCode.SeeOther, wylogowanie.StatusCode);

        var zadanie = new HttpRequestMessage(HttpMethod.Get, "/panel");
        zadanie.Headers.Add("Cookie", ciastko);
        var tresc = await (await klient.SendAsync(zadanie)).Content.ReadAsStringAsync();

        Assert.Contains("name=\"klucz\"", tresc);
        Assert.DoesNotContain(_projekt.Id, tresc);
    }

    [Fact]
    public async Task Bez_skonfigurowanego_klucza_nie_ma_takze_trasy_wylogowania()
    {
        // Trzecia trasa siedzi pod tym samym „if" co dwie pozostale. Gdyby wyciekla
        // poza niego, byloby to jedyne miejsce, po ktorym da sie stwierdzic, ze
        // aplikacja w ogole ma panel.
        var klient = Klient(adminToken: null);

        var naWyloguj = await klient.PostAsync("/panel/wyloguj", null);
        var naZmyslona = await klient.PostAsync("/zmyslona-trasa", null);

        Assert.Equal(naZmyslona.StatusCode, naWyloguj.StatusCode);
    }

    public void Dispose()
    {
        DirectoryOps.Delete(_root);
        GC.SuppressFinalize(this);
    }
}
