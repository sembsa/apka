using Generator.Engine.ClaudeCli;
using Generator.Engine.Jobs;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;

if (args.Length < 2)
{
    Console.Error.WriteLine("""
        Uzycie: dotnet run --project src/Generator.Cli -- <katalog-projektu> "<opis klienta>"
        Katalog MUSI lezec poza drzewem repo (podproces auto-wykrywa CLAUDE.md).
        """);
    return 2;
}

var projectDir = Path.GetFullPath(args[0]);
var description = args[1];

// Punkt 11 raportu: pusty opis przechodzil bramke argumentow (dlugosc args == 2
// jest spelniona przez pusty string) i wydawal pieniadze na model za nic. Trzy
// linie walidacji, PRZED czymkolwiek innym, zeby nie tworzyc nawet katalogu
// projektu dla wywolania, ktore i tak zaraz odrzucimy.
if (string.IsNullOrWhiteSpace(description))
{
    Console.Error.WriteLine("Opis klienta nie moze byc pusty.");
    return 2;
}

// Wykrywanie "katalog lezy w drzewie repo" idzie przez szukanie .git w gore
// od KATALOGU PROJEKTU, nie przez Directory.GetCurrentDirectory(): przy
// "dotnet run" biezacy katalog procesu to katalog projektu Generator.Cli,
// nie korzen repozytorium, wiec porownanie z cwd nigdy by nie zadzialalo.
// Podproces claude -p startuje z cwd ustawionym na katalog roboczy klienta
// i auto-wykrywa CLAUDE.md, wiec katalog klienta w naszym repo wciagnalby
// do sesji generujacej strone nasze zasady pracy w parze i indeks pamieci.
var repoRoot = FindRepoRoot(projectDir);
if (repoRoot is not null)
{
    Console.Error.WriteLine($"Katalog projektu lezy w drzewie repozytorium: {repoRoot}");
    Console.Error.WriteLine("Wybierz sciezke poza repo, np. ~/generator-projects/<id>.");
    return 2;
}

var projects = new ProjectStore(projectDir);
var versions = new VersionStore(projectDir);
var meta = File.Exists(Path.Combine(projectDir, "project.json"))
    ? projects.Load()
    : projects.Create(SourceKind.Idea);

// Punkt 11 raportu: "claude" na sztywno bylo jedynym miejscem w tym programie,
// ktore wydaje prawdziwe pieniadze — nie dalo sie sprawdzic reczne calej sciezki
// CLI inaczej niz placac. GENERATOR_CLAUDE_CMD pozwala podstawic atrape
// (Generator.FakeClaude) bez modyfikowania kodu, np.:
//   GENERATOR_CLAUDE_CMD="dotnet /pelna/sciezka/Generator.FakeClaude.dll --scenario success"
var (claudeExecutable, claudePrefixArgs) = ResolveClaudeCommand();
var runner = new ClaudeRunner(claudeExecutable, claudePrefixArgs);
var service = new GenerationService(runner, projects, versions);

// Pierwsza wersja: opis klienta jedzie jako komentarz globalny (Anchor = null)
// przez RunRoundAsync — silnik sam buduje instrukcje (PromptBuilder.BuildRound).
// BuildBrief nie jest tu wolany: nie ma go czym nakarmic (RunRoundAsync
// przyjmuje uwagi, nie gotowy tekst polecenia), a zapisywanie jego wyniku do
// pliku w katalogu roboczym nie ma odbiorcy w silniku i trafiloby do
// snapshotu klienta — VersionStore kopiuje CALY katalog roboczy.
Directory.CreateDirectory(versions.WorkDir);

Console.WriteLine($"Projekt: {meta.Id}");
Console.WriteLine($"Katalog roboczy: {versions.WorkDir}");
Console.WriteLine("Uruchamiam claude -p (to wydaje prawdziwe pieniadze)...");

var outcome = await service.RunRoundAsync(
    meta,
    [new Comment(Guid.NewGuid().ToString(), null, description, "desktop", DateTimeOffset.UtcNow)]);

Console.WriteLine($"Koszt przebiegu: ${outcome.SpentUsd:0.0000}");

if (!outcome.Succeeded)
{
    Console.Error.WriteLine($"NIEUDANE [{outcome.Failure!.Handling}] po {outcome.Failure.Attempts} probach");
    Console.Error.WriteLine($"Przyczyna (log, nie dla klienta): {outcome.Failure.Cause}");
    return 1;
}

Console.WriteLine($"Wersja {outcome.Version!.Number}: {outcome.Version.SnapshotDir}");
if (outcome.Version.OrphanedAnchors.Count > 0)
    Console.WriteLine($"Osierocone kotwice: {string.Join(", ", outcome.Version.OrphanedAnchors)}");

foreach (var r in outcome.CommentResults)
    Console.WriteLine($"  {r.CommentId}: {r.Status} — {r.Note}");

return 0;

// GENERATOR_CLAUDE_CMD: prosty split po bialych znakach (bez obslugi cudzyslowow —
// wystarcza do postaci "dotnet <dll> --scenario <nazwa>", ktorej uzywaja testy i
// ta atrapa). Brak zmiennej = zachowanie produkcyjne, "claude" bez argumentow z gory.
static (string Executable, IReadOnlyList<string>? PrefixArgs) ResolveClaudeCommand()
{
    var raw = Environment.GetEnvironmentVariable("GENERATOR_CLAUDE_CMD");
    if (string.IsNullOrWhiteSpace(raw))
        return ("claude", null);

    var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return (parts[0], parts.Length > 1 ? parts[1..] : null);
}

// Szuka .git (katalog zwyklego repo albo plik — worktree/submodul) w gore od
// "start" wlacznie. Zwraca znaleziony korzen repozytorium albo null, gdy
// zaden przodek go nie ma (droga wolna).
static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        var gitPath = Path.Combine(dir.FullName, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
            return dir.FullName;
    }

    return null;
}
