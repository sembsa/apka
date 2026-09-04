namespace Generator.Engine.ClaudeCli;

public record PermissionDenial(string ToolName, string? FilePath);

/// Podzbior JSON-a `claude -p --output-format json`, ktory nas obchodzi.
/// Nieznane pola sa ignorowane — CLI dorzuca ich duzo (usage, modelUsage, iterations).
public record ClaudeResult(
    string SessionId,
    bool IsError,
    string Subtype,
    string StopReason,
    string TerminalReason,
    decimal TotalCostUsd,
    IReadOnlyList<PermissionDenial> PermissionDenials,
    string ResultText);
