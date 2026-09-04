using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Generator.Engine.Tests;

public static class FakeClaude
{
    // Atrapa jest osobnym projektem; testy odpalaja ja przez `dotnet <dll>`,
    // co dziala identycznie na macOS i na Windows (Przemek) bez skryptow powloki.
    public static string DllPath =>
        Path.Combine(AppContext.BaseDirectory, "FakeClaude", "Generator.FakeClaude.dll");
}

public class FakeClaudeTests
{
    [Fact]
    public void Scenariusz_success_zwraca_prawidlowy_output()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(FakeClaude.DllPath);
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add("success");

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        Assert.Equal(0, p.ExitCode);
        Assert.NotEmpty(stdout);
        
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        
        Assert.Equal("success", root.GetProperty("subtype").GetString());
        var denials = root.GetProperty("permission_denials");
        Assert.Equal(0, denials.GetArrayLength());
    }

    [Fact]
    public void Scenariusz_denied_zwraca_prawidlowy_output()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(FakeClaude.DllPath);
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add("denied");

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        Assert.Equal(0, p.ExitCode);
        Assert.NotEmpty(stdout);
        
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        
        var denials = root.GetProperty("permission_denials");
        Assert.Equal(1, denials.GetArrayLength());
        
        var denial = denials[0];
        Assert.Equal("Write", denial.GetProperty("tool_name").GetString());
    }

    [Fact]
    public void Scenariusz_crash_zwraca_prawidlowy_output()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(FakeClaude.DllPath);
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add("crash");

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        Assert.Equal(1, p.ExitCode);
        Assert.Empty(stdout);
        Assert.NotEmpty(stderr);
        Assert.Contains("bypassPermissions", stderr);
    }
}
