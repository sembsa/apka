namespace Generator.Web.Preview;

public static class PreviewEndpoint
{
    /// <param name="snapshotDir">(projectId, numerWersji) -> katalog snapshotu na dysku</param>
    public static void MapPreview(this WebApplication app, Func<string, int, string> snapshotDir)
    {
        app.MapGet("/preview/{projectId}/{version:int}/{**sciezka}",
            async (string projectId, int version, string? sciezka, HttpContext ctx) =>
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
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(
                    PreviewInjector.Inject(await File.ReadAllTextAsync(plik), "/js/preview-click.js"));
                return;
            }

            ctx.Response.ContentType = plik.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                ? "text/css" : "application/octet-stream";
            await ctx.Response.SendFileAsync(plik);
        });
    }
}
