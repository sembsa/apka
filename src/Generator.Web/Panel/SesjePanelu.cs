using System.Collections.Concurrent;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Generator.Web.Panel;

/// <summary>
/// Kto jest wpuszczony do panelu i jak dlugo. Jedyne miejsce, ktore wie, czym jest
/// wazna sesja — `Program.cs` pyta o tak/nie i nie zna ani zycia sesji, ani postaci
/// identyfikatora.
///
/// Po co w ogole sesja: bez niej lista projektow istnieje WYLACZNIE jako odpowiedz
/// na POST z kluczem. F5 znaczy wtedy „wyslac formularz ponownie?", a klikniecie
/// w podglad albo edytor wyprowadza z panelu bezpowrotnie — Wstecz laduje na stronie
/// POST-a. Klucz trzeba bylo wklejac po kazdym takim wyjsciu.
///
/// W ciastku ladnie SAM IDENTYFIKATOR, nigdy `Admin:Token`. To jest cala roznica
/// miedzy „mozna uniewaznic" a „sekret otwierajacy dane wszystkich klientow lezy
/// na dysku przegladarki i jedzie w kazdym zadaniu".
///
/// Stan w pamieci procesu jest tu wlasciwy, a nie tymczasowy: panel chodzi lokalnie,
/// a restart aplikacji ma wylogowywac. Gdyby kiedys stanal drugi proces, kazdy mialby
/// wlasne sesje — i to jest moment, w ktorym ta klasa wymaga decyzji, nie wczesniej.
/// </summary>
public class SesjePanelu(TimeProvider czas)
{
    /// Liczone od OSTATNIEGO uzycia, nie od utworzenia (patrz `Wazna`). Dwanascie
    /// godzin, bo panel nie ma prawa wylogowac czlowieka w srodku pracy.
    public static readonly TimeSpan Zycie = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _wazneDo = new();

    public SesjePanelu() : this(TimeProvider.System) { }

    /// <summary>
    /// Nowa sesja. Identyfikator to 256 bitow z generatora kryptograficznego —
    /// jest sekretem samym w sobie, wiec nie moze byc ani przewidywalny, ani krotki.
    /// </summary>
    public string Utworz()
    {
        // Sprzatanie tutaj, a nie w tle: sesji sa jednostki, a osobny timer trzeba
        // by bylo zatrzymywac razem z aplikacja. Wejscie do panelu jest wystarczajaco
        // rzadkie, zeby przejscie po slowniku bylo niezauwazalne.
        var teraz = czas.GetUtcNow();
        foreach (var (id, wazneDo) in _wazneDo)
            if (wazneDo <= teraz)
                _wazneDo.TryRemove(id, out _);

        var nowy = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _wazneDo[nowy] = teraz + Zycie;
        return nowy;
    }

    /// <summary>
    /// Czy ta sesja wpuszcza — i jesli tak, to odswieza jej termin. Waznosc liczy sie
    /// od ostatniego uzycia: inaczej panel wyrzucalby po dwunastu godzinach kogos,
    /// kto siedzi w nim caly dzien i wlasnie klikal.
    /// </summary>
    public bool Wazna(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_wazneDo.TryGetValue(id, out var wazneDo)) return false;

        var teraz = czas.GetUtcNow();
        if (wazneDo <= teraz)
        {
            _wazneDo.TryRemove(id, out _);
            return false;
        }

        _wazneDo[id] = teraz + Zycie;
        return true;
    }

    /// <summary>
    /// Wylogowanie. Kasujemy sesje PO STRONIE SERWERA, bo samo skasowanie ciastka
    /// w przegladarce niczego nie zamyka komus, kto zdazyl je skopiowac.
    /// </summary>
    public void Zakoncz(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _wazneDo.TryRemove(id, out _);
    }
}
