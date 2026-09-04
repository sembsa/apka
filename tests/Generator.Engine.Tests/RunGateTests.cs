using Generator.Engine.ClaudeCli;
using Generator.Engine.Gates;
using Xunit;

namespace Generator.Engine.Tests;

public class RunGateTests
{
    private static ClaudeRunOutcome Outcome(string stdout, int exit = 0, string stderr = "") =>
        new(ProcessStarted: true, ExitCode: exit, Stdout: stdout, StdErr: stderr, Elapsed: TimeSpan.FromSeconds(8));

    private const string Ok = """
    {"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
    "session_id":"s-1","total_cost_usd":0.018,"permission_denials":[],"result":"gotowe"}
    """;

    // Ten JSON wyglada na sukces w kazdym polu poza permission_denials.
    private const string Denied = """
    {"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
    "session_id":"s-2","total_cost_usd":0.0127,
    "permission_denials":[{"tool_name":"Write","tool_input":{"file_path":"/tmp/index.html"}}],
    "result":"brak uprawnien"}
    """;

    private const string Truncated = """
    {"is_error":false,"subtype":"success","stop_reason":"max_tokens","terminal_reason":"completed",
    "session_id":"s-3","total_cost_usd":0.9,"permission_denials":[],"result":"..."}
    """;

    private const string Aborted = """
    {"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"aborted",
    "session_id":"s-4","total_cost_usd":0.05,"permission_denials":[],"result":"przerwano"}
    """;

    [Fact]
    public void Udany_przebieg_przechodzi()
    {
        var g = RunGate.Evaluate(Outcome(Ok));
        Assert.Equal(GateVerdict.Ok, g.Verdict);
        Assert.Equal("s-1", g.Parsed!.SessionId);
    }

    [Fact]
    public void Niepuste_permission_denials_to_Halt_mimo_ze_json_mowi_success()
    {
        var g = RunGate.Evaluate(Outcome(Denied));
        Assert.Equal(GateVerdict.Halt, g.Verdict);
        Assert.Contains("permission_denials", g.Cause);
        Assert.Contains("Write", g.Cause);
        // Kolejka potrzebuje kosztu nawet z nieudanego przebiegu — pieniadze zostaly wydane.
        Assert.Equal(0.0127m, g.Parsed!.TotalCostUsd);
    }

    [Fact]
    public void Pusty_stdout_i_exit_1_to_Retry()
    {
        var g = RunGate.Evaluate(Outcome("", exit: 1, stderr: "Error: brak klucza"));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
        Assert.Null(g.Parsed);
    }

    [Fact]
    public void Nieuruchomiony_proces_to_Halt()
    {
        var g = RunGate.Evaluate(new ClaudeRunOutcome(false, -1, "", "nie ma programu", TimeSpan.Zero));
        Assert.Equal(GateVerdict.Halt, g.Verdict);
    }

    [Fact]
    public void Stop_reason_inny_niz_end_turn_to_Retry()
    {
        var g = RunGate.Evaluate(Outcome(Truncated));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
        Assert.Contains("max_tokens", g.Cause);
        Assert.Equal(0.9m, g.Parsed!.TotalCostUsd);
    }

    [Fact]
    public void Terminal_reason_inny_niz_completed_to_Retry()
    {
        var g = RunGate.Evaluate(Outcome(Aborted));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
        Assert.Contains("aborted", g.Cause);
    }

    [Fact]
    public void Exit_niezerowy_z_poprawnym_JSON_niesie_Parsed()
    {
        var g = RunGate.Evaluate(Outcome(Ok, exit: 1));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
        Assert.NotNull(g.Parsed);
        Assert.Equal("s-1", g.Parsed!.SessionId);
    }

    [Fact]
    public void Stdout_niebedacy_JSON_i_exit_0_to_Retry()
    {
        var g = RunGate.Evaluate(Outcome("to nie jest json", exit: 0));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
        Assert.Null(g.Parsed);
    }

    [Fact]
    public void Is_error_true_to_Retry()
    {
        var json = Ok.Replace("\"is_error\":false", "\"is_error\":true");
        var g = RunGate.Evaluate(Outcome(json));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
    }

    [Fact]
    public void Verdict_mapuje_sie_na_handling_z_kontraktu()
    {
        Assert.Equal(FailureHandling.Retrying, RunGate.ToHandling(GateVerdict.Retry));
        Assert.Equal(FailureHandling.Halted, RunGate.ToHandling(GateVerdict.Halt));
    }

    // Odstepstwo od briefu: Ok nie ma odpowiednika w FailureHandling (ktory ma
    // tylko Retrying/Halted), wiec wolimy rzucic niz po cichu zwrocic Halted
    // dla poprawnego wyniku — patrz raport Taska 5.
    [Fact]
    public void Ok_nie_mapuje_sie_na_handling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RunGate.ToHandling(GateVerdict.Ok));
    }
}
