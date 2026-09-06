using Generator.Engine.Versioning;
using Generator.Web.Api;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Generator.Web.Components.Pages;
using Generator.Web.Panel;
using Generator.Web.Preview;
using Microsoft.AspNetCore.Http.HttpResults;
using Generator.Engine.Sources;

var builder = WebApplication.CreateBuilder(args);

// Jeden katalog na snapshoty: mock go zapisuje, /preview z niego czyta. Gdyby te dwie
// sciezki sie rozjechaly, iframe byłby pusty, a testy komponentow tego nie widza.
//
// Domyslnie OBOK APLIKACJI, nie w katalogu tymczasowym systemu. Tam lezy caly
// dorobek klienta: snapshoty wersji, uwagi, project.json — a na Linuksie /tmp
// znika przy restarcie maszyny. Uruchomienie „z pudelka" bylo wiec uruchomieniem,
// ktore po cichu gubi oplacone strony, i nie bylo o tym slowa w zadnym
// appsettings*.json. Teraz wpis tam JEST — zeby bylo widac, gdzie to lezy.
//
// Sciezka wzgledna liczy sie od ContentRoot, nie od katalogu roboczego procesu:
// „projekty" ma znaczyc to samo niezaleznie od tego, skad ktos uruchomil aplikacje.
// Sciezke bezwzgledna (tak podaja ja testy) `Path.GetFullPath` zostawia w spokoju.
var skonfigurowanyRoot = builder.Configuration["Projects:Root"];
var snapshotRoot = string.IsNullOrWhiteSpace(skonfigurowanyRoot)
    ? Path.Combine(builder.Environment.ContentRootPath, "projekty")
    : Path.GetFullPath(skonfigurowanyRoot, builder.Environment.ContentRootPath);

var claudeCmd = builder.Configuration["Projects:ClaudeCmd"] ?? "claude";

builder.Services.AddSingleton(new ProjectPaths(snapshotRoot, claudeCmd));
builder.Services.AddSingleton<JobQueue>();

// Osobna rejestracja, nie `new` w ProjectApi: to jest jedyne miejsce w calej aplikacji,
// ktore siega do OBCEJ sieci pod adres podany przez klienta. Ma byc widoczne w skladzie
// zaleznosci i podmienialne w testach HTTP, zeby nie chodzily do internetu.
builder.Services.AddSingleton<IZrodloStrony>(_ => new ZrodloStrony());

