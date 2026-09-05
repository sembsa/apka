using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Generator.Web.Contracts;

namespace Generator.Web.Api;

public static class ApiEndpoints
{
    /// Kontrakt 1. Cienka warstwa nad ProjectApi — zadnej logiki poza autoryzacja
    /// tokenem i mapowaniem wyjatkow na kody. Blazor NIE chodzi tedy (wola
    /// IProjectApi wprost), wiec kazda regula zapisana tylko tutaj bylaby regula,
    /// ktorej nasze wlasne UI nie przestrzega.
    public static void MapApi(this WebApplication app)
    {
        // `{id:guid}` na KAZDEJ trasie z projektem. `ProjectPaths.Dir(id)` to
        // `Path.Combine(root, id)` bez zadnej walidacji, a `ChooseProposalAsync`
        // i `RollbackAsync` pod ta sciezka ZAPISUJA. Wykonawca Zadania 5 zamknal
        // biala lista `proposalId`, bo „idzie prosto z URL-a" — o `id` jest to samo
        // prawdziwe. Ograniczenie trasy jest tansze i pewniejsze niz walidacja
        // w kazdej metodzie z osobna: trasa, ktora nie pasuje, daje 404 i nigdy
        // nie dochodzi do sklejania sciezki.
        var grupa = app.MapGroup("/api");

        grupa.MapPost("/projects", async (NowyProjekt body, IProjectApi api) =>
        {
            var p = await api.CreateAsync(body.Source, body.Description);
            return Results.Created($"/api/projects/{p.Id}", p);
        });

        grupa.MapGet("/projects/{id:guid}", (string id, IProjectApi api, HttpContext ctx) =>
            Chroniony(id, api, ctx, p => Task.FromResult(Results.Ok(p))));

        grupa.MapPost("/projects/{id:guid}/proposals", (string id, IProjectApi api, HttpContext ctx) =>
            Chroniony(id, api, ctx, async _ =>
                Results.Accepted(value: new { jobId = await api.RequestProposalsAsync(id) })));

        grupa.MapGet("/projects/{id:guid}/proposals", (string id, IProjectApi api, HttpContext ctx) =>
            Chroniony(id, api, ctx, async _ => Results.Ok(await api.GetProposalsAsync(id))));

        grupa.MapPost("/projects/{id:guid}/proposals/{proposalId}/choose",
            (string id, string proposalId, IProjectApi api, HttpContext ctx) =>
                Chroniony(id, api, ctx, async _ =>
                {
                    await api.ChooseProposalAsync(id, proposalId);
                    return Results.Ok();
                }));

        grupa.MapPost("/projects/{id:guid}/versions", (string id, IProjectApi api, HttpContext ctx) =>
            Chroniony(id, api, ctx, async _ =>
                Results.Accepted(value: new { jobId = await api.CreateFirstVersionAsync(id) })));

        grupa.MapGet("/projects/{id:guid}/versions", (string id, IProjectApi api, HttpContext ctx) =>
            Chroniony(id, api, ctx, p => Task.FromResult(Results.Ok(p.Versions))));

        grupa.MapPost("/projects/{id:guid}/versions/{n:int}/comments/apply",
            (string id, int n, CommentDto[] uwagi, IProjectApi api, HttpContext ctx) =>
                Chroniony(id, api, ctx, async _ =>
                    Results.Accepted(value: new { jobId = await api.ApplyCommentsAsync(id, n, uwagi) })));

        grupa.MapGet("/projects/{id:guid}/versions/{n:int}/comments",
            (string id, int n, IProjectApi api, HttpContext ctx) =>
                Chroniony(id, api, ctx, async _ => Results.Ok(await api.GetCommentsAsync(id, n))));

        grupa.MapPost("/projects/{id:guid}/rollback/{n:int}",
            (string id, int n, IProjectApi api, HttpContext ctx) =>
                Chroniony(id, api, ctx, async _ =>
                {
                    await api.RollbackAsync(id, n);
                    return Results.Ok();
                }));

        grupa.MapGet("/projects/{id:guid}/versions/{n:int}/zip",
            (string id, int n, IProjectApi api, ProjectPaths paths, HttpContext ctx) =>
                Chroniony(id, api, ctx, _ =>
                {
                    // SnapshotPath(n), nie WorkDir: klient pobiera WERSJE, ktora oglada,
                    // a work/ trzyma stan po ostatniej rundzie. Po powrocie do v1 albo
                    // przy pobraniu starszej wersji te dwie rzeczy to rozne strony.
                    var katalog = paths.Versions(id).SnapshotPath(n);
                    if (!Directory.Exists(katalog))
                        return Task.FromResult(Results.NotFound());

                    // Do pamieci, nie do pliku tymczasowego: strona z decyzji 4 to
                    // kilka plikow tekstowych, a plik tymczasowy trzeba by sprzatac
                    // takze wtedy, gdy klient zerwie polaczenie w polowie pobierania.
                    var bufor = new MemoryStream();
                    using (var zip = new ZipArchive(bufor, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var plik in Directory.EnumerateFiles(katalog, "*", SearchOption.AllDirectories))
                        {
                            // Nazwy wpisow zawsze z ukosnikiem w przod. `GetRelativePath`
                            // oddaje separator SYSTEMU, wiec ZIP zbudowany na Windows
                            // (druga osoba pracuje na Windows) mialby wpisy „css\styl.css"
                            // — jeden plik o dziwnej nazwie zamiast katalogu.
                            var wpis = Path.GetRelativePath(katalog, plik)
                                .Replace(Path.DirectorySeparatorChar, '/');
                            zip.CreateEntryFromFile(plik, wpis);
                        }
                    }
                    bufor.Position = 0;
                    return Task.FromResult(Results.File(bufor, "application/zip", $"strona-v{n}.zip"));
                }));

        grupa.MapGet("/jobs/{jobId}", (string jobId, IProjectApi api) =>
            Zamien(async () => Results.Ok(await api.GetJobAsync(jobId))));
    }

