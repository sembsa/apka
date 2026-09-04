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

    // Rozne subtype niz "success", ale is_error nadal false — sam subtype ma
    // rozstrzygac o Retry, nie tylko is_error.
    private const string WrongSubtype = """
    {"is_error":false,"subtype":"error_max_turns","stop_reason":"end_turn","terminal_reason":"completed",
    "session_id":"s-5","total_cost_usd":0.02,"permission_denials":[],"result":"za duzo tur"}
    """;

    // Niepuste permission_denials RAZEM ze stop_reason innym niz end_turn —
    // rozstrzyga priorytet: denials (deterministyczne) bije stop_reason (przejsciowy).
    private const string DeniedAndTruncated = """
    {"is_error":false,"subtype":"success","stop_reason":"max_tokens","terminal_reason":"completed",
    "session_id":"s-6","total_cost_usd":0.03,
    "permission_denials":[{"tool_name":"Write","tool_input":{"file_path":"/tmp/index.html"}}],
    "result":"brak uprawnien i urwany stop_reason"}
    """;

    // Niepuste permission_denials RAZEM z is_error:true — rozstrzyga priorytet:
    // denials bije is_error/subtype, bo tylko denials sa deterministyczne.
    private const string DeniedAndError = """
    {"is_error":true,"subtype":"error","stop_reason":"end_turn","terminal_reason":"completed",
    "session_id":"s-7","total_cost_usd":0.04,
    "permission_denials":[{"tool_name":"Write","tool_input":{"file_path":"/tmp/index.html"}}],
    "result":"brak uprawnien i is_error"}
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
        Assert.Equal(0.05m, g.Parsed!.TotalCostUsd);
    }

    [Fact]
    public void Subtype_inny_niz_success_to_Retry()
    {
        var g = RunGate.Evaluate(Outcome(WrongSubtype));
        Assert.Equal(GateVerdict.Retry, g.Verdict);
        Assert.Contains("error_max_turns", g.Cause);
        Assert.Equal(0.02m, g.Parsed!.TotalCostUsd);
    }

    [Fact]
    public void Permission_denials_ma_priorytet_nad_stop_reason()
    {
        var g = RunGate.Evaluate(Outcome(DeniedAndTruncated));
        Assert.Equal(GateVerdict.Halt, g.Verdict);
        Assert.Contains("permission_denials", g.Cause);
        Assert.DoesNotContain("stop_reason", g.Cause);
        Assert.Equal(0.03m, g.Parsed!.TotalCostUsd);
    }

    [Fact]
    public void Permission_denials_ma_priorytet_nad_is_error()
    {
        var g = RunGate.Evaluate(Outcome(DeniedAndError));
        Assert.Equal(GateVerdict.Halt, g.Verdict);
        Assert.Contains("permission_denials", g.Cause);
        Assert.DoesNotContain("is_error", g.Cause);
        Assert.Equal(0.04m, g.Parsed!.TotalCostUsd);
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
        Assert.Equal(0.018m, g.Parsed!.TotalCostUsd);
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