// Ruling 9: mock zostaje. Praca nad UI bez zainstalowanego `claude` i galezie
// `failed` z /dev/wynik-rundy stoja na nim. Domyslnie WYLACZONY.
if (builder.Configuration.GetValue("Projects:UseMock", false))
{
    builder.Services.AddSingleton<IProjectApi>(_ => new MockProjectApi
    {
        SnapshotRoot = snapshotRoot,

        // W devie powtorka po chwili sie udaje, zeby dalo sie zobaczyc, ze polling podnosi
        // wynik bez odswiezania strony. Bez tego `retrying` wisialoby w nieskonczonosc.
        RetryResolvesAfter = builder.Environment.IsDevelopment() ? TimeSpan.FromSeconds(4) : null,

        // Atrapa konczy zadanie w 2 s, a prawdziwy model potrzebowal 275 s (zmierzone).
        // Ekranu czekania nie dalo sie przez to ani obejrzec, ani pokazac drugiej
        // osobie — a to on decyduje, czy klient wytrzyma te piec minut.
        // `Projects:MockDelaySeconds=20` i widac wszystko: etapy, licznik, szkielety.
        SimulatedDelay = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("Projects:MockDelaySeconds", 2.0)),
    });
}
else
{
    // ProjectPaths rozwiazywane Z KONTENERA, nie zamkniete w domkniecie: testy HTTP
    // podmieniaja je na atrape silnika przez RemoveAll/AddSingleton, a to dziala tylko
    // wtedy, gdy rejestracja siega po nie w chwili budowania IProjectApi.
    builder.Services.AddSingleton<IProjectApi>(sp => new ProjectApi(
        sp.GetRequiredService<ProjectPaths>(),
        sp.GetRequiredService<JobQueue>(),
        sp.GetRequiredService<ILogger<ProjectApi>>(),
        sp.GetRequiredService<IZrodloStrony>()));
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Sprzatanie po poprzednim procesie — PRZED przyjeciem pierwszego zadania.
// Kolejka zyje w pamieci, wiec restart (u nas: kazdy deploy) gubi runde w locie.
// Bez tego przegladarka klienta odpytuje `jobId` sprzed awarii i dostaje czterysta
// czwórke, czyli „nie znam takiego zadania" za runde, ktora zostala oplacona.
// Zadanie NIE jest wznawiane — patrz `OdzyskiwaniePoStarcie`.
//
// Wolane wprost, nie jako IHostedService: test ma przechodzic dokladnie ta sama
// droga co aplikacja, bez budowania hosta.
new OdzyskiwaniePoStarcie(
    app.Services.GetRequiredService<ProjectPaths>(),
    app.Services.GetRequiredService<JobQueue>()).Wykonaj();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Podglad snapshotu wersji. Skrypt zbierajacy klikniecia doklejamy TUTAJ, przy
// serwowaniu - snapshot na dysku i ZIP klienta zostaja czyste.
// Jedna prawda o tym, gdzie lezy snapshot: VersionStore. Recznie sklejana sciezka
// „snapshotRoot/id/vN" zgadzala sie tylko z atrapa — silnik pisze do
// „snapshotRoot/id/versions/001" i podglad pokazywalby pusty iframe.
app.MapPreview((projectId, version) =>
    new VersionStore(Path.Combine(snapshotRoot, projectId)).SnapshotPath(version));

// Pobranie strony kliknieciem w link. Ten sam katalog co podglad i ta sama straz
// (GUID projektu). Osobna trasa, bo endpoint ZIP pod /api wymaga naglowka
// X-Project-Token, ktorego zwykly <a href> nie wysle — czyli z UI byl nieosiagalny
// i klient nie mial jak odebrac tego, za co zaplacil.
app.MapPobierz((projectId, version) =>
    new VersionStore(Path.Combine(snapshotRoot, projectId)).SnapshotPath(version));

// Kontrakt 1. Nasze Blazorowe UI chodzi po IProjectApi w procesie — te trasy sa dla
// klientow spoza niego.
app.MapApi();

// TYLKO Development. Gałęzie `failed` z kontraktu 4.3 nie mają wyzwalacza w UI —
// bez tego nie da się ich przejść jak klient, a to jedyna rzecz, której testy
// komponentów nie zastąpią. Za `IsDevelopment()`, bo w produkcji nie ma mocka
// i nikt z zewnątrz nie może przestawiać wyniku rundy.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/wynik-rundy/{wynik}", (string wynik, IProjectApi api) =>
    {
        if (api is not MockProjectApi mock) return Results.NotFound();
        if (!Enum.TryParse<MockJobOutcome>(wynik, ignoreCase: true, out var outcome))
            return Results.BadRequest("success | retrying | halted");

        mock.NextJobOutcome = outcome;
        return Results.Ok($"kolejna runda: {outcome}");
    });
}

