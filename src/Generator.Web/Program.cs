using Generator.Web.Components;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Generator.Web.Preview;

var builder = WebApplication.CreateBuilder(args);

// Jeden katalog na snapshoty: mock go zapisuje, /preview z niego czyta. Gdyby te dwie
// sciezki sie rozjechaly, iframe byłby pusty, a testy komponentow tego nie widza.
var snapshotRoot = builder.Configuration["Projects:Root"]
    ?? Path.Combine(Path.GetTempPath(), "generator-stron");

// Backend w pamieci do czasu Planu B. Podmiana na klienta HTTP dotknie tej jednej linii.
builder.Services.AddSingleton<IProjectApi>(_ => new MockProjectApi { SnapshotRoot = snapshotRoot });

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
app.MapPreview((projectId, version) => Path.Combine(snapshotRoot, projectId, $"v{version}"));

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
