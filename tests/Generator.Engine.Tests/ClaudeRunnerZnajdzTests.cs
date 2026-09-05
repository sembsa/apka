using Generator.Engine.ClaudeCli;
using Xunit;

namespace Generator.Engine.Tests;

/// <summary>
/// Zmierzone na zywym systemie: `claude` z npm to na Windows trzy pliki — `claude`
/// (skrypt sh), `claude.cmd` i `claude.ps1`. `Process.Start` z `UseShellExecute = false`
/// NIE stosuje PATHEXT, wiec trafial w skrypt sh i konczyl sie „An error occurred trying
/// to start process 'claude'". Silnik byl nieuruchamialny na Windows — wyszlo dopiero
/// przy pierwszej probie przejscia sciezki klienta na prawdziwym CLI, po 300 zielonych
/// testach.
///
/// Udawany dysk jest CASE-INSENSITIVE, bo taki jest Windows, a PATHEXT jest wielkimi
/// literami. Dysk rozrozniajacy wielkosc klamalby o systemie i test przechodzilby
/// albo padal z niewlasciwego powodu.
///
/// Separator listy katalogow bierzemy z `Path.PathSeparator`, nie wpisujemy `:` ani `;`
/// — inaczej „przypadek linuksowy" uruchomiony na Windows testuje wylacznie to, ze
/// split nie dziala.
/// </summary>
public class ClaudeRunnerZnajdzTests
{
    private const string Pathext = ".COM;.EXE;.BAT;.CMD;.PS1";

    private static HashSet<string> Dysk(params string[] pliki) =>
        new(pliki, StringComparer.OrdinalIgnoreCase);

    private static string Sciezka(params string[] katalogi) =>
        string.Join(Path.PathSeparator, katalogi);

    [Fact]
    public void Na_windows_dobiera_rozszerzenie_z_pathext()
    {
        // Na dysku sa OBA: skrypt sh bez rozszerzenia i uruchamialny `.cmd`.
        var dysk = Dysk(@"C:\npm\claude", @"C:\npm\claude.cmd");

        var wynik = ClaudeRunner.Znajdz(
            "claude", Sciezka(@"C:\inne", @"C:\npm"), Pathext, dysk.Contains);

        Assert.Equal(@"C:\npm\claude.cmd", wynik, ignoreCase: true);
    }

    [Fact]
    public void Kolejnosc_pathext_decyduje_ktory_plik_wygrywa()
    {
        var dysk = Dysk(@"C:\npm\claude.cmd", @"C:\npm\claude.exe");

        var wynik = ClaudeRunner.Znajdz("claude", Sciezka(@"C:\npm"), Pathext, dysk.Contains);

        Assert.Equal(@"C:\npm\claude.exe", wynik, ignoreCase: true);   // .EXE przed .CMD
    }

    [Fact]
    public void Wczesniejszy_katalog_w_path_wygrywa()
    {
        var dysk = Dysk(@"C:\pierwszy\claude.cmd", @"C:\drugi\claude.cmd");

        var wynik = ClaudeRunner.Znajdz(
            "claude", Sciezka(@"C:\pierwszy", @"C:\drugi"), Pathext, dysk.Contains);

        Assert.Equal(@"C:\pierwszy\claude.cmd", wynik, ignoreCase: true);
    }

    [Fact]
    public void Bez_pathext_bierze_plik_bez_rozszerzenia()
    {
        // Zachowanie linuksowe: pusty PATHEXT = jeden obrot petli, bez dorabiania.
        var katalog = Path.Combine("usr", "local", "bin");
        var dysk = Dysk(Path.Combine(katalog, "claude"));

        var wynik = ClaudeRunner.Znajdz("claude", Sciezka(katalog), null, dysk.Contains);

        Assert.Equal(Path.Combine(katalog, "claude"), wynik, ignoreCase: true);
    }

    [Fact]
    public void Sciezki_podanej_wprost_nie_zgadujemy()
    {
        // Tak testy podstawiaja atrape: ("dotnet", [dll, "--scenario", ...]).
        var wynik = ClaudeRunner.Znajdz(
            @"C:\atrapa\FakeClaude.exe", Sciezka(@"C:\npm"), Pathext, _ => false);

        Assert.Equal(@"C:\atrapa\FakeClaude.exe", wynik);
    }

    [Fact]
    public void Gdy_nic_nie_ma_oddajemy_oryginal_zeby_blad_zostal_zrozumialy()
    {
        var wynik = ClaudeRunner.Znajdz("claude", Sciezka(@"C:\npm"), Pathext, _ => false);

        Assert.Equal("claude", wynik);
    }
}
