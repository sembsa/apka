using System.ComponentModel;
using System.Diagnostics;

namespace Generator.Engine.ClaudeCli;

public interface IClaudeRunner
{
    Task<ClaudeRunOutcome> RunAsync(ClaudeRunRequest request, CancellationToken ct = default);
}

/// executable + prefixArgs pozwalaja podstawic atrape w testach:
/// produkcja to ("claude", null), testy to ("dotnet", [dll, "--scenario", ...]).
public class ClaudeRunner(string executable, IReadOnlyList<string>? prefixArgs = null) : IClaudeRunner
{
    public const string Model = "claude-opus-5";
    public const string AllowedTools = "Read,Write,Edit,Glob,Grep";

    private static readonly string SystemPromptAppendix =
        "Generujesz prosta strone www: jeden plik index.html i jeden style.css, bez frameworkow. " +
        "Zachowuj atrybuty data-cmt-id na blokach tresci.";

    public static IReadOnlyList<string> BuildArguments(ClaudeRunRequest r, bool useBare)
    {
        var args = new List<string>
        {
            "-p", r.Instruction,
            "--output-format", "json",
            "--restricted",
            "--strict-mcp-config",
            "--tools", AllowedTools,
            "--permission-mode", "acceptEdits",
            "--permission-prompts", "none",
            "--model", Model,
            "--append-system-prompt", SystemPromptAppendix,
        };

        if (useBare) args.Add("--bare");

        if (!string.IsNullOrEmpty(r.SessionId))
        {
            args.Add(r.IsFirstRun ? "--session-id" : "--resume");
            args.Add(r.SessionId);
        }

        return args;
    }

    public async Task<ClaudeRunOutcome> RunAsync(ClaudeRunRequest request, CancellationToken ct = default)
    {
        // --bare tylko z kluczem: bez niego konczy sie exit 1 i pustym stdout.
        var useBare = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.WorkspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // wymagane, zeby moc zamknac stdin
            UseShellExecute = false,
        };

        foreach (var a in prefixArgs ?? []) psi.ArgumentList.Add(a);
        foreach (var a in BuildArguments(request, useBare)) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Brak pliku wykonywalnego to normalna sciezka (bramka z Taska 5 ja
            // rozpoznaje po ProcessStarted: false) — nie moze rzucic wyjatkiem.
            return new ClaudeRunOutcome(false, -1, string.Empty, ex.Message, sw.Elapsed);
        }

        if (p is null)
            return new ClaudeRunOutcome(false, -1, string.Empty, "Process.Start zwrocil null", sw.Elapsed);

        using (p)
        {
            // Natychmiast: inaczej claude -p czeka 3 s na dane wejsciowe.
            p.StandardInput.Close();

            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            finally
            {
                // using (p) samo w sobie NIE zabija procesu OS przy Dispose — tylko
                // zwalnia uchwyty .NET. Bez tego anulowanie (np. limit czasu w
                // kolejce z Taska 9) zostawia prawdziwego `claude` sierota: dalej
                // dziala, wydaje budzet i moze dopisac pliki do katalogu roboczego
                // juz po tym, jak orkiestrator uznal zadanie za zakonczone.
                if (!p.HasExited)
                {
                    // Kill nie moze rzucic dalej: wyjatek z finally ZASTAPILBY
                    // OperationCanceledException, na ktorym opiera sie kontrakt
                    // anulowania (patrz komentarz przy return nizej) — kolejka
                    // z Taska 9 zlapalaby cos innego niz oczekuje i zalogowala
                    // przekroczenie limitu czasu jako awarie. Od .NET Core 3.0
                    // Kill() na juz zakonczonym procesie jest udokumentowanym
                    // no-opem, wiec InvalidOperationException tu nie leci —
                    // jedyny realny wyjatek to Win32Exception (system odmowil
                    // zabicia procesu), lapany na wszelki wypadek.
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Win32Exception)
                    {
                    }
                }
            }
            sw.Stop();

            // Anulowanie propaguje sie jako OperationCanceledException (rzucony
            // wyzej przez WaitForExitAsync) zamiast wracac jako ClaudeRunOutcome:
            // to idiomatyczny kontrakt CancellationToken w .NET, a wywolujacy
            // (kolejka z Taska 9) i tak musi reagowac na przekroczenie wlasnego
            // limitu czasu, nie na surowy wynik podprocesu.
            return new ClaudeRunOutcome(true, p.ExitCode, await stdout, await stderr, sw.Elapsed);
        }
    }
}
