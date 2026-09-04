namespace Generator.Engine.ClaudeCli;

/// IsFirstRun rozstrzyga --session-id vs --resume. Nigdy oba naraz.
public record ClaudeRunRequest(
    string WorkspaceDir,
    string Instruction,
    string? SessionId,
    bool IsFirstRun);
