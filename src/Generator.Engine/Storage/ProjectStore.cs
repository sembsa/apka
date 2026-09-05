using System.Text.Json;
using System.Text.Json.Serialization;
using Generator.Engine.Model;

namespace Generator.Engine.Storage;

/// Metadane w project.json obok snapshotow. Bez bazy: v1 nie ma kont,
/// a sekcja 7 planu gwarantuje jedno zadanie na projekt jednoczesnie.
public class ProjectStore(string projectDir)
{
    public const int DefaultRoundsLimit = 15;      // kontrakt 4.2
    public const decimal DefaultBudgetUsd = 8.00m; // kontrakt 4.2

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string MetaPath => Path.Combine(projectDir, "project.json");

    public ProjectMeta Create(SourceKind source)
    {
        Directory.CreateDirectory(projectDir);
        var meta = new ProjectMeta(
            Id: Guid.NewGuid().ToString(),
            Token: Guid.NewGuid().ToString("N"),
            Source: source,
            WorkspaceDir: Path.Combine(projectDir, "work"),
            Status: ProjectStatus.Draft,
            RoundsUsed: 0,
            RoundsLimit: DefaultRoundsLimit,
            SpentUsd: 0m,
            BudgetUsd: DefaultBudgetUsd,
            Versions: []);
        Directory.CreateDirectory(meta.WorkspaceDir);
        Save(meta);
        return meta;
    }

    public ProjectMeta Load()
    {
        try
        {
            var json = File.ReadAllText(MetaPath);
            var meta = JsonSerializer.Deserialize<ProjectMeta>(json, Options)
                ?? throw new InvalidDataException($"project.json deserializuje sie na null w: {MetaPath}");
            return Migruj(meta);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"Nie mozna czytac project.json w: {MetaPath}", ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"project.json jest uszkodzony lub niepelny w: {MetaPath}", ex);
        }
    }

    /// project.json zapisany przed kontraktem 5.1 nie ma `CurrentVersion`, wiec
    /// deserializuje sie na 0 — czyli „brak wersji" na projekcie, ktory je ma.
    /// Podnosimy do najnowszej TYLKO gdy pole jest zerowe, a wersje sa: swiadomy
    /// powrot zapisuje liczbe >= 1 i nie wolno go nadpisac.
    private static ProjectMeta Migruj(ProjectMeta meta)
    {
        if (meta.Versions.Count == 0) return meta;

        // Dwa przypadki, jedna naprawa. Drugi jest nowy i grozny: po tej zmianie
        // `Current is null` znaczy ALBO „nie ma zadnej wersji", ALBO „CurrentVersion
        // wskazuje numer spoza listy" (reczna edycja, uszkodzony zapis). Gdyby ten
        // drugi przypadek dozyl do RestoreWorkDirAfterFailure, nieudana runda
        // weszlaby w galaz „brak wersji" i SKASOWALA WorkDir do pusta na projekcie,
        // ktory ma trzy wersje i wydany budzet klienta. Naprawiamy tutaj, przy
        // wejsciu, zeby niezmiennik „sa wersje => Current != null" znowu byl prawda
        // w calym silniku.
        var poprawny = meta.Versions.Any(v => v.Number == meta.CurrentVersion);
        return poprawny
            ? meta
            : meta with { CurrentVersion = meta.Versions.Max(v => v.Number) };
    }

    public void Save(ProjectMeta meta)
    {
        var json = JsonSerializer.Serialize(meta, Options);
        var tmpPath = MetaPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, MetaPath, overwrite: true);
    }

    /// Runda klienta. Powtorki po awarii jej NIE zuzywaja (kontrakt 4.1).
    public static ProjectMeta WithRoundConsumed(ProjectMeta m) =>
        m with { RoundsUsed = m.RoundsUsed + 1 };

    /// Budzet rosnie od kazdego przebiegu, w tym od powtorek.
    public static ProjectMeta WithSpend(ProjectMeta m, decimal usd) =>
        m with { SpentUsd = m.SpentUsd + usd };
}
