using Generator.Engine.IO;
using Generator.Engine.Model;

namespace Generator.Engine.Versioning;

public class VersionStore(string projectDir)
{
    public string WorkDir => Path.Combine(projectDir, "work");

    public string SnapshotPath(int number) =>
        Path.Combine(projectDir, "versions", number.ToString("D3"));

    public VersionMeta Commit(int number, string sessionId, decimal costUsd,
        IReadOnlyList<string> orphanedAnchors, int? basedOn,
        IReadOnlyList<string> changedAnchors)
    {
        var target = SnapshotPath(number);
        DirectoryOps.Delete(target);
        DirectoryOps.Copy(WorkDir, target);

        // Data stemplowana TUTAJ, nie przekazywana parametrem: chwila powstania wersji
        // to chwila, w ktorej snapshot trafil na dysk, i nikt z zewnatrz nie ma powodu
        // jej podawac. Dzieki temu Commit zachowuje szesc parametrow, a wszystkie
        // istniejace wywolania (z atrapami w testach Web wlacznie) zostaja bez zmian.
        return new VersionMeta(number, sessionId, target, costUsd, orphanedAnchors,
            basedOn, changedAnchors, DateTimeOffset.UtcNow);
    }

    /// Droga powrotna ze snapshotu (dotad VersionStore byl tylko-do-zapisu). Kopiuje
    /// snapshot wersji "number" na WorkDir, calkowicie go zastepujac. Zamyka trzy
    /// rzeczy naraz (patrz raport, punkt 3): powrot do wersji z decyzji 5
    /// planu/kontraktu §5.1 (Plan B), naprawe zepsutego WorkDir po nieudanej rundzie
    /// (GenerationService.Failed) i wyjscie awaryjne przy naprawie kotwic.
    ///
    /// Uzywa DirectoryOps.SafeReplace, NIE naiwnego "skasuj-potem-kopiuj" jak Commit
    /// powyzej — tu ofiara nieudanej kopii w polowie byloby WorkDir, jedyna mutowalna
    /// prawda systemu, a nie odtwarzalny snapshot.
    public void Restore(int number)
    {
        var source = SnapshotPath(number);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(
                $"Nie mozna przywrocic wersji {number}: brak snapshotu w {source}");

        DirectoryOps.SafeReplace(source, WorkDir);
    }
}
