using Generator.Web.Api;

namespace Generator.Web.Panel;

/// <summary>
/// Co znaczy liczba w kolumnie „koszt". Trzy stany, nie dwa, bo „0,00 USD" ma
/// dzis DWA rozne znaczenia i pomylenie ich zamienia panel w zrodlo falszywego
/// spokoju.
/// </summary>
public enum StanKosztu
{
    /// Nie wiemy nic — projekt nie da sie odczytac.
    Nieznany,

    /// Projekt jeszcze nic nie kosztowal, bo jeszcze nic nie wygenerowal. To PRAWDA,
    /// nie luka w danych.
    JeszczeNic,

    /// Sa wersje, a kosztu nie ma. Silnik zapisuje go dopiero, gdy `claude` odda JSON —
    /// a na Windowsie dzis nie oddaje go wcale (awaria B). To BRAK POMIARU, nie
    /// darmowa strona, i panel ma o tym mowic wprost.
    NieZmierzony,

    Zmierzony,
}

/// <summary>
/// Jedna wersja w historii projektu — tyle, ile panel potrafi powiedziec bez
/// schodzenia na dysk. Powstalo z pytania, na ktore panel dotad NIE odpowiadal:
/// ktora runda byla droga. Suma kosztu projektu i liczba wersji wygladaly tak samo
/// przy czterech rundach po 0,55 USD i przy trzech po 0,10 z jedna po 2,00.
/// </summary>
/// <param name="Powstala">
/// `null` dla wersji sprzed wprowadzenia stempla czasu (kontrakt 6.2) — panel
/// pokazuje wtedy myslnik, a nie zmyslona date.
/// </param>
/// <param name="NaBazie">
/// Numer wersji, z ktorej ta powstala. Rozny od `Numer - 1` po powrocie do starszej
/// wersji — i wtedy wlasnie jest najbardziej potrzebny, bo tlumaczy, czemu historia
/// nie jest linia prosta.
/// </param>
public record WersjaPanelu(
    int Numer,
    decimal Koszt,
    DateTimeOffset? Powstala,
    int? NaBazie,
    int OsieroconychKotwic,
    bool Biezaca);

/// <summary>
/// Jeden wiersz panelu: tyle o projekcie klienta, ile my — dwoje ludzi po naszej
/// stronie — potrzebujemy, zeby wiedziec, co ten klient ma i ile nas kosztowal.
///
/// Czego tu NIE MA i byc nie moze: `Token`. Panel jest lista WSZYSTKICH projektow,
/// wiec wydrukowany token zamienilby jedna strone w pek kluczy do kont wszystkich
/// klientow — a tokeny sie nie rotuja (decyzja 3: bez kont, token w linku).
/// Podglad go nie potrzebuje: kontrakt 1 mowi wprost, ze `/preview/*` w v1 tokenu
/// NIE wymaga. Do obejrzenia strony klienta wystarczy wiec samo `Id`, a wejscie
/// w jego edytor jest osobna, swiadoma czynnoscia — nie efektem ubocznym listy.
/// </summary>
/// <param name="Blad">
/// Nie-null, gdy `project.json` nie dal sie odczytac. Wtedy pozostale pola sa puste.
/// Wiersz zostaje na liscie, bo panel jest najbardziej potrzebny wlasnie wtedy,
/// gdy cos jest zepsute — lista, ktora znika w calosci przez jeden uszkodzony plik,
/// nie jest narzedziem diagnostycznym.
/// </param>
public record WierszPanelu(
    string Id,
    string Status,
    string Opis,
    int RundyZuzyte,
    int RundyLimit,
    decimal Wydano,
    decimal Budzet,
    IReadOnlyList<WersjaPanelu> Wersje,
    int WersjaBiezaca,
    DateTime OstatniZapis,
    string? Blad = null)
{
    /// Liczona z historii, nie trzymana obok niej: osobne pole moglo rozjechac sie
    /// z lista i panel przeczylby sam sobie w dwoch miejscach ekranu.
    public int Wersji => Wersje.Count;

    /// Jedno miejsce, ktore decyduje, co znaczy zero — komorka tabeli i podsumowanie
    /// musza mowic to samo, inaczej panel sam sobie przeczy w dwoch miejscach ekranu.
    public StanKosztu Koszt => this switch
    {
        { Blad: not null } => StanKosztu.Nieznany,
        { Wydano: > 0m } => StanKosztu.Zmierzony,
        { Wersji: 0 } => StanKosztu.JeszczeNic,
        _ => StanKosztu.NieZmierzony,
    };

    /// Projekt zamrozony ALBO na granicy — to sa wiersze, na ktore patrzymy najpierw.
    public bool WymagaUwagi =>
        Blad is not null || Status == "frozen" || RundyZuzyte >= RundyLimit || Wydano >= Budzet;
}

