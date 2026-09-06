using Generator.Engine.IO;
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

        var dane = Dane();
        var suma = dane.Podsumuj(dane.Wczytaj());

        Assert.Equal(1, suma.BezPomiaru);
        Assert.Equal(1.50m, suma.Wydano);
    }

    [Fact]
    public void Projekt_bez_wersji_nie_liczy_sie_jako_niezmierzony()
    {
        // Swiezy projekt jeszcze nic nie kosztowal i to jest prawda, nie luka.
        Projekt(wydano: 0m, wersji: 0);

        var dane = Dane();
        Assert.Equal(0, dane.Podsumuj(dane.Wczytaj()).BezPomiaru);
        Assert.Equal(StanKosztu.JeszczeNic, dane.Wczytaj().Single().Koszt);
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

        var dane = Dane();
        var suma = dane.Podsumuj(dane.Wczytaj());

        Assert.Equal(2, suma.Projektow);
        Assert.Equal(4.00m, suma.Wydano);
        Assert.Equal(2 * ProjectStore.DefaultBudgetUsd, suma.Budzet);
    }

    [Fact]
    public void Katalog_z_wersjami_ale_bez_project_json_jest_LICZONY_a_nie_przemilczany()
    {
        // W devie to smiec po atrapie (pisze snapshoty do tego samego korzenia).
        // W produkcji to czyjas strona lezaca na dysku bez metadanych — czyli
        // dokladnie ta rzecz, ktorej panel nie ma prawa przemilczec. Nie jest to
        // teoria: w `src/Generator.Web/projekty` leza dzis dwa takie katalogi.
        Projekt();
        Directory.CreateDirectory(Path.Combine(_root, "osierocony", "versions"));

        var dane = Dane();
        var wiersze = dane.Wczytaj();

        Assert.Single(wiersze);
        Assert.Equal(1, dane.Podsumuj(wiersze).ObceKatalogi);
    }

    [Fact]
    public void Pusty_katalog_nie_jest_niczym_i_nie_zaklóca_licznika()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pusty"));

        var dane = Dane();
        Assert.Equal(0, dane.Podsumuj(dane.Wczytaj()).ObceKatalogi);
    }

    public void Dispose()
    {
        DirectoryOps.Delete(_root);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Kolumna kosztow ma trzy stany, nie dwa — bo „0,00 USD" znaczy dzis co innego na
/// swiezym projekcie, a co innego na takim, ktory ma juz wersje. Te testy sa osobno,
/// zeby bylo widac, ze to rozroznienie jest wymaganiem, a nie szczegolem widoku.
/// </summary>
public class StanKosztuTests
{
    private static WierszPanelu Wiersz(decimal wydano, int wersji, string? blad = null) =>
        new("id", "active", "", 0, 15, wydano, 8m, wersji, wersji, DateTime.Now, blad);

    [Fact]
    public void Swiezy_projekt_jeszcze_nic_nie_kosztowal_i_to_jest_prawda_a_nie_luka()
        => Assert.Equal(StanKosztu.JeszczeNic, Wiersz(wydano: 0m, wersji: 0).Koszt);

    [Fact]
    public void Wersje_bez_kosztu_to_brak_pomiaru()
        => Assert.Equal(StanKosztu.NieZmierzony, Wiersz(wydano: 0m, wersji: 2).Koszt);

    [Fact]
    public void Koszt_wiekszy_od_zera_jest_zmierzony()
        => Assert.Equal(StanKosztu.Zmierzony, Wiersz(wydano: 0.01m, wersji: 1).Koszt);

    [Fact]
    public void O_projekcie_ktorego_nie_da_sie_odczytac_nie_mowimy_nic()
        => Assert.Equal(StanKosztu.Nieznany, Wiersz(wydano: 0m, wersji: 0, blad: "uszkodzony").Koszt);
}

public class OdmianaTests
{
    [Theory]
    [InlineData(1, "projekt")]
    [InlineData(2, "projekty")]
    [InlineData(4, "projekty")]
    [InlineData(5, "projektów")]
    [InlineData(11, "projektów")]  // nie „projekt", mimo koncowki 1
    [InlineData(12, "projektów")]  // nie „projekty", mimo koncowki 2
    [InlineData(22, "projekty")]
    [InlineData(25, "projektów")]
    [InlineData(0, "projektów")]
    public void Liczebnik_odmienia_rzeczownik(int n, string oczekiwane)
        => Assert.Equal(oczekiwane, Odmiana.Projektow(n));
}
