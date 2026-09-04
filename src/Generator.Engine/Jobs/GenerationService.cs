using System.Text.RegularExpressions;
using Generator.Engine.ClaudeCli;
using Generator.Engine.Gates;
using Generator.Engine.Model;
using Generator.Engine.Prompts;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;

namespace Generator.Engine.Jobs;

/// Kontrakt 4.3: frontend rozgalezia sie po Handling; Cause zostaje w logach.
/// RunRoundAsync jest terminalne — samo wyczerpuje swoj wewnetrzny budzet powtorek,
/// wiec kazde JobFailure, ktore stad wychodzi, niesie Halted (nigdy Retrying:
/// to by sugerowalo, ze wywolujacy ma sam sprobowac ponownie, a on juz probowal).
public record JobFailure(FailureHandling Handling, string Cause, int Attempts);

public record GenerationOutcome(
    bool Succeeded,
    VersionMeta? Version,
    JobFailure? Failure,
    IReadOnlyList<CommentResult> CommentResults,
    decimal SpentUsd);

public partial class GenerationService(IClaudeRunner runner, ProjectStore projects, VersionStore versions)
{
    /// Kontrakt 4.3: jedna automatyczna powtorka, potem halted.
    public const int RunRetryLimit = 1;

    /// Bramka miekka 5.2 nie podaje liczby — 2 do przestawienia po pomiarach.
    public const int AnchorRetryLimit = 2;

