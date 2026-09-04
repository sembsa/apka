namespace Generator.Engine.Model;

public enum SourceKind { Url, Idea }
public enum ProjectStatus { Draft, Active, Frozen }

public record VersionMeta(
    int Number,
    string SessionId,
    string SnapshotDir,
    decimal CostUsd,
    IReadOnlyList<string> OrphanedAnchors);

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
    IReadOnlyList<VersionMeta> Versions);
