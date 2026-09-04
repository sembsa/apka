using Generator.Engine.ClaudeCli;
using Xunit;

namespace Generator.Engine.Tests;

public class ClaudeResultParserTests
{
    // Skrocony, ale wierny kształt z pomiaru 2026-09-04 (przebieg udany).
    private const string SuccessJson = """
    {"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
    "session_id":"b0c0830f-81e8-4cd4-865a-5e7562278188","total_cost_usd":0.018619,
    "permission_denials":[],"result":"Utworzylem index.html",
    "usage":{"input_tokens":4,"output_tokens":239},
    "modelUsage":{"claude-opus-5[1m]":{},"claude-haiku-4-5-20251001":{}}}
    """;

    private const string DeniedJson = """
    {"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
    "session_id":"e00452ce-e4db-4a8e-84e6-83e38c926cdf","total_cost_usd":0.012750,
    "permission_denials":[{"tool_name":"Write","tool_use_id":"toolu_01BRkt",
    "tool_input":{"file_path":"/tmp/t1/index.html","content":"<h1>Test</h1>"}}],
    "result":"Nie mam uprawnien do zapisu tego pliku."}
    """;

    [Fact]
    public void Czyta_pola_ktore_decyduja_o_bramce()
    {
        var r = ClaudeResultParser.TryParse(SuccessJson);

        Assert.NotNull(r);
        Assert.False(r.IsError);
        Assert.Equal("success", r.Subtype);
        Assert.Equal("end_turn", r.StopReason);
        Assert.Equal("completed", r.TerminalReason);
        Assert.Equal("b0c0830f-81e8-4cd4-865a-5e7562278188", r.SessionId);
        Assert.Equal(0.018619m, r.TotalCostUsd);
        Assert.Empty(r.PermissionDenials);
    }

    [Fact]
    public void Czyta_odmowe_uprawnien_wraz_ze_sciezka()
    {
        var r = ClaudeResultParser.TryParse(DeniedJson);

        Assert.NotNull(r);
        var denial = Assert.Single(r.PermissionDenials);
        Assert.Equal("Write", denial.ToolName);
        Assert.Equal("/tmp/t1/index.html", denial.FilePath);
        // Kluczowe: JSON odmowy wyglada na sukces we wszystkich innych polach.
        Assert.False(r.IsError);
        Assert.Equal("success", r.Subtype);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Error: bypassPermissions not supported in restricted mode")]
    public void Pusty_lub_niebedacy_jsonem_stdout_daje_null(string? stdout)
    {
        Assert.Null(ClaudeResultParser.TryParse(stdout));
    }
}
