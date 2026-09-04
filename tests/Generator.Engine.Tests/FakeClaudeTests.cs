using System.Diagnostics;
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
    [Theory]
    [InlineData("success", 0, true)]
    [InlineData("denied", 0, true)]
    [InlineData("crash", 1, false)]
    public void Scenariusz_zwraca_oczekiwany_exit_code_i_stdout(string scenario, int exit, bool hasStdout)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(FakeClaude.DllPath);
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add(scenario);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        Assert.Equal(exit, p.ExitCode);
        Assert.Equal(hasStdout, stdout.Length > 0);
    }
}
