using System.Text.Json.Serialization;

namespace Generator.Engine.Model;

public enum SourceKind { Url, Idea }
public enum ProjectStatus { Draft, Active, Frozen }

/// <param name="BasedOn">
/// Numer wersji, Z KTOREJ ta powstala (null dla pierwszej). NIE `Number - 1`:
/// po powrocie do v1 kolejna runda tworzy v3, ktorej rodzicem jest v1, nie v2.
/// Kontrakt 5.1.
/// </param>
/// <param name="CreatedAt">
/// Kontrakt 6.2: „Wersja 2 — wczoraj 14:30" rozpoznaje sie bez wysilku, samo
/// „Wersja 2" zmusza klienta do pamietania, co robil. Znaczy to przy powrocie do
/// starszej wersji (5.1).
///
/// Nullowalne, bo `project.json` zapisany przed ta zmiana daty nie ma. Wtedy `null`
/// — a nie data pliku snapshotu: mtime katalogu zmienia go kopiowanie, archiwizacja
/// czy synchronizacja, wiec byla by to data wygladajaca na prawdziwa i czasem falszywa.
/// „Nie wiem" widac w UI i da sie zignorowac; zmyslona data nie.
/// </param>
public record VersionMeta(
    int Number,
    string SessionId,
    string SnapshotDir,
    decimal CostUsd,
    IReadOnlyList<string> OrphanedAnchors,
    int? BasedOn = null,
    IReadOnlyList<string>? ChangedAnchors = null,
    DateTimeOffset? CreatedAt = null);

/// <param name="ActiveJob">
/// Zadanie, ktore JEST W LOCIE dla tego projektu — zapisywane na dysk ZANIM klient
/// dostanie identyfikator, i czyszczone po zakonczeniu.
///
/// Kolejka zyje w pamieci procesu, wiec restart gubi zadanie. Bez tego pola
/// `GET /api/jobs/{id}` po restarcie oddaje 404, czyli „nie znam takiego zadania"
/// komus, kto wlasnie za nie zaplacil. Ten jeden zapis pozwala po starcie
/// odpowiedziec prawde: runda zostala przerwana (patrz `OdzyskiwaniePoStarcie`).
///
/// Zadanie NIE jest wznawiane. Proces `claude` zginal razem z aplikacja, wiec
/// wznowienie znaczyloby uruchomienie rundy DRUGI RAZ — drugi raz zaplacone,
/// przy ryzyku zdublowanej wersji.
/// </param>
/// <param name="CurrentVersion">
/// Wersja, ktora klient oglada i do ktorej trafiaja nowe uwagi. 0 = brak wersji.
/// Kontrakt 5.1: po powrocie „aktualna" przestaje znaczyc „ostatnia", wiec
/// `Versions[^1]` jest bledem w kazdym miejscu, ktore pyta o biezaca wersje.
/// </param>
/// <summary>
/// Zadanie w locie, zapamietane na dysku (patrz `ProjectMeta.ActiveJob`).
/// </summary>
/// <param name="VersionsBefore">
/// Ile wersji mial projekt, ZANIM to zadanie ruszylo. Bez tej liczby odzyskiwanie
/// po restarcie nie odroznia rundy PRZERWANEJ od rundy, ktora sie UDALA i zginela
/// dopiero przy czyszczeniu znacznika — i oglaszaloby „przerwana" o rundzie, ktorej
/// wynik klient ma w historii. To ta sama klasa klamstwa co dlug 3.
/// </param>
public record ActiveJob(string JobId, int VersionsBefore);

public record ProjectMeta(
    string Id,
    string Token,
    SourceKind Source,
    string WorkspaceDir,
    ProjectStatus Status,
    int RoundsUsed,
    int RoundsLimit,
    decimal SpentUsd,
    decimal BudgetUsd,
    IReadOnlyList<VersionMeta> Versions,
    int CurrentVersion = 0,
    string Description = "",
    string? SourceUrl = null,
    string? ChosenProposal = null,
    ActiveJob? ActiveJob = null)
{
    /// Jedyna droga do „wersji biezacej". Uzywaj tego zamiast Versions[^1].
    ///
    /// [JsonIgnore] jest OBOWIAZKOWE: bez niego JsonSerializer zapisuje ta
    /// wlasciwosc do project.json (jest publiczna i bezargumentowa), wiec plik
    /// puchnie o pelna kopie rekordu wersji przy kazdym zapisie, a diagnozujacy
    /// zly rollback czyta blok "Current" pochodzacy z innego momentu niz
    /// "CurrentVersion" i wyciaga bledny wniosek. Zmierzone, nie teoretyczne.
    [JsonIgnore]
    public VersionMeta? Current =>
        Versions.FirstOrDefault(v => v.Number == CurrentVersion);

    /// Kontrakt 5.1: `max(number) + 1`, nie `liczba wersji + 1` — po powrocie
    /// te dwie liczby sie rozjezdzaja i doszloby do kolizji numerow.
    [JsonIgnore]
    public int NextVersionNumber =>
        Versions.Count == 0 ? 1 : Versions.Max(v => v.Number) + 1;
}
