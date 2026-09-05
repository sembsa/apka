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
/// Separator listy katalogow bierzemy z `Path.PathSeparator`, a same sciezki skladamy
/// przez `Path.Combine` (patrz `Kat`) — nie wpisujemy `:`, `;`, `/` ani `\` na sztywno.
/// Inaczej „przypadek windowsowy" uruchomiony na macOS testuje wylacznie to, ze
/// `Path.Combine` nie sklei `C:\npm` z `claude.cmd` po windowsowemu.
/// </summary>
public class ClaudeRunnerZnajdzTests
{
    private const string Pathext = ".COM;.EXE;.BAT;.CMD;.PS1";

    private static HashSet<string> Dysk(params string[] pliki) =>
        new(pliki, StringComparer.OrdinalIgnoreCase);

    private static string Sciezka(params string[] katalogi) =>
        string.Join(Path.PathSeparator, katalogi);

    /// <summary>
    /// Katalog bezwzgledny w konwencji biezacego systemu: `C:\npm` na Windows,
    /// `/npm` na Uniksie. `Znajdz` sklada kandydatow przez `Path.Combine`, wiec
    /// sciezka wpisana na sztywno po windowsowemu daje na macOS `C:\npm/claude.cmd`
    /// i test pada mimo poprawnej implementacji. To ten sam blad co formatowanie
    /// liczb kultura systemu w atrapach silnika, tylko po stronie separatorow.
    /// </summary>
    private static string Kat(params string[] segmenty) =>
        Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(segmenty));

    private static string Plik(string katalog, string nazwa) => Path.Combine(katalog, nazwa);

    [Fact]
    public void Na_windows_dobiera_rozszerzenie_z_pathext()
    {
        // Na dysku sa OBA: skrypt sh bez rozszerzenia i uruchamialny `.cmd`.
        var npm = Kat("npm");
        var dysk = Dysk(Plik(npm, "claude"), Plik(npm, "claude.cmd"));

        var wynik = ClaudeRunner.Znajdz(
            "claude", Sciezka(Kat("inne"), npm), Pathext, dysk.Contains);

        Assert.Equal(Plik(npm, "claude.cmd"), wynik, ignoreCase: true);
    }

    [Fact]
    public void Kolejnosc_pathext_decyduje_ktory_plik_wygrywa()
    {
        var npm = Kat("npm");
        var dysk = Dysk(Plik(npm, "claude.cmd"), Plik(npm, "claude.exe"));

        var wynik = ClaudeRunner.Znajdz("claude", Sciezka(npm), Pathext, dysk.Contains);

        Assert.Equal(Plik(npm, "claude.exe"), wynik, ignoreCase: true);   // .EXE przed .CMD
    }

    [Fact]
    public void Wczesniejszy_katalog_w_path_wygrywa()
    {
        var pierwszy = Kat("pierwszy");
        var drugi = Kat("drugi");
        var dysk = Dysk(Plik(pierwszy, "claude.cmd"), Plik(drugi, "claude.cmd"));

        var wynik = ClaudeRunner.Znajdz(
            "claude", Sciezka(pierwszy, drugi), Pathext, dysk.Contains);

        Assert.Equal(Plik(pierwszy, "claude.cmd"), wynik, ignoreCase: true);
    }

    [Fact]
    public void Bez_pathext_bierze_plik_bez_rozszerzenia()
    {
        // Zachowanie linuksowe: pusty PATHEXT = jeden obrot petli, bez dorabiania.
        var katalog = Kat("usr", "local", "bin");
        var dysk = Dysk(Plik(katalog, "claude"));

        var wynik = ClaudeRunner.Znajdz("claude", Sciezka(katalog), null, dysk.Contains);

        Assert.Equal(Plik(katalog, "claude"), wynik, ignoreCase: true);
    }

    [Fact]
    public void Sciezki_podanej_wprost_nie_zgadujemy()
    {
        // Tak testy podstawiaja atrape: ("dotnet", [dll, "--scenario", ...]).
        var atrapa = Plik(Kat("atrapa"), "FakeClaude.exe");

        // Na dysku LEZY to, co petla zgadywania by znalazla: `Path.Combine` z katalogu
        // PATH i bezwzglednej sciezki oddaje sama sciezke, wiec kandydatem jest
        // `...FakeClaude.exe.EXE`. Bez wczesnego wyjscia test dostalby wlasnie ten plik
        // — dzieki temu sprawdza wyjscie, a nie „i tak nic nie znaleziono".
        var dysk = Dysk(atrapa + ".EXE");

        var wynik = ClaudeRunner.Znajdz(atrapa, Sciezka(Kat("npm")), Pathext, dysk.Contains);

        Assert.Equal(atrapa, wynik);
    }

    [Fact]
    public void Gdy_nic_nie_ma_oddajemy_oryginal_zeby_blad_zostal_zrozumialy()
    {
        var wynik = ClaudeRunner.Znajdz("claude", Sciezka(Kat("npm")), Pathext, _ => false);

        Assert.Equal("claude", wynik);
    }
}
