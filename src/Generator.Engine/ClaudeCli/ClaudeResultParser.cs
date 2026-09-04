using System.Text.Json;

namespace Generator.Engine.ClaudeCli;

public static class ClaudeResultParser
{
    /// Zwraca null, gdy stdout jest pusty albo nie jest JSON-em — to normalny
    /// przypadek przy bledzie startu procesu (komunikat idzie na stderr).
    public static ClaudeResult? TryParse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            return new ClaudeResult(
                SessionId: Str(root, "session_id"),
                IsError: root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
                Subtype: Str(root, "subtype"),
                StopReason: Str(root, "stop_reason"),
                TerminalReason: Str(root, "terminal_reason"),
                TotalCostUsd: TotalCost(root),
                PermissionDenials: Denials(root),
                ResultText: Str(root, "result"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static decimal TotalCost(JsonElement root)
    {
        if (!root.TryGetProperty("total_cost_usd", out var c) || c.ValueKind != JsonValueKind.Number)
            return 0m;

        return c.TryGetDecimal(out var d) ? d : 0m;
    }

    private static List<PermissionDenial> Denials(JsonElement root)
    {
        var list = new List<PermissionDenial>();
        if (!root.TryGetProperty("permission_denials", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            // Pomiń element, jeśli nie jest obiektem
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string? path = null;
            if (item.TryGetProperty("tool_input", out var input) &&
                input.ValueKind == JsonValueKind.Object &&
                input.TryGetProperty("file_path", out var fp) &&
                fp.ValueKind == JsonValueKind.String)
                path = fp.GetString();

            list.Add(new PermissionDenial(Str(item, "tool_name"), path));
        }
        return list;
    }
}
