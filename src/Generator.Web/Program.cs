using Generator.Web.Components;
using Generator.Web.Contracts;
using Generator.Web.Mock;

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

app.Run();