    [GeneratedRegex(@"^RAPORT\s+(\S+)\s+(applied|rejected)\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex ReportRegex();

    public async Task<GenerationOutcome> RunRoundAsync(
        ProjectMeta meta,
        IReadOnlyList<Comment> comments,
        CancellationToken ct = default)
    {
        var previousAnchors = PreviousAnchors(meta);
        var instruction = PromptBuilder.BuildRound(comments, previousAnchors);

        // Sesja: PIERWSZY przebieg projektu (brak wersji) dostaje nowy GUID i
        // --session-id; kazdy kolejny przebieg — w tym powtorka w tej samej
        // rundzie i kazda kolejna runda — wznawia ten sam identyfikator przez
        // --resume. Nie ma tu rozroznienia "runda" vs "powtorka": to jeden
        // ciagly identyfikator na caly projekt.
        var sessionId = meta.Versions.Count > 0 ? meta.Versions[^1].SessionId : Guid.NewGuid().ToString();
        var isFirstRun = meta.Versions.Count == 0;

        decimal spent = 0m;
        var attempts = 0;
        ClaudeResult? lastParsed = null;

        // --- przebieg + bramka 5.1 + bramka twarda 5.2, z jedna powtorka ---
        // Kolejnosc: bramka wykonania (RunGate) NAJPIERW — nie ma sensu sprawdzac
        // renderowalnosci pliku po przebiegu, ktory sam w sobie sie nie powiodl.
        while (true)
        {
            attempts++;
            var outcome = await runner.RunAsync(
                new ClaudeRunRequest(versions.WorkDir, instruction, sessionId, isFirstRun), ct);

            var gate = RunGate.Evaluate(outcome);
            // Kontrakt: Parsed niesie TotalCostUsd takze przy werdykcie negatywnym —
            // pieniadze zostaly wydane niezaleznie od tego, czy przebieg sie udal.
            spent += gate.Parsed?.TotalCostUsd ?? 0m;
            lastParsed = gate.Parsed ?? lastParsed;

            if (gate.Verdict == GateVerdict.Halt)
                return Failed(meta, FailureHandling.Halted, gate.Cause, attempts, spent);

            if (gate.Verdict == GateVerdict.Retry)
            {
                if (attempts > RunRetryLimit)
                    return Failed(meta, FailureHandling.Halted, gate.Cause, attempts, spent);

                // Sesja istnieje dopiero, gdy CLI zdazylo zwrocic sparsowalny JSON
                // (gate.Parsed niepuste) — to na tym opiera sie caly --resume.
                // Przy pustym/niesparsowalnym stdout (np. przebieg padl zanim
                // model cokolwiek zwrocil) sesja mogla nigdy nie powstac; empirycznie
                // sprawdzone na prawdziwym `claude -p --resume <nieistniejacy-id>`:
                // exit 1, pusty stdout, stderr "No conversation found with session
                // ID: ...". Bezwarunkowe przestawienie na --resume zamienialoby
                // KAZDA taka usterke w gwarantowany Halted (powtorka od razu pada
                // na zlym --resume), mimo ze powtorka z --session-id mialaby szanse.
                if (gate.Parsed is not null)
                    isFirstRun = false;
                continue;
            }

            var render = RenderValidator.Check(versions.WorkDir);
            if (render.Ok) break;

            var cause = $"wersja sie nie renderuje: {string.Join("; ", render.Problems)}";
            if (attempts > RunRetryLimit)
                return Failed(meta, FailureHandling.Halted, cause, attempts, spent);

            isFirstRun = false;
            instruction = $"{instruction}\n\nPOPRAW: {cause}";
        }

        // --- bramka miekka: kotwice. Po wyczerpaniu prob wersja IDZIE do klienta,
        // ale KAZDA proba naprawy dotyka realny WorkDir na dysku (efekt runnera
        // pisze pliki bezposrednio) — proba, ktora zepsuje renderowalnosc, nie
        // moze zostac wpisana do snapshotu (bramka twarda ma pierwszenstwo nad
        // kazda wersja, nie tylko nad pierwszym przebiegiem). Dlatego trzymamy
        // kopie zapasowa ostatniego znanego DOBREGO stanu i przywracamy ja, gdy
        // proba naprawy cokolwiek popsuje.
        var current = AnchorExtractor.Extract(versions.WorkDir);
        var orphaned = AnchorExtractor.Orphaned(previousAnchors, current);

        if (orphaned.Count > 0)
        {
            var backupDir = Directory.CreateTempSubdirectory("gen-svc-anchor-backup-").FullName;
            try
            {
                ReplaceDirectory(versions.WorkDir, backupDir);

                var anchorTries = 0;
                while (orphaned.Count > 0 && anchorTries < AnchorRetryLimit)
                {
                    anchorTries++;
                    var repair = await runner.RunAsync(
                        new ClaudeRunRequest(versions.WorkDir, PromptBuilder.BuildAnchorRepair(orphaned), sessionId, false),
                        ct);

                    var gate = RunGate.Evaluate(repair);
                    spent += gate.Parsed?.TotalCostUsd ?? 0m;

                    var repaired = gate.Verdict == GateVerdict.Ok && RenderValidator.Check(versions.WorkDir).Ok;
                    if (!repaired)
                    {
                        // Nie walczymy dalej — przywracamy ostatni dobry stan (ta
                        // proba mogla go nadpisac zepsutym HTML-em) i dostarczamy
                        // z lista osieroconych sprzed tej proby.
                        ReplaceDirectory(backupDir, versions.WorkDir);
                        break;
                    }

                    current = AnchorExtractor.Extract(versions.WorkDir);
                    orphaned = AnchorExtractor.Orphaned(previousAnchors, current);
                    ReplaceDirectory(versions.WorkDir, backupDir);   // ten stan tez jest dobry — odswiez kopie
                }
            }
            finally
            {
                // Sprzatanie w finally moze samo rzucic (uchwyt pliku wciaz
                // otwarty, wyscig z systemem plikow) — dokladnie tak samo jak przy
                // Process.Kill w ClaudeRunner.cs. Gdyby anulowanie przyszlo w
                // trakcie petli naprawy kotwic, OperationCanceledException lecialby
                // przez ten finally; goly Directory.Delete, ktory akurat wtedy
                // rzuci (bo proba naprawy trzyma otwarty plik / trwa jeszcze zapis),
                // ZASTAPILBY je swoim wyjatkiem (np. IOException) i wywolujacy
                // zapisalby to jako zwykla awarie zamiast uszanowac anulowanie.
                // Ten catch (Exception) jest wiec celowo szeroki: zaden blad ze
                // sprzatania nie jest wazniejszy od wyjatku, ktory to sprzatanie
                // przerwalo — NIE zawezaj do konkretnych typow.
                try
                {
                    Directory.Delete(backupDir, recursive: true);
                }
                catch (Exception)
                {
                }
            }
        }

        var number = meta.Versions.Count + 1;
        var version = versions.Commit(number, sessionId, spent, orphaned);

        var updated = ProjectStore.WithRoundConsumed(ProjectStore.WithSpend(meta, spent)) with
        {
            Status = ProjectStatus.Active,
            Versions = [.. meta.Versions, version],
        };
        projects.Save(Freeze(updated));

        var results = ParseReport(lastParsed?.ResultText ?? string.Empty, comments);
        return new GenerationOutcome(true, version, null, results, spent);
    }

    /// Kontrakt 4.1: projekt zatrzymuje PIERWSZY wyczerpany licznik (rundy albo budzet).
    private static ProjectMeta Freeze(ProjectMeta m) =>
        m.RoundsUsed >= m.RoundsLimit || m.SpentUsd >= m.BudgetUsd
            ? m with { Status = ProjectStatus.Frozen }
            : m;

    /// Kontrakt 3.3: status wylacznie z raportu kluczowanego naszym Comment.Id.
    /// Komentarz bez wpisu zostaje open — nie zgadujemy za model.
    public static IReadOnlyList<CommentResult> ParseReport(string resultText, IReadOnlyList<Comment> comments)
    {
        var known = comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var results = new List<CommentResult>();

        foreach (var m in ReportRegex().Matches(resultText).Cast<Match>())
        {
            var id = m.Groups[1].Value;
            if (!known.Contains(id)) continue;
            if (results.Any(r => r.CommentId == id)) continue;

            results.Add(new CommentResult(
                id,
                m.Groups[2].Value == "applied" ? CommentStatus.Applied : CommentStatus.Rejected,
                m.Groups[3].Value.Trim()));
        }

        return results;
    }

    /// Kontrakt 4.1 (asymetria liczników): powtorki po awarii ZUZYWAJA budzet —
    /// pieniadze zostaly wydane naprawde — ale NIE zuzywaja rund klienta, bo
    /// awaria po naszej stronie nie moze zabierac klientowi umowionych poprawek.
    /// Dlatego tu jest WithSpend, ale NIGDY WithRoundConsumed, i status wraca
    /// wylacznie przez Freeze — nigdy na sile ustawiany na Active — bo kontrakt
    /// 4.4 wymaga, zeby nieudana runda byla dla klienta brakiem zdarzenia poza
    /// realnie wydanymi pieniedzmi: Versions i RoundsUsed zostaja nietkniete.
    private GenerationOutcome Failed(ProjectMeta meta, FailureHandling handling, string cause, int attempts, decimal spent)
    {
        projects.Save(Freeze(ProjectStore.WithSpend(meta, spent)));
        return new GenerationOutcome(false, null, new JobFailure(handling, cause, attempts), [], spent);
    }

    private IReadOnlyList<string> PreviousAnchors(ProjectMeta meta) =>
        meta.Versions.Count == 0
            ? []
            : [.. AnchorExtractor.Extract(meta.Versions[^1].SnapshotDir)];

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;

        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// Uzywane jednakowo do robienia kopii zapasowej (WorkDir -> backup) i do
    /// przywracania z niej (backup -> WorkDir): kierunek okresla kolejnosc
    /// argumentow u wywolujacego. "to" jest zawsze CALKOWICIE zastepowany.
    private static void ReplaceDirectory(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        CopyDirectory(from, to);
    }
}