    private record NowyProjekt(string Source, string Description);

    /// Ruling 8: token z `?token=` na GET (link otwierany w przegladarce) albo
    /// z naglowka `X-Project-Token` na POST. Porownanie idzie po odczytaniu
    /// projektu, wiec nieistniejacy projekt daje 404 przed sprawdzeniem tokenu —
    /// i tak nie ma czego chronic, a odwrotna kolejnosc dawalaby 401 na wszystko
    /// i uniemozliwiala odroznienie zlego linku od zlego tokenu.
    private static Task<IResult> Chroniony(
        string id, IProjectApi api, HttpContext ctx, Func<ProjectView, Task<IResult>> praca) =>
        Zamien(async () =>
        {
            var projekt = await api.GetAsync(id);
            var podany = ctx.Request.Headers["X-Project-Token"].FirstOrDefault()
                ?? ctx.Request.Query["token"].FirstOrDefault();

            return podany is null || !CryptographicOperations.FixedTimeEquals(
                       Encoding.UTF8.GetBytes(podany),
                       Encoding.UTF8.GetBytes(projekt.Token))
                ? Results.Unauthorized()
                : await praca(projekt);
        });

    /// Wyjatki dziedzinowe na kody kontraktu. Bez tego kazdy z nich wychodzi
    /// jako 500 i frontend nie ma jak odroznic „projekt zamrozony" od awarii.
    /// Kolejnosc jest wymuszona: trzy pierwsze dziedzicza po InvalidOperationException.
    private static async Task<IResult> Zamien(Func<Task<IResult>> praca)
    {
        try { return await praca(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ProjectFrozenException e) { return Results.Conflict(e.Message); }
        catch (StaleVersionException e) { return Results.Conflict(e.Message); }
        catch (JobRunningException e) { return Results.Conflict(e.Message); }

        // Kolejka zamknieta = proces sie zamyka. To nie jest blad zadania klienta,
        // wiec nie 500: 503 mowi „sprobuj za chwile" i tak to rozumie kazdy klient HTTP.
        catch (ObjectDisposedException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }

        // Reszta strazy ProjectApi (druga wersja pierwsza, wybor propozycji przed
        // wygenerowaniem) rzuca golym InvalidOperationException. To konflikt STANU
        // projektu, a nie awaria serwera — 500 mowilby klientowi „to nasza wina"
        // o rzeczy, ktora zrobil sam i ktorej nie powtorzy z sukcesem.
        catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
    }
}
