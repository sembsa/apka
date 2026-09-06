using Generator.Engine.Versioning;

namespace Generator.Web.Api;

/// <summary>
/// Sprzata po zadaniach, ktore zginely razem z poprzednim procesem. Wolane raz,
/// przy starcie aplikacji, ZANIM zacznie przyjmowac zadania.
///
/// Dlug 4: kolejka zyje w pamieci procesu. Na naszej maszynie restart gubiacy runde
/// to niedogodnosc; po wystawieniu publicznie KAZDY DEPLOY gubi runde oplacona przez
/// klienta, ktory zostaje z wiszacym ekranem i czterysta czwórka na `GET /api/jobs/{id}`.
///
/// Zadanie NIE jest wznawiane — i to jest decyzja, nie uproszczenie. Proces `claude`
/// zginal razem z aplikacja, wiec „wznowienie" znaczyloby uruchomienie rundy DRUGI
/// RAZ: drugi raz zaplacone, przy ryzyku zdublowanej wersji. Zamiast tego mowimy
/// prawde, co kontrakt juz przewiduje: 4.3 traktuje nieudana runde jako normalny
/// wynik, a 4.4 gwarantuje, ze nie zabiera klientowi niczego.
///
/// Trzy rzeczy na projekt, w tej kolejnosci:
///   1. zadanie wraca do kolejki jako `failed`/`halted`, razem z przypisaniem
///      do projektu (bez niego trasa i tak oddalaby 404 — sprawdza token PROJEKTU)
///   2. `work/` wraca do ostatniej udanej wersji — polprodukt nie moze zostac,
///      bo to on idzie do podgladu i do nastepnej rundy
///   3. znacznik `ActiveJobId` znika, inaczej projekt zostalby zablokowany na zawsze
/// </summary>
public class OdzyskiwaniePoStarcie(ProjectPaths paths, JobQueue kolejka)
{
    public void Wykonaj()
    {
        // Swiezy klon albo pierwszy start na nowym serwerze: `projekty/` jeszcze nie
        // istnieje. Start aplikacji nie moze sie na tym wywrocic.
        if (!Directory.Exists(paths.Root)) return;

        foreach (var katalog in Directory.EnumerateDirectories(paths.Root))
        {
            var id = Path.GetFileName(katalog);
            if (string.IsNullOrEmpty(id) || !paths.Exists(id)) continue;

            try
            {
                Odzyskaj(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or InvalidDataException or DirectoryNotFoundException)
            {
                // Lapiemy PRZY KAZDYM PROJEKCIE z osobna, tak jak panel przy czytaniu
                // listy: odzyskiwanie jest najbardziej potrzebne wtedy, gdy cos jest
                // zepsute. Jeden uszkodzony `project.json` nie moze zablokowac startu
                // aplikacji ani zostawic pozostalych klientow z wiszacym ekranem.
                //
                // Konsola, nie ILogger: ta klasa biegnie przed pelnym startem hosta
                // i jest wolana wprost z testu, ktory nie buduje kontenera.
                Console.Error.WriteLine($"odzyskiwanie projektu {id} nie powiodlo sie: {ex.Message}");
            }
        }
    }

    private void Odzyskaj(string id)
    {
        var store = paths.Store(id);
        var meta = store.Load();
        if (meta.ActiveJobId is not { } jobId) return;

        kolejka.ZarejestrujPrzerwane(jobId, id);

        // `CurrentVersion == 0` to przerwana WERSJA PIERWSZA — snapshotu numer 0 nie
        // ma i naiwne „przywroc biezaca" rzucaloby tu `DirectoryNotFoundException`,
        // czyli akurat na projektach, ktore najczesciej sa w locie na swiezym serwerze.
        // Uczciwy stan koncowy: projekt zostaje szkicem. Nic nie przepadlo, bo nic
        // jeszcze nie istnialo.
        if (meta.CurrentVersion > 0)
            new VersionStore(paths.Dir(id)).Restore(meta.CurrentVersion);

        store.Save(meta with { ActiveJobId = null });
    }
}