// Panel wewnetrzny — dla nas dwojga, nie dla klientow. Montowany TYLKO gdy klucz
// jest ustawiony: bez niego trasy nie ma i adres oddaje 404. To nie jest przesada.
// Panel pokazuje naraz opisy wszystkich klientow (a te zawieraja adresy i telefony)
// oraz koszty, ktorych kontrakt 4.1 zabrania wypuszczac do klienta. Domyslne
// uruchomienie „z pudelka" nie moze tego wystawic dlatego, ze ktos nie doczytal.
//
// Poza routerem Blazora (MapRazorComponents) swiadomie: router bierze KAZDY komponent
// z „@page" w assembly, wiec strona z dyrektywa istnialaby zawsze, niezaleznie od
// konfiguracji. Tu o istnieniu trasy decyduje „if".
if (KluczPanelu.Ustawiony(builder.Configuration) is { } kluczPanelu)
{
    const string ciastkoSesji = "panel";
    var sesje = new SesjePanelu();

    // Ciastko trzyma IDENTYFIKATOR sesji, nigdy `Admin:Token`. Sekret otwierajacy
    // dane wszystkich klientow nie ma prawa lezec na dysku przegladarki ani jechac
    // w kazdym zadaniu — identyfikator mozna uniewaznic, tokena z appsettings nie.
    CookieOptions Ciastko(HttpRequest zadanie) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        // Zadania klienta o /preview i /editor nie maja powodu wozic ze soba ciastka
        // panelu — ograniczamy je do jednej sciezki.
        Path = "/panel",
        // Z zadania, nie na sztywno: `Secure` na http://localhost kazaloby przegladarce
        // wyrzucic ciastko PO CICHU (panel „nie dziala bez powodu"), a pod HTTPS jest
        // konieczne. Panel chodzi u nas lokalnie, ale ma nie zepsuc sie, jesli kiedys
        // stanie za TLS-em.
        Secure = zadanie.IsHttps,
        IsEssential = true,
        // Bez `Expires`: ciastko sesyjne ginie z zamknieciem przegladarki, a drugim,
        // niezaleznym limitem jest `SesjePanelu.Zycie` po stronie serwera.
    };

    // `Results.SeeOther` nie istnieje, a `Results.Redirect` daje 302. Roznica jest
    // realna: 302 pozwala przegladarce powtorzyc metode, 303 nakazuje GET — i to
    // wlasnie 303 jest kodem opisujacym „formularz przyjety, wynik jest pod tym
    // adresem" (POST/Redirect/GET).
    IResult Przekieruj(HttpResponse odpowiedz, string dokad)
    {
        odpowiedz.Headers.Location = dokad;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    IResult Lista(ProjectPaths sciezki)
    {
        var dane = new PanelDane(sciezki);
        var wiersze = dane.Wczytaj();
        return new RazorComponentResult<Panel>(
            new { Dane = wiersze, Suma = dane.Podsumuj(wiersze) });
    }

    app.MapGet("/panel", (HttpRequest zadanie, ProjectPaths sciezki) =>
        sesje.Wazna(zadanie.Cookies[ciastkoSesji])
            ? Lista(sciezki)
            : (IResult)new RazorComponentResult<Panel>(new { Odmowa = false }));

    // Klucz idzie POSTem w ciele, nie w adresie: adres laduje w historii przegladarki,
    // w naglowku Referer i w logu dostepowym serwera, a to sekret otwierajacy
    // wszystkie projekty naraz.
    app.MapPost("/panel", async (HttpContext kontekst) =>
    {
        var podany = (await kontekst.Request.ReadFormAsync())["klucz"].ToString();
        IResult Odmowa() => new RazorComponentResult<Panel>(new { Odmowa = true }) { StatusCode = 401 };
        // 401, a nie 404 — swiadomie. 404 ukrywaloby sam fakt, ze panel tu jest, ale
        // GET /panel i tak pokazuje formularz kazdemu, kto zgadnie adres, wiec ukrywanie
        // POSTa niczego by nie zamknelo. Bez klucza (przypadek wyzej) trasy nie ma
        // w ogole i wtedy ukryte jest wszystko — i to jest granica, ktora liczy sie
        // naprawde.
        if (!KluczPanelu.Zgadza(kluczPanelu, podany))
            return Odmowa();

        kontekst.Response.Cookies.Append(ciastkoSesji, sesje.Utworz(), Ciastko(kontekst.Request));

        // Przekierowanie, a nie tresc. Gdyby POST odpowiadal lista, w pasku adresu
        // zostawaloby zadanie POST i kazde F5 witaloby pytaniem „wyslac formularz
        // ponownie?", a powrot Wstecz z podgladu ladowalby w tym samym miejscu.
        // Po 303 stoi tam GET, ktory mozna powtarzac do woli.
        return Przekieruj(kontekst.Response, "/panel");
    })
    // Zadne z zadan panelu nie zmienia stanu projektow, a samo ciastko sesji nie
    // wystarcza do niczego bez znajomosci klucza przy wejsciu. Antyforgery dokladaloby
    // token do formularza, ktory i tak nie ma czego zepsuc.
    .DisableAntiforgery();

    app.MapPost("/panel/wyloguj", (HttpContext kontekst) =>
    {
        // Sesja ginie PO STRONIE SERWERA. Samo skasowanie ciastka zamykaloby panel
        // tylko temu, kto grzecznie je oddaje — a nie temu, kto zdazyl je skopiowac.
        sesje.Zakoncz(kontekst.Request.Cookies[ciastkoSesji]);
        kontekst.Response.Cookies.Delete(ciastkoSesji, Ciastko(kontekst.Request));
        return Przekieruj(kontekst.Response, "/panel");
    }).DisableAntiforgery();
}

app.Run();

/// Widoczne dla WebApplicationFactory w testach — Program z top-level statements
/// jest domyslnie internal, a testy sa w innym assembly.
public partial class Program;
