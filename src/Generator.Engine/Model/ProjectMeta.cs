using System.Text.Json.Serialization;

namespace Generator.Engine.Model;

public enum SourceKind { Url, Idea }
public enum ProjectStatus { Draft, Active, Frozen }

/// <param name="BasedOn">
/// Numer wersji, Z KTOREJ ta powstala (null dla pierwszej). NIE `Number - 1`:
/// po powrocie do v1 kolejna runda tworzy v3, ktorej rodzicem jest v1, nie v2.
/// Kontrakt 5.1.
/// </param>
public record VersionMeta(
    int Number,
    string SessionId,
    string SnapshotDir,
    decimal CostUsd,
    IReadOnlyList<string> OrphanedAnchors,
    int? BasedOn = null,
    IReadOnlyList<string>? ChangedAnchors = null);

/// <param name="CurrentVersion">
/// Wersja, ktora klient oglada i do ktorej trafiaja nowe uwagi. 0 = brak wersji.
/// Kontrakt 5.1: po powrocie „aktualna" przestaje znaczyc „ostatnia", wiec
/// `Versions[^1]` jest bledem w kazdym miejscu, ktore pyta o biezaca wersje.
/// </param>
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
    string? ChosenProposal = null)
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
