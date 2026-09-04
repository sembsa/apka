using Generator.Engine.ClaudeCli;

namespace Generator.Engine.Gates;

/// Kontrakt 4.3: frontend rozgalezia sie po handling, nie po przyczynie.
public enum FailureHandling { Retrying, Halted }

public enum GateVerdict { Ok, Retry, Halt }

public record GateResult(GateVerdict Verdict, string Cause, ClaudeResult? Parsed);

/// Piec warunkow z sekcji 5.1 planu. `claude -p` sygnalizuje sukces zbyt chetnie:
/// exit code, is_error, subtype i stop_reason wygladaja identycznie przy odmowie
/// uprawnien, ktora nie utworzyla ani jednego pliku (zmierzone 2026-09-04).
public static class RunGate
{
    /// GateVerdict.Ok nie ma odpowiednika w FailureHandling — wywolujacy nie
    /// powinien mapowac sukcesu na obsluge awarii. Rzucamy zamiast po cichu
    /// zwracac Halted, zeby blad wywolania nie przeszedl niezauwazony.
    public static FailureHandling ToHandling(GateVerdict v) => v switch
    {
        GateVerdict.Retry => FailureHandling.Retrying,
        GateVerdict.Halt => FailureHandling.Halted,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "GateVerdict.Ok nie mapuje sie na FailureHandling")
    };

    public static GateResult Evaluate(ClaudeRunOutcome outcome)
    {
        // 1. Proces musi wystartowac.
        if (!outcome.ProcessStarted)
            return new GateResult(GateVerdict.Halt, $"proces nie wystartowal: {outcome.StdErr}", null);

        var parsed = ClaudeResultParser.TryParse(outcome.Stdout);

        // 1b. Brak JSON-a na stdout — blad startu, komunikat jest na stderr.
        //     Zwykle przejsciowy (zasoby, start procesu), wiec Retry.
        if (parsed is null)
            return new GateResult(GateVerdict.Retry,
                $"brak JSON na stdout (exit {outcome.ExitCode}): {Trim(outcome.StdErr)}", null);

        // 4. Niepuste permission_denials — sprawdzane NAJPIERW spomiedzy warunkow
        //    liczonych na sparsowanym JSON-ie (zaraz po tym, ze w ogole mamy JSON).
        //    Gdy uprawnienia zostaly odrzucone, nasza konfiguracja jest zla — zadna
        //    powtorka tego nie naprawi, niezaleznie od tego, co mowia pozostale pola
        //    (exit code, is_error, stop_reason). To jedyny deterministyczny warunek
        //    bramki, wiec ma pierwszenstwo nad wszystkimi innymi — w tym nad exit
        //    code i is_error, ktore same w sobie moga byc przejsciowe.
        if (parsed.PermissionDenials.Count > 0)
        {
            var names = string.Join(", ", parsed.PermissionDenials.Select(d => d.ToolName));
            return new GateResult(GateVerdict.Halt,
                $"niepuste permission_denials: {names}", parsed);
        }

        // 2. Exit code i is_error / subtype.
        if (outcome.ExitCode != 0)
            return new GateResult(GateVerdict.Retry, $"exit code {outcome.ExitCode}", parsed);

        if (parsed.IsError || parsed.Subtype != "success")
            return new GateResult(GateVerdict.Retry,
                $"is_error={parsed.IsError}, subtype={parsed.Subtype}", parsed);

        // 3. stop_reason / terminal_reason.
        if (parsed.StopReason != "end_turn")
            return new GateResult(GateVerdict.Retry, $"stop_reason={parsed.StopReason}", parsed);

        if (parsed.TerminalReason != "completed")
            return new GateResult(GateVerdict.Retry, $"terminal_reason={parsed.TerminalReason}", parsed);

        // Warunek 5 (bramki wersji z 5.2) sprawdza GenerationService — potrzebuje plikow.
        return new GateResult(GateVerdict.Ok, string.Empty, parsed);
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}