/// <param name="BezPomiaru">Ile projektow ma wersje, ale zerowy koszt — patrz StanKosztu.</param>
/// <param name="ObceKatalogi">
/// Katalogi w `projekty/`, ktore maja zawartosc, ale nie maja `project.json`, wiec panel
/// nie umie o nich nic powiedziec. W devie to zwykle smieci po atrapie (pisze snapshoty
/// do tego samego korzenia). W produkcji to znaczy, ze czyjas strona lezy na dysku bez
/// metadanych — i wlasnie dlatego ta liczba jest na ekranie, a nie przemilczana.
/// </param>
public record PodsumowaniePanelu(int Projektow, decimal Wydano, decimal Budzet, int BezPomiaru, int ObceKatalogi);

/// <summary>
/// Czyta katalog projektow do postaci nadajacej sie na liste. Osobno od `ProjectApi`,
/// bo to jedyne miejsce w aplikacji, ktore z zalozenia patrzy POZA jeden projekt —
/// kazda inna sciezka dostaje `id` od klienta i ma prawo widziec tylko jego.
/// </summary>
public class PanelDane(ProjectPaths paths)
{
    public IReadOnlyList<WierszPanelu> Wczytaj()
    {
        // Na swiezym klonie `projekty/` jeszcze nie istnieje. Pusta lista, nie wyjatek:
        // panel przed pierwszym klientem jest pusty, a nie zepsuty.
        if (!Directory.Exists(paths.Root)) return [];

        return Katalogi()
            .Where(paths.Exists)
            .Select(Wiersz)
            .OrderByDescending(w => w.OstatniZapis)
            .ToList();
    }

    /// Katalogi z zawartoscia, ale bez `project.json` — patrz PodsumowaniePanelu.ObceKatalogi.
    public int ObceKatalogi() =>
        Directory.Exists(paths.Root)
            ? Katalogi().Count(id => !paths.Exists(id) && Directory.EnumerateFileSystemEntries(paths.Dir(id)).Any())
            : 0;

    public PodsumowaniePanelu Podsumuj(IReadOnlyList<WierszPanelu> wiersze) => new(
        Projektow: wiersze.Count,
        Wydano: wiersze.Sum(w => w.Wydano),
        Budzet: wiersze.Sum(w => w.Budzet),
        BezPomiaru: wiersze.Count(w => w.Koszt == StanKosztu.NieZmierzony),
        ObceKatalogi: ObceKatalogi());

    private IEnumerable<string> Katalogi() =>
        Directory.EnumerateDirectories(paths.Root)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrEmpty(id))!;

    private WierszPanelu Wiersz(string id)
    {
        var zapis = File.GetLastWriteTime(Path.Combine(paths.Dir(id), "project.json"));
        try
        {
            var m = paths.Store(id).Load();
            return new WierszPanelu(
                Id: m.Id,
                Status: m.Status.ToString().ToLowerInvariant(),
                Opis: m.Description,
                RundyZuzyte: m.RoundsUsed,
                RundyLimit: m.RoundsLimit,
                Wydano: m.SpentUsd,
                Budzet: m.BudgetUsd,
                Wersje: [.. m.Versions.Select(v => new WersjaPanelu(
                    v.Number, v.CostUsd, v.CreatedAt, v.BasedOn,
                    v.OrphanedAnchors.Count, v.Number == m.CurrentVersion))],
                WersjaBiezaca: m.CurrentVersion,
                OstatniZapis: zapis);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
        {
            // `ProjectStore.Load` opakowuje i uszkodzony JSON, i blad wejscia-wyjscia
            // w `InvalidDataException`. Lapiemy przy KAZDYM projekcie osobno, zeby
            // jeden zly plik nie zabral z soba calej listy.
            return new WierszPanelu(id, "?", "", 0, 0, 0m, 0m, [], 0, zapis, Blad: ex.Message);
        }
    }
}

/// <summary>
/// Polska odmiana rzeczownika po liczbie. Bez tego panel pisze „2 projektów", co przy
/// ekranie ogladanym codziennie kluje w oczy — a jest to jedno wyrazenie, nie problem.
/// </summary>
public static class Odmiana
{
    public static string Projektow(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "projekt",
        (2 or 3 or 4, not (12 or 13 or 14)) => "projekty",
        _ => "projektów",
    };

    public static string Maja(int n) => n % 10 == 1 && n % 100 != 11 ? "ma" : "mają";

    /// „1 osierocona kotwica", „2 osierocone kotwice", „5 osieroconych kotwic".
    /// Przymiotnik odmienia sie razem z rzeczownikiem, wiec zwracamy oba naraz —
    /// sklejanie ich osobno w widoku dawaloby „2 osierocona kotwice".
    public static string OsieroconeKotwice(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "osierocona kotwica",
        (2 or 3 or 4, not (12 or 13 or 14)) => "osierocone kotwice",
        _ => "osieroconych kotwic",
    };
}
