using Generator.Web.Components;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Generator.Web.Preview;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Backend w pamieci do czasu Planu B. Podmiana na klienta HTTP dotknie tej jednej linii.
builder.Services.AddSingleton<IProjectApi, MockProjectApi>();

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
app.MapPreview((projectId, version) =>
    Path.Combine(builder.Configuration["Projects:Root"] ?? Path.GetTempPath(), projectId, $"v{version}"));

app.Run();
