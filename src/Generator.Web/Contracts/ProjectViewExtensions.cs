namespace Generator.Web.Contracts;

/// <summary>
/// Jedno miejsce, w ktorym „aktualna wersja" jest liczona. Powstalo razem z powrotem do
/// wersji: gdy `CurrentVersion` moze wskazywac na starsza niz ostatnia, kazde
/// `Versions[^1]` rozsiane po komponentach jest cichym bledem.
/// </summary>
public static class ProjectViewExtensions
{
    /// Wersja, ktora klient oglada. `null`, gdy nie ma jeszcze zadnej.
    public static VersionView? Aktualna(this ProjectView project) =>
        project.Versions.FirstOrDefault(v => v.Number == project.CurrentVersion);

    /// Numer aktualnej wersji, albo 1 gdy jeszcze zadnej nie ma (pierwsza runda).
    public static int NumerAktualnej(this ProjectView project) =>
        project.CurrentVersion > 0 ? project.CurrentVersion : 1;

    /// <summary>
    /// Wersja, Z KTOREJ powstala podana — po `BasedOn`, nigdy po `Number - 1`.
    /// Do tego porownujemy zmiany i z tej wersji bierzemy raport „co zrobilismy".
    /// </summary>
    public static VersionView? Rodzic(this ProjectView project, VersionView? wersja) =>
        wersja?.BasedOn is { } rodzic
            ? project.Versions.FirstOrDefault(v => v.Number == rodzic)
            : null;
}
