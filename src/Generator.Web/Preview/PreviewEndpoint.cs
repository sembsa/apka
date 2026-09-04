using Generator.Web.Contracts;

namespace Generator.Web.Preview;

public static class PreviewEndpoint
{
    /// <param name="snapshotDir">(projectId, numerWersji) -> katalog snapshotu na dysku</param>
    public static void MapPreview(this WebApplication app, Func<string, int, string> snapshotDir)
    {
        app.MapGet("/preview/{projectId}/{version:int}/{**sciezka}",
            async (string projectId, int version, string? sciezka,
                   bool? zmienione, IProjectApi api, HttpContext ctx) =>
        {
            var katalog = Path.GetFullPath(snapshotDir(projectId, version));
            var plik = Path.GetFullPath(Path.Combine(
                katalog, string.IsNullOrEmpty(sciezka) ? "index.html" : sciezka));

            // Wyjscie poza katalog snapshotu: sciezka podana przez klienta nie moze
            // czytac naszego dysku (../../ w URL-u).
            if (!plik.StartsWith(katalog + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(plik))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (plik.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                // Flaga, nie lista kotwic w URL-u. Zbior zmienionych blokow zna serwer;
                // przyjmowanie go z query string dawaloby drugie zrodlo prawdy i wpuszczaloby
                // dowolna tresc do selektora CSS.
                var podswietl = zmienione == true
                    ? await ZmienioneWWersji(api, projectId, version)
                    : null;

                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(PreviewInjector.Inject(
                    await File.ReadAllTextAsync(plik), "/js/preview-click.js", podswietl));
                return;
            }

            ctx.Response.ContentType = plik.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                ? "text/css" : "application/octet-stream";
            await ctx.Response.SendFileAsync(plik);
        });
    }

    /// <summary>
    /// Podglad moze byc otwarty na wersji, ktorej juz nie ma w projekcie (stary link),
    /// albo projektu, ktory zniknal — brak podswietlenia jest wtedy wlasciwa odpowiedzia,
    /// a nie 500 w twarz klienta ogladajacego wlasna strone.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> ZmienioneWWersji(
        IProjectApi api, string projectId, int version)
    {
        try
        {
            var projekt = await api.GetAsync(projectId);
            return projekt.Versions.FirstOrDefault(v => v.Number == version)?.ChangedAnchors;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
