using System.Diagnostics;
using Generator.Engine.ClaudeCli;
using Xunit;

namespace Generator.Engine.Tests;

public class ClaudeRunnerTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("gen-run-").FullName;
    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static ClaudeRunner ForScenario(string scenario) =>
        new("dotnet", [FakeClaude.DllPath, "--scenario", scenario]);

    private ClaudeRunRequest Request(string? sessionId = null, bool first = true) =>
        new(_work, "Utworz prosta strone", sessionId, first);

    [Fact]
    public async Task Konczy_sie_szybciej_niz_3s_bo_stdin_jest_zamykany()
    {
        // Scenariusz "stdin-wait" (nie "success"!): atrapa robi Console.In.ReadToEnd()
        // PRZED odpowiedzia, wiec realnie czeka, az rodzic zamknie swoj koniec potoku.
        // "success" nigdy nie czyta stdin, wiec ten sam test przechodzilby identycznie
        // nawet gdyby ClaudeRunner w ogole nie wywolywal StandardInput.Close() —
        // sprawdzone empirycznie (raport Task 4, fix round 1): z "success" test padal
        // w 56 ms bez wywolania Close() w ogole. Nie "upraszczaj" z powrotem do "success".
        //
        // CancellationTokenSource to siatka bezpieczenstwa: gdyby Close() faktycznie
        // zniknal z ClaudeRunner, atrapa czekalaby na stdin w nieskonczonosc. Bez limitu
        // caly przebieg `dotnet test` zawiesilby sie zamiast dac czytelna porazke.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        var outcome = await ForScenario("stdin-wait").RunAsync(Request(), cts.Token);
        sw.Stop();

        Assert.True(outcome.ProcessStarted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"przebieg zajal {sw.Elapsed} — stdin nie zostal zamkniety?");
        Assert.True(outcome.Elapsed > TimeSpan.Zero);
        Assert.Contains("\"subtype\":\"success\"", outcome.Stdout);
    }

    [Fact]
    public async Task Przekazuje_stdout_i_exit_code_bez_interpretacji()
    {
        var ok = await ForScenario("success").RunAsync(Request());
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("\"subtype\":\"success\"", ok.Stdout);

        var crash = await ForScenario("crash").RunAsync(Request());
        Assert.Equal(1, crash.ExitCode);
        Assert.Equal(string.Empty, crash.Stdout);
        Assert.Contains("bypassPermissions", crash.StdErr);
    }

    [Fact]
    public async Task Brakujacy_plik_wykonywalny_nie_rzuca_wyjatkiem()
    {
        var runner = new ClaudeRunner("nie-ma-takiego-programu-xyz");
        var outcome = await runner.RunAsync(Request());

        Assert.False(outcome.ProcessStarted);
        Assert.NotEqual(0, outcome.ExitCode);
    }

    [Fact]
    public void Pierwszy_przebieg_dostaje_session_id_kolejny_resume_nigdy_oba()
    {
        var first = ClaudeRunner.BuildArguments(
            new ClaudeRunRequest("/w", "brief", "11111111-1111-1111-1111-111111111111", IsFirstRun: true), useBare: false);
        Assert.Contains("--session-id", first);
        Assert.DoesNotContain("--resume", first);

        var next = ClaudeRunner.BuildArguments(
            new ClaudeRunRequest("/w", "runda", "11111111-1111-1111-1111-111111111111", IsFirstRun: false), useBare: false);
        Assert.Contains("--resume", next);
        Assert.DoesNotContain("--session-id", next);
    }

    [Fact]
    public void Argumenty_zawieraja_wszystkie_flagi_sandboxa()
    {
        var args = ClaudeRunner.BuildArguments(
            new ClaudeRunRequest("/w", "brief", null, IsFirstRun: true), useBare: false);

        Assert.Contains("-p", args);
        Assert.Contains("--restricted", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--permission-mode", args);
        Assert.Contains("acceptEdits", args);
        Assert.Contains("--permission-prompts", args);
        Assert.Contains("none", args);
        Assert.Contains("Read,Write,Edit,Glob,Grep", args);
        Assert.Contains("claude-opus-5", args);
        Assert.DoesNotContain("--bare", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);

        var prod = ClaudeRunner.BuildArguments(
            new ClaudeRunRequest("/w", "brief", null, IsFirstRun: true), useBare: true);
        Assert.Contains("--bare", prod);
    }
}
