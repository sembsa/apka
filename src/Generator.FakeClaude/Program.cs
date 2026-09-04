// Atrapa `claude -p` dla testów. Nie waliduje flag poza --scenario;
// sprawdzanie flag jest zadaniem testów ClaudeRunner, nie atrapy.
var scenario = "success";
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--scenario") scenario = args[i + 1];

const string success = """
{"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
"num_turns":2,"session_id":"b0c0830f-81e8-4cd4-865a-5e7562278188","total_cost_usd":0.018619,
"permission_denials":[],"result":"Utworzylem index.html",
"modelUsage":{"claude-opus-5[1m]":{},"claude-haiku-4-5-20251001":{}}}
""";

// Realny kształt odmowy uprawnien: JSON wyglada na sukces, plik nie powstaje.
const string denied = """
{"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
"num_turns":2,"session_id":"e00452ce-e4db-4a8e-84e6-83e38c926cdf","total_cost_usd":0.012750,
"permission_denials":[{"tool_name":"Write","tool_use_id":"toolu_01BRkt","tool_input":{"file_path":"/tmp/t1/index.html","content":"<h1>Test</h1>"}}],
"result":"Nie mam uprawnien do zapisu tego pliku.",
"modelUsage":{"claude-opus-5[1m]":{},"claude-haiku-4-5-20251001":{}}}
""";

switch (scenario)
{
    case "success":
        Console.Out.Write(success);
        return 0;
    case "denied":
        Console.Out.Write(denied);
        return 0;
    case "crash":
        Console.Error.Write("Error: bypassPermissions not supported in restricted mode");
        return 1;   // stdout celowo pusty
    default:
        Console.Error.Write($"nieznany scenariusz: {scenario}");
        return 2;
}
