using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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

    /// UTF8Encoding(false) — jawnie BEZ BOM. `new UTF8Encoding()` i `Encoding.UTF8`
    /// pisza BOM, ktory trafilby na poczatek promptu jako trzy smieciowe bajty.
    private static readonly UTF8Encoding Utf8BezBom = new(encoderShouldEmitUTF8Identifier: false);

    /// Dopisek dla generowania STRONY (wersja pierwsza i kazda runda uwag).
    public const string PageAppendix =
        "Generujesz prosta strone www: jeden plik index.html i jeden style.css, bez frameworkow. " +
        "Zachowuj atrybuty data-cmt-id na blokach tresci.";

    /// Dopisek dla PROPOZYCJI. Osobny, bo PageAppendix zada dokladnie tego, czego
    /// propozycje zabraniaja: jednego index.html (a maja byc trzy szkice) i
    /// zachowywania data-cmt-id (szkic nie jest wersja, wiec kotwic nie ma).
    /// Zaszyty na stale dopisek strony jechal wczesniej takze z propozycjami —
    /// model dostawal w jednym wywolaniu dwa wykluczajace sie polecenia.
    public const string ProposalsAppendix =
        "Generujesz TRZY szkice kierunku, kazdy jako jeden samodzielny plik HTML " +
        "ze stylami w <style>. Bez frameworkow. Nie dodawaj atrybutow data-cmt-id.";

    /// <summary>
    /// Rozwija nazwe polecenia do pelnej sciezki pliku wykonywalnego.
    ///
    /// POWOD, zmierzony: na Windows `claude` z npm to trzy pliki — `claude` (skrypt sh),
    /// `claude.cmd` i `claude.ps1`. `Process.Start` z `UseShellExecute = false` NIE
    /// stosuje PATHEXT, wiec szuka pliku dokladnie „claude", znajduje skrypt sh i nie
    /// umie go uruchomic: „An error occurred trying to start process 'claude'".
    /// Silnik byl wiec nieuruchamialny na Windows, a pracujemy na nim oboje — wyszlo
    /// dopiero przy pierwszej probie przejscia sciezki na prawdziwym CLI.
    ///
    /// Na Linuksie i macOS PATHEXT nie istnieje, petla robi jeden obrot z pustym
    /// rozszerzeniem i wynik jest taki jak dotad.
    /// </summary>
    public static string Znajdz(
        string polecenie, string? path, string? pathext, Func<string, bool> istnieje)
    {
        // Sciezka podana wprost (albo atrapa „dotnet") — nie zgadujemy.
        if (polecenie.Contains(Path.DirectorySeparatorChar) ||
            polecenie.Contains(Path.AltDirectorySeparatorChar) ||
            istnieje(polecenie))
            return polecenie;

        var rozszerzenia = string.IsNullOrEmpty(pathext)
            ? [""]
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var katalog in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in rozszerzenia)
            {
                var kandydat = Path.Combine(katalog.Trim(), polecenie + ext);
                if (istnieje(kandydat)) return kandydat;
            }
        }

        // Nic nie znaleziono: oddajemy oryginal, zeby komunikat bledu zostal ten,
        // ktory czlowiek rozumie („nie ma claude w PATH"), a nie nasz wymysl.
        return polecenie;
    }

    private static string Znajdz(string polecenie) => Znajdz(
        polecenie,
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        File.Exists);

    public static IReadOnlyList<string> BuildArguments(ClaudeRunRequest r, bool useBare)
    {
        // `-p` BEZ tresci promptu: instrukcja idzie przez STDIN (patrz RunAsync).
        // Zmierzone przez Przemka na Windows: `claude.cmd` uruchamia sie przez cmd.exe,
        // a cmd.exe urywa polecenie na pierwszym znaku nowej linii. PromptBuilder sklada
        // prompt przez AppendLine, wiec do modelu docierala TYLKO PIERWSZA LINIA, a
        // wszystko po niej — z `--output-format json` wlacznie — gubilo sie po drodze
        // (stdout byl proza, nie JSON-em). Przy okazji znika limit ~8191 znakow linii
        // polecenia cmd.exe, ktory brief z wklejonym szkicem HTML potrafi przekroczyc,
        // i mielenie `% ^ & | < >` z tekstu klienta przez interpreter poleceń.
        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--restricted",
            "--strict-mcp-config",
            "--tools", AllowedTools,
            "--permission-mode", "acceptEdits",
            "--permission-prompts", "none",
            "--model", Model,
            "--append-system-prompt", r.SystemAppendix ?? PageAppendix,
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

        var psi = new ProcessStartInfo(Znajdz(executable))
        {
            WorkingDirectory = request.WorkspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // kanal promptu (patrz nizej)
            UseShellExecute = false,

            // Jawny UTF-8 bez BOM na wszystkich trzech strumieniach. Domyslne
            // kodowanie przekierowanych strumieni to strona kodowa konsoli — na
            // polskim Windows CP1250 — wiec „Kraków" w briefie klienta dotarloby
            // do modelu przekrecone, a polskie znaki w JSON-ie z odpowiedzia
            // wrocilyby przekrecone do nas. BOM (UTF8Encoding domyslnie go pisze)
            // dokleilby trzy bajty na poczatku promptu.
            StandardInputEncoding = Utf8BezBom,
            StandardOutputEncoding = Utf8BezBom,
            StandardErrorEncoding = Utf8BezBom,
        };

        foreach (var a in prefixArgs ?? []) psi.ArgumentList.Add(a);
        foreach (var a in BuildArguments(request, useBare)) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Punkt 10 raportu: to jest sciezka INTERPRETACJI (brak pliku
            // wykonywalnego to normalna sciezka, ktora bramka z Taska 5 rozpoznaje
            // po ProcessStarted: false), nie sprzatanie w finally — zgodnie z zasada
            // projektu ("waski catch tam, gdzie interpretujemy, szeroki tylko w
            // blokach sprzatajacych") lapiemy tylko wyjatki, ktore Process.Start
            // faktycznie dokumentuje dla "nie udalo sie wystartowac procesu"
            // (brak pliku, zla nazwa, brak wsparcia platformy). Szeroki catch
            // (Exception) zamienialby np. OutOfMemoryException w zwykle
            // "proces nie wystartowal" — myslacy blad staje sie cichym Halt zamiast
            // wywalic sie tak, jak powinien.
            return new ClaudeRunOutcome(false, -1, string.Empty, ex.Message, sw.Elapsed);
        }

        if (p is null)
            return new ClaudeRunOutcome(false, -1, string.Empty, "Process.Start zwrocil null", sw.Elapsed);

        using (p)
        {
            // Kolejnosc jest istotna: najpierw ZACZYNAMY czytac stdout i stderr,
            // dopiero potem piszemy prompt. Brief z wklejonym szkicem HTML ma
            // dziesiatki kilobajtow; gdyby potomek zdazyl zapelnic swoj bufor
            // wyjscia, zanim my skonczymy pisac, oba procesy staneleby na
            // pelnych potokach — klasyczny zakleszczenie na potoku.
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);

            try
            {
                await p.StandardInput.WriteAsync(request.Instruction.AsMemory(), ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Potomek zamknal swoj koniec potoku, zanim odebral calosc (np. padl
                // na zlej fladze). Nie ma czego dostarczyc, a prawdziwa przyczyne
                // niesie exit code i stderr — zapis nie moze ich przeslonic.
            }

            // Zamkniecie jest obowiazkowe, nie kosmetyczne: `claude -p` czyta stdin
            // do EOF, wiec bez tego czekalby w nieskonczonosc.
            p.StandardInput.Close();

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
                    // przekroczenie limitu czasu jako awarie. Kill(bool) z
                    // entireProcessTree: true udokumentowanego rzuca nie tylko
                    // Win32Exception, ale tez AggregateException ("Not all
                    // processes in the associated process's descendant tree
                    // could be terminated") — realny wynik wyscigu, gdy potomek
                    // (np. wywolany przez claude przez narzedzie) konczy sie
                    // dokladnie w trakcie zabijania drzewa. Dlatego catch
                    // (Exception) tutaj CELOWO: to sprzatanie w finally, ktorego
                    // jedynym zadaniem jest nie przesloniec wyjatku niosacego
                    // prawdziwa przyczyne (anulowanie) — zaden blad ze sprzatania
                    // nie jest wazniejszy od tego, co to sprzatanie przerwalo.
                    // NIE zawezaj z powrotem do konkretnych typow.
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
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
