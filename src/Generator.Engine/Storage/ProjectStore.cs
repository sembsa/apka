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

    public ProjectMeta Load() =>
        JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(MetaPath), Options)
        ?? throw new InvalidDataException($"project.json nie da sie odczytac: {MetaPath}");

    public void Save(ProjectMeta meta) =>
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta, Options));

    /// Runda klienta. Powtorki po awarii jej NIE zuzywaja (kontrakt 4.1).
    public static ProjectMeta WithRoundConsumed(ProjectMeta m) =>
        m with { RoundsUsed = m.RoundsUsed + 1 };

    /// Budzet rosnie od kazdego przebiegu, w tym od powtorek.
    public static ProjectMeta WithSpend(ProjectMeta m, decimal usd) =>
        m with { SpentUsd = m.SpentUsd + usd };
}
