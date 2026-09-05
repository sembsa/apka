namespace Generator.Engine.ClaudeCli;

/// IsFirstRun rozstrzyga --session-id vs --resume. Nigdy oba naraz.
///
/// <param name="SystemAppendix">
/// Dopisek do promptu systemowego. `null` = dopisek dla STRONY
/// (ClaudeRunner.PageAppendix) — domyslny, bo to zdecydowana wiekszosc wywolan.
/// Propozycje musza podac swoj wlasny: dopisek strony zada jednego index.html
/// i zachowywania data-cmt-id, a prompt propozycji zada trzech plikow a/b/c.html
/// i BRAKU data-cmt-id. Jadac razem, te dwa teksty mowily modelowi rzeczy
/// wykluczajace sie — a testy tego nie widza, bo omijaja ClaudeRunner.
/// </param>
public record ClaudeRunRequest(
    string WorkspaceDir,
    string Instruction,
    string? SessionId,
    bool IsFirstRun,
    string? SystemAppendix = null);
