namespace Generator.Web.Preview;

public static class PobierzEndpoint
{
    /// <summary>
    /// Pobranie strony przez KLIKNIECIE W LINK. Osobna trasa od `/api/.../zip`, bo tamta
    /// jest `Chroniony` — wymaga naglowka `X-Project-Token`, a przegladarka nie wysle
    /// naglowka ze zwyklego `<a href>`. Bez tej trasy endpoint ZIP istnial, ale UI nie
    /// mialo jak do niego siegnac i klient nie mial jak odebrac tego, za co zaplacil.
    ///
    /// Strzezona samym GUID-em — dokladnie tak jak `/preview/...`, ktore juz dzis wydaje
    /// KAZDY plik tej samej strony (kontrakt 1: „`/preview/*` w v1 tokenu nie wymaga").
    /// Paczka z tego samego katalogu nie otwiera wiec nowej klasy dostepu; GUID projektu
    /// jest poswiadczeniem do wszystkiego poza `/api/*`.
    /// </summary>
    /// <param name="snapshotDir">(projectId, numerWersji) -> katalog snapshotu na dysku</param>
    public static void MapPobierz(this WebApplication app, Func<string, int, string> snapshotDir)
    {
        app.MapGet("/pobierz/{projectId}/{version:int}", (string projectId, int version) =>
        {
            var katalog = snapshotDir(projectId, version);

            // Nieznany projekt i nieistniejaca wersja to jedna sytuacja: stary albo
            // uciety link. 404, nie 500 — klient ma zobaczyc „nie ma", a nie awarie.
            if (!Directory.Exists(katalog)) return Results.NotFound();

            return Results.File(Paczka.Zbuduj(katalog), "application/zip", Paczka.Nazwa(version));
        });
    }
}
