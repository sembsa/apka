using Generator.Engine.Model;
using Generator.Web.Api;

namespace Generator.Web.Panel;

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
    int Wersji,
    int WersjaBiezaca,
    DateTime OstatniZapis,
    string? Blad = null)
{
    /// <summary>
    /// Czy „0.00 USD" znaczy „tanio", czy „nie mamy pomiaru". Silnik zapisuje koszt
    /// dopiero wtedy, gdy `claude` odda JSON — a na Windowsie dzis nie oddaje go
    /// wcale (awaria B). Projekt z wersjami i zerowym kosztem to wiec nie jest
    /// projekt darmowy, tylko projekt niezmierzony, i panel ma to mowic wprost.
    /// Bez tego rozroznienia podsumowanie „lacznie 0,00 USD" czyta sie jak dobra
    /// wiadomosc, a jest brakiem danych.
    /// </summary>
    public bool KosztZmierzony => Wydano > 0m;

    /// Projekt zamrozony ALBO na granicy — to sa wiersze, na ktore patrzymy najpierw.
    public bool WymagaUwagi =>
        Blad is not null || Status == "frozen" || RundyZuzyte >= RundyLimit || Wydano >= Budzet;
}

/// <param name="BezPomiaru">Ile projektow ma wersje, ale zerowy koszt — patrz KosztZmierzony.</param>
public record PodsumowaniePanelu(int Projektow, decimal Wydano, decimal Budzet, int BezPomiaru);

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

        return Directory.EnumerateDirectories(paths.Root)
            .Select(d => Path.GetFileName(d))
            .Where(id => !string.IsNullOrEmpty(id) && paths.Exists(id))
            .Select(Wiersz)
            .OrderByDescending(w => w.OstatniZapis)
            .ToList();
    }

    public static PodsumowaniePanelu Podsumuj(IReadOnlyList<WierszPanelu> wiersze) => new(
        Projektow: wiersze.Count,
        Wydano: wiersze.Sum(w => w.Wydano),
        Budzet: wiersze.Sum(w => w.Budzet),
        BezPomiaru: wiersze.Count(w => w.Blad is null && w.Wersji > 0 && !w.KosztZmierzony));

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
                Wersji: m.Versions.Count,
                WersjaBiezaca: m.CurrentVersion,
                OstatniZapis: zapis);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
        {
            // `ProjectStore.Load` opakowuje i uszkodzony JSON, i blad wejscia-wyjscia
            // w `InvalidDataException`. Lapiemy przy KAZDYM projekcie osobno, zeby
            // jeden zly plik nie zabral z soba calej listy.
            return new WierszPanelu(id, "?", "", 0, 0, 0m, 0m, 0, 0, zapis, Blad: ex.Message);
        }
    }
}
