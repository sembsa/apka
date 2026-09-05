using Generator.Engine.Versioning;
using Generator.Web.Api;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Generator.Web.Preview;

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
        sp.GetRequiredService<ILogger<ProjectApi>>()));
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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

app.Run();

/// Widoczne dla WebApplicationFactory w testach — Program z top-level statements
/// jest domyslnie internal, a testy sa w innym assembly.
public partial class Program;
