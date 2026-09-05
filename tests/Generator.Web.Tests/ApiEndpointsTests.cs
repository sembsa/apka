using System.Net;
using System.Net.Http.Json;
using Generator.Web.Api;
using Generator.Web.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Generator.Web.Tests;

public class ApiEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-http-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _app;

    /// Aplikacja BEZ naszych nadpisan — jedyny sposob, zeby zobaczyc konfiguracje
    /// domyslna. Kazdy inny test podaje wlasny `Projects:Root`, wiec wartosc, ktora
    /// dostaje klient uruchamiajacy „z pudelka", nie jest sprawdzana nigdzie indziej.
    private readonly WebApplicationFactory<Program> _domyslny;

    /// PRAWDZIWY ProjectApi, atrapowany tylko silnik. Wariant z `Projects:UseMock`
    /// bylby latwiejszy, ale testowalby parytet mocka z kontraktem zamiast kodu,
    /// ktory pojdzie na produkcje — a to wlasnie mock ma prawo sie rozjechac.
    public ApiEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _domyslny = factory;
        _app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Projects:Root", _root);
            b.ConfigureServices(uslugi =>
            {
                uslugi.RemoveAll<ProjectPaths>();
                uslugi.AddSingleton(new ProjectPaths(_root, id => new FakeRunner(
                    new Generator.Engine.Storage.ProjectStore(Path.Combine(_root, id)),
                    new Generator.Engine.Versioning.VersionStore(Path.Combine(_root, id)))));
            });
        });
    }

    [Fact]
    public void Domyslny_katalog_projektow_lezy_obok_aplikacji_a_nie_w_tymczasowym()
    {
        // Bez konfiguracji projekty klientow ladowaly w `Path.GetTempPath()`, a wpisu
        // nie bylo w zadnym appsettings*.json — wiec nikt nie widzial, gdzie to lezy.
        // Na Linuksie /tmp znika przy restarcie maszyny: uruchomienie „z pudelka"
        // bylo uruchomieniem, ktore gubi oplacone strony klientow razem z historia
        // wersji i uwagami.
        var root = Path.GetFullPath(_domyslny.Services.GetRequiredService<ProjectPaths>().Root);
        var aplikacja = Path.GetFullPath(
            _domyslny.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath);

        Assert.StartsWith(aplikacja, root);
        Assert.DoesNotContain(Path.GetFullPath(Path.GetTempPath()), root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Wejscie_na_strone_propozycji_nie_zamawia_ich_samo_z_siebie()
    {
        // Prerender uruchamialby `OnInitializedAsync` na serwerze JUZ przy tym GET,
        // a `Proposals` zamawia w nim trzy szkice — czyli okolo pol dolara przy
        // kazdym F5 i przy wejsciu wprost na ten adres, ZANIM klient cokolwiek kliknie.
        // Testy komponentow tego nie widza (bUnit renderuje od razu interaktywnie),
        // wiec to jedyna warstwa, na ktorej ta wlasnosc da sie sprawdzic.
        var klient = _app.CreateClient();
        var p = await (await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "kwiaciarnia" }))
            .Content.ReadFromJsonAsync<ProjectView>();

        var odp = await klient.GetAsync($"/proposals/{p!.Id}");
        Assert.Equal(HttpStatusCode.OK, odp.StatusCode);

        // `RunProposalsAsync` tworzy ten katalog jako pierwsza rzecz. Brak katalogu
        // = zadanie nie ruszylo.
        Assert.False(Directory.Exists(Path.Combine(_root, p.Id, "proposals")));
    }

    [Fact]
    public async Task Tworzenie_projektu_zwraca_201_z_tokenem()
    {
        var klient = _app.CreateClient();
        var odp = await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "kwiaciarnia" });

        Assert.Equal(HttpStatusCode.Created, odp.StatusCode);
        var p = await odp.Content.ReadFromJsonAsync<ProjectView>();
        Assert.NotEmpty(p!.Token);
    }

    [Fact]
    public async Task Bez_tokenu_nie_da_sie_czytac_cudzego_projektu()
    {
        // Decyzja 3: projekt = link z tokenem. Token jest JEDYNYM zabezpieczeniem,
        // wiec endpoint bez niego to endpoint bez ochrony.
        var klient = _app.CreateClient();
        var p = await (await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "x" })).Content.ReadFromJsonAsync<ProjectView>();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await klient.GetAsync($"/api/projects/{p!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await klient.GetAsync($"/api/projects/{p.Id}?token=zle")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await klient.GetAsync($"/api/projects/{p.Id}?token={p.Token}")).StatusCode);
    }

    [Fact]
    public async Task Stan_zadania_wymaga_tokenu_projektu_do_ktorego_nalezy()
    {
        // §1 mowi: „`/api/*` wymaga tokenu projektu". `/api/jobs/{id}` byla jedyna
        // trasa, ktora szla przez `Zamien`, a nie `Chroniony` — czyli jedyna, ktora
        // oddawala stan cudzego projektu kazdemu, kto zna sam `jobId`.
        var klient = _app.CreateClient();
        var p = await (await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "kwiaciarnia" })).Content.ReadFromJsonAsync<ProjectView>();
        var jobId = await Zadanie(klient, p!, $"/api/projects/{p!.Id}/proposals");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await klient.GetAsync($"/api/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await klient.GetAsync($"/api/jobs/{jobId}?token=zle")).StatusCode);

        // Cudzy WAZNY token tez nie otwiera cudzego zadania — sprawdzamy token
        // projektu, DO KTOREGO to zadanie nalezy, a nie „jakikolwiek prawdziwy".
        var obcy = await (await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "obcy" })).Content.ReadFromJsonAsync<ProjectView>();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await klient.GetAsync($"/api/jobs/{jobId}?token={obcy!.Token}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await klient.GetAsync($"/api/jobs/{jobId}?token={p.Token}")).StatusCode);
    }

    [Fact]
    public async Task Nieznane_zadanie_to_404_a_nie_401()
    {
        // Ta sama kolejnosc co przy projekcie: najpierw „nie ma czego chronic",
        // dopiero potem token. Odwrotnie kazde zapytanie dawaloby 401 i nie dalo
        // sie odroznic zlego linku od zlego tokenu.
        var odp = await _app.CreateClient().GetAsync($"/api/jobs/{Guid.NewGuid()}?token=x");
        Assert.Equal(HttpStatusCode.NotFound, odp.StatusCode);
    }

    [Fact]
    public async Task Nieznany_projekt_to_404_a_nie_500()
    {
        var odp = await _app.CreateClient().GetAsync($"/api/projects/{Guid.NewGuid()}?token=x");
        Assert.Equal(HttpStatusCode.NotFound, odp.StatusCode);
    }

    [Fact]
    public async Task Runda_na_zamrozonym_projekcie_to_409()
    {
        var klient = _app.CreateClient();
        var p = await Gotowy(klient);

        var store = new Generator.Engine.Storage.ProjectStore(Path.Combine(_root, p.Id));
        store.Save(store.Load() with { Status = Generator.Engine.Model.ProjectStatus.Frozen });

        var zad = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{p.Id}/versions/1/comments/apply")
        {
            Content = JsonContent.Create(Array.Empty<CommentDto>()),
        };
        zad.Headers.Add("X-Project-Token", p.Token);

        Assert.Equal(HttpStatusCode.Conflict, (await klient.SendAsync(zad)).StatusCode);
    }

    [Fact]
    public async Task ZIP_wersji_jest_prawdziwym_archiwum_ze_snapshotem_TEJ_wersji()
    {
        var klient = _app.CreateClient();
        var p = await Gotowy(klient);

        // Druga wersja jest tu warunkiem, a nie ozdoba: dopoki projekt ma jedna wersje,
        // work/ i snapshot v1 maja identyczna tresc i ZIP zbudowany z dowolnego z nich
        // przechodzi. Po tej rundzie work/ trzyma juz v2 — i dopiero wtedy widac,
        // KTORA wersje klient naprawde pobral. Kontrakt 5.1: ZIP idzie za wersja,
        // ktora klient oglada, nie za ostatnim stanem katalogu roboczego.
        await Poczekaj(klient, p, await Zadanie(klient, p, $"/api/projects/{p.Id}/versions/1/comments/apply",
            JsonContent.Create(new[] { Uwaga("k1") })));

        var odp = await klient.GetAsync($"/api/projects/{p.Id}/versions/1/zip?token={p.Token}");

        Assert.Equal(HttpStatusCode.OK, odp.StatusCode);
        Assert.Equal("application/zip", odp.Content.Headers.ContentType!.MediaType);

        using var zip = new System.IO.Compression.ZipArchive(
            await odp.Content.ReadAsStreamAsync());
        var wpis = Assert.Single(zip.Entries, e => e.FullName == "index.html");

        using var czytnik = new StreamReader(wpis.Open());
        var html = await czytnik.ReadToEndAsync();
        Assert.Contains("v1", html);
        Assert.DoesNotContain("v2", html);
    }

    [Fact]
    public async Task Podglad_czyta_z_katalogu_w_ktorym_snapshot_zapisal_silnik()
    {
        // Trasa podgladu skleja sciezke sama, wiec moze sie rozjechac z VersionStore
        // bez jednego bledu kompilacji i bez jednego czerwonego testu komponentu —
        // klient dostaje wtedy pusty iframe. To jedyne miejsce, w ktorym te dwie
        // sciezki spotykaja sie naprawde: przez HTTP, na plikach z silnika.
        var klient = _app.CreateClient();
        var p = await Gotowy(klient);

        var odp = await klient.GetAsync($"/preview/{p.Id}/1/");

        Assert.Equal(HttpStatusCode.OK, odp.StatusCode);
        Assert.Contains("v1", await odp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Trasa_przyjmuje_tylko_identyfikator_w_ksztalcie_GUID()
    {
        // `ProjectPaths.Dir(id)` to `Path.Combine(root, id)` bez walidacji, a pod ta
        // sciezka `ChooseProposalAsync` i `RollbackAsync` ZAPISUJA. `{id:guid}` sprawia,
        // ze tekst z URL-a, ktory GUID-em nie jest, nie dochodzi nawet do sklejania.
        var klient = _app.CreateClient();
        var p = await (await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "x" })).Content.ReadFromJsonAsync<ProjectView>();

        // Ten sam, wazny projekt — tyle ze pod nazwa katalogu, ktora GUID-em nie jest.
        // Token sie zgadza, plik istnieje, wiec bez ograniczenia trasy odpowiedzia
        // byloby 200: nic poza `{id:guid}` tego nie zatrzymuje.
        Directory.CreateDirectory(Path.Combine(_root, "nie-guid"));
        File.Copy(Path.Combine(_root, p!.Id, "project.json"),
                  Path.Combine(_root, "nie-guid", "project.json"));

        Assert.Equal(HttpStatusCode.NotFound,
            (await klient.GetAsync($"/api/projects/nie-guid?token={p.Token}")).StatusCode);

        // Dowod, ze odmawia TRASA, a nie brak danych: ten sam plik pod GUID-em to 200.
        Assert.Equal(HttpStatusCode.OK,
            (await klient.GetAsync($"/api/projects/{p.Id}?token={p.Token}")).StatusCode);
    }

    /// Projekt z jedna wersja, przejsciem przez prawdziwe HTTP: propozycje -> wybor
    /// -> wersja 1, z czekaniem na kazde zadanie.
    private async Task<ProjectView> Gotowy(HttpClient klient)
    {
        var p = (await (await klient.PostAsJsonAsync("/api/projects",
            new { source = "idea", description = "kwiaciarnia" }))
            .Content.ReadFromJsonAsync<ProjectView>())!;

        await Poczekaj(klient, p, await Zadanie(klient, p, $"/api/projects/{p.Id}/proposals"));
        await Post(klient, p, $"/api/projects/{p.Id}/proposals/b/choose");
        await Poczekaj(klient, p, await Zadanie(klient, p, $"/api/projects/{p.Id}/versions"));
        return p;
    }

    private static async Task<HttpResponseMessage> Post(
        HttpClient klient, ProjectView p, string sciezka, HttpContent? tresc = null)
    {
        var zad = new HttpRequestMessage(HttpMethod.Post, sciezka) { Content = tresc };
        zad.Headers.Add("X-Project-Token", p.Token);
        var odp = await klient.SendAsync(zad);
        odp.EnsureSuccessStatusCode();
        return odp;
    }

    private record IdZadania(string JobId);

    private static async Task<string> Zadanie(
        HttpClient klient, ProjectView p, string sciezka, HttpContent? tresc = null) =>
        (await (await Post(klient, p, sciezka, tresc)).Content.ReadFromJsonAsync<IdZadania>())!.JobId;

    private static CommentDto Uwaga(string id) =>
        new(id, "hero", "tekst " + id, "desktop", DateTimeOffset.UnixEpoch, "open", null);

    /// Token jest tu OBOWIAZKOWY, odkad `/api/jobs/{id}` przestal byc jedyna trasa
    /// `/api/*` bez ochrony (§1). Bez niego to 401, nie 200.
    private static async Task Poczekaj(HttpClient klient, ProjectView p, string jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await klient.GetFromJsonAsync<JobView>($"/api/jobs/{jobId}?token={p.Token}");
            if (job!.Status == "succeeded") return;
            Assert.NotEqual("failed", job.Status);
            await Task.Delay(25);
        }
        Assert.Fail($"zadanie {jobId} nie skonczylo sie w 5 s");
    }
}
