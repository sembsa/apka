using System.Diagnostics;
using System.Text.Json;
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

    [Fact]
    public void Propozycje_dostaja_wlasny_dopisek_systemowy_a_nie_dopisek_strony()
    {
        // Ta warstwa jest jedynym miejscem, gdzie taki blad da sie zlapac testem:
        // GenerationServiceTests uzywaja ScriptedRunner, ktory OMIJA ClaudeRunner,
        // wiec sprzecznosc dopiskow byla widoczna dopiero przy prawdziwym claude -p.
        var strona = ClaudeRunner.BuildArguments(
            new ClaudeRunRequest("/w", "runda", null, IsFirstRun: true), useBare: false);
        var propozycje = ClaudeRunner.BuildArguments(
            new ClaudeRunRequest("/w", "propozycje", null, IsFirstRun: true,
                SystemAppendix: ClaudeRunner.ProposalsAppendix), useBare: false);

        // Domyslnie (null) leci dopisek strony — tak jak przed ta zmiana.
        Assert.Contains(ClaudeRunner.PageAppendix, strona);
        Assert.DoesNotContain(ClaudeRunner.ProposalsAppendix, strona);

        Assert.Contains(ClaudeRunner.ProposalsAppendix, propozycje);
        Assert.DoesNotContain(ClaudeRunner.PageAppendix, propozycje);
    }

    [Fact]
    public void Dopiski_nie_moga_sobie_zaprzeczac_w_dwoch_konkretnych_punktach()
    {
        // Nie "teksty sa rozne" (to przeszloby po dowolnej literowce), tylko dwie
        // sprzecznosci, ktore realnie wystapily: liczba plikow i data-cmt-id.
        Assert.Contains("index.html", ClaudeRunner.PageAppendix);
        Assert.DoesNotContain("index.html", ClaudeRunner.ProposalsAppendix);

        Assert.Contains("Zachowuj atrybuty data-cmt-id", ClaudeRunner.PageAppendix);
        Assert.Contains("Nie dodawaj atrybutow data-cmt-id", ClaudeRunner.ProposalsAppendix);
    }

    // --- Fix round 2/5: end-to-end na tym, co FAKTYCZNIE dostaje proces ---
    //
    // Powyzszy "Argumenty_zawieraja_wszystkie_flagi_sandboxa" sprawdza tylko liste
    // zwrocona przez czysta funkcje BuildArguments — nie sprawdza par flaga-wartosc
    // (podmiana "acceptEdits" <-> "none" miedzy --permission-mode a --permission-prompts
    // przeszlaby ten test) i nie sprawdza wcale "--output-format"/"json" ani odczytu
    // ANTHROPIC_API_KEY w RunAsync (odwrocenie "!" tez by przeszlo). Testy nizej
    // uruchamiaja RunAsync przeciwko scenariuszowi "echo-args", ktory odsyla WLASNE
    // argv procesu jako JSON — sprawdzamy to, co faktycznie trafilo do podprocesu,
    // nie to, co ClaudeRunner deklaruje, ze wyslal.

    private static string[] ParseEchoedArgs(string stdout) =>
        JsonSerializer.Deserialize<string[]>(stdout) ?? [];

    private static int IndexOfFlag(IReadOnlyList<string> argv, string flag)
    {
        for (var i = 0; i < argv.Count; i++)
            if (argv[i] == flag) return i;
        return -1;
    }

    private static void AssertFlagValue(IReadOnlyList<string> argv, string flag, string expectedValue)
    {
        var i = IndexOfFlag(argv, flag);
        Assert.True(i >= 0, $"brak flagi {flag} w argv: {string.Join(' ', argv)}");
        Assert.True(i + 1 < argv.Count, $"flaga {flag} bez wartosci w argv: {string.Join(' ', argv)}");
        Assert.Equal(expectedValue, argv[i + 1]);
    }

    [Fact]
    public async Task Flagi_trafiaja_do_procesu_z_poprawnymi_parami_wartosci()
    {
        var runner = new ClaudeRunner("dotnet", [FakeClaude.DllPath, "--scenario", "echo-args"]);
        var outcome = await runner.RunAsync(Request());

        Assert.True(outcome.ProcessStarted);
        Assert.Equal(0, outcome.ExitCode);
        var argv = ParseEchoedArgs(outcome.Stdout);

        AssertFlagValue(argv, "--output-format", "json");
        AssertFlagValue(argv, "--tools", "Read,Write,Edit,Glob,Grep");
        AssertFlagValue(argv, "--permission-mode", "acceptEdits");
        AssertFlagValue(argv, "--permission-prompts", "none");
        AssertFlagValue(argv, "--model", "claude-opus-5");

        Assert.Contains("-p", argv);
        Assert.Contains("--restricted", argv);
        Assert.Contains("--strict-mcp-config", argv);
        Assert.DoesNotContain("--dangerously-skip-permissions", argv);
    }

    // Te dwa testy mutuja globalna zmienna srodowiskowa procesu (ANTHROPIC_API_KEY).
    // Bezpieczne bez osobnej kolekcji xUnit: xUnit domyslnie NIE zrownolegla testow
    // WEWNATRZ jednej klasy (tylko rozne klasy/kolekcje biegna rownolegle wzgledem
    // siebie), a zadna inna klasa testowa w tym projekcie nie czyta/ustawia tej
    // zmiennej. Wartosc poprzednia przywracana w finally niezaleznie od wyniku testu.

    [Fact]
    public async Task Bare_dodawany_gdy_klucz_api_jest_ustawiony()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-dummy");
            var runner = new ClaudeRunner("dotnet", [FakeClaude.DllPath, "--scenario", "echo-args"]);
            var outcome = await runner.RunAsync(Request());
            var argv = ParseEchoedArgs(outcome.Stdout);

            Assert.Contains("--bare", argv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }

    [Fact]
    public async Task Bare_pomijany_gdy_klucz_api_nie_jest_ustawiony()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var runner = new ClaudeRunner("dotnet", [FakeClaude.DllPath, "--scenario", "echo-args"]);
            var outcome = await runner.RunAsync(Request());
            var argv = ParseEchoedArgs(outcome.Stdout);

            Assert.DoesNotContain("--bare", argv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }

    // --- Fix round 2/5: anulowanie nie moze zostawiac sieroty ---

    [Fact]
    public async Task Anulowanie_zabija_osierocony_proces_potomny()
    {
        // "sleep" spi realnie dlugo (30 s) i — jesli podano --pidfile — zapisuje
        // wlasny PID do pliku PRZED usnieciem. Nie mozna poznac PID przez stdout
        // z RunAsync: ReadToEndAsync zwraca dopiero po zakonczeniu procesu (EOF),
        // a proces ma zostac zabity, zanim sam sie zakonczy.
        //
        // Recenzja: Dispose() z "using (p)" NIE zabija procesu OS, tylko zwalnia
        // uchwyty .NET — bez jawnego Kill() prawdziwy dlugo dzialajacy `claude`
        // zostalby sierota po anulowaniu w kolejce (Task 9), dalej wydajac budzet
        // i mogac dopisac pliki do katalogu roboczego po tym, jak orkiestrator
        // uznal zadanie za zakonczone.
        var pidFile = Path.Combine(_work, "fake-pid.txt");
        using var cts = new CancellationTokenSource();
        var runner = new ClaudeRunner("dotnet", [FakeClaude.DllPath, "--scenario", "sleep", "--pidfile", pidFile]);
        var runTask = runner.RunAsync(Request(), cts.Token);

        var pid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(5));

        cts.Cancel();

        // Decyzja opisana przy ClaudeRunner.RunAsync: anulowanie propaguje sie jako
        // OperationCanceledException, nie wraca jako ClaudeRunOutcome.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.True(
            await WaitUntilProcessDeadAsync(pid, TimeSpan.FromSeconds(5)),
            $"proces {pid} (atrapa --scenario sleep) nadal zyje po anulowaniu — zostal sierota");
    }

    private static async Task<int> WaitForPidFileAsync(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                if (int.TryParse(text, out var pid)) return pid;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Atrapa nie zapisala PID do {path} w ciagu {timeout}");
    }

    private static async Task<bool> WaitUntilProcessDeadAsync(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsProcessAlive(pid)) return true;
            await Task.Delay(50);
        }
        return !IsProcessAlive(pid);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // proces juz nie istnieje
        }
    }
}
