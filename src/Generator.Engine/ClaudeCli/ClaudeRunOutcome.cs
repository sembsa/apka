namespace Generator.Engine.ClaudeCli;

/// Surowy wynik podprocesu — bez interpretacji. Ocena nalezy do bramki (Task 5).
public record ClaudeRunOutcome(
    bool ProcessStarted,
    int ExitCode,
    string Stdout,
    string StdErr,
    TimeSpan Elapsed);
