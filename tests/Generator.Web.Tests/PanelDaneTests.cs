using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Web.Api;
using Generator.Web.Panel;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Panel jest jedynym miejscem w aplikacji, ktore patrzy na WSZYSTKIE projekty naraz.
/// Te testy pilnuja dwoch rzeczy: ze lista przezywa zepsuty projekt (bo wtedy jest
/// najbardziej potrzebna) i ze zero w kolumnie kosztow nie klamie.
/// </summary>
public class PanelDaneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gen-panel-" + Guid.NewGuid().ToString("N"));

    private PanelDane Dane() => new(new ProjectPaths(_root, "claude"));

    private ProjectMeta Projekt(decimal wydano = 0m, int wersji = 0, ProjectStatus status = ProjectStatus.Active)
    {
        var store = new ProjectStore(Path.Combine(_root, Guid.NewGuid().ToString()));
        var meta = store.Create(SourceKind.Idea) with
        {
            Status = status,
            SpentUsd = wydano,
            Versions = Enumerable.Range(1, wersji)
                .Select(n => new VersionMeta(n, "s", "d", 0m, []))
                .ToList(),
            CurrentVersion = wersji,
        };
        store.Save(meta);
        return meta;
    }

    [Fact]
    public void Jeden_uszkodzony_projekt_nie_zabiera_ze_soba_calej_listy()
    {
        // Panel jest najbardziej potrzebny wtedy, gdy cos jest zepsute. Lista, ktora
        // w calosci znika przez jeden zly plik, nie jest narzedziem diagnostycznym.
        var dobry = Projekt();
        var zly = Path.Combine(_root, Guid.NewGuid().ToString());
        Directory.CreateDirectory(zly);
        File.WriteAllText(Path.Combine(zly, "project.json"), "{ to nie jest JSON");

        var wiersze = Dane().Wczytaj();

        Assert.Equal(2, wiersze.Count);
        Assert.Contains(wiersze, w => w.Id == dobry.Id && w.Blad is null);
        Assert.Contains(wiersze, w => w.Blad is not null);
    }

    [Fact]
    public void Uszkodzony_projekt_wola_o_uwage()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zepsuty"));
        File.WriteAllText(Path.Combine(_root, "zepsuty", "project.json"), "nie-json");

        Assert.True(Dane().Wczytaj().Single().WymagaUwagi);
    }

    [Fact]
    public void Brak_katalogu_projektow_to_pusta_lista_a_nie_wyjatek()
    {
        // Na swiezym klonie `projekty/` jeszcze nie istnieje. Panel przed pierwszym
        // klientem ma byc pusty, nie zepsuty.
        Assert.Empty(Dane().Wczytaj());
    }

    [Fact]
    public void Katalog_bez_project_json_nie_jest_projektem()
    {
        Directory.CreateDirectory(Path.Combine(_root, "smieci"));

        Assert.Empty(Dane().Wczytaj());
    }

    [Fact]
    public void Zero_kosztu_przy_istniejacych_wersjach_to_brak_pomiaru_a_nie_darmowa_strona()
    {
        // Silnik zapisuje koszt dopiero, gdy `claude` odda JSON — a na Windowsie
        // dzis nie oddaje go wcale. „Lacznie 0,00 USD" czytaloby sie jak dobra
        // wiadomosc, bedac brakiem danych.
        Projekt(wydano: 0m, wersji: 2);
        Projekt(wydano: 1.50m, wersji: 1);

        var suma = PanelDane.Podsumuj(Dane().Wczytaj());

        Assert.Equal(1, suma.BezPomiaru);
        Assert.Equal(1.50m, suma.Wydano);
    }

    [Fact]
    public void Projekt_bez_wersji_nie_liczy_sie_jako_niezmierzony()
    {
        // Swiezy projekt jeszcze nic nie kosztowal i to jest prawda, nie luka.
        Projekt(wydano: 0m, wersji: 0);

        Assert.Equal(0, PanelDane.Podsumuj(Dane().Wczytaj()).BezPomiaru);
    }

    [Fact]
    public void Wyczerpany_budzet_i_zamrozenie_wolaja_o_uwage()
    {
        Projekt(wydano: ProjectStore.DefaultBudgetUsd, wersji: 1);
        Projekt(status: ProjectStatus.Frozen, wersji: 1);
        Projekt(wydano: 0.10m, wersji: 1);

        Assert.Equal(2, Dane().Wczytaj().Count(w => w.WymagaUwagi));
    }

    [Fact]
    public void Sumuje_koszty_i_budzety_wszystkich_projektow()
    {
        Projekt(wydano: 1.25m, wersji: 1);
        Projekt(wydano: 2.75m, wersji: 1);

        var suma = PanelDane.Podsumuj(Dane().Wczytaj());

        Assert.Equal(2, suma.Projektow);
        Assert.Equal(4.00m, suma.Wydano);
        Assert.Equal(2 * ProjectStore.DefaultBudgetUsd, suma.Budzet);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
