using System.Net;
using System.Net.Sockets;

namespace Generator.Engine.Sources;

/// <summary>
/// Adres podaje KLIENT, a pobiera nasz serwer — to jest SSRF, czyli klient uzywa
/// naszego procesu jako sondy do sieci, w ktorej sam nie siedzi. Bez tej polityki
/// „https://169.254.169.254/latest/meta-data/" wladowaloby poswiadczenia maszyny
/// wprost do promptu, a „http://127.0.0.1:5000/panel" — nasz wlasny panel wewnetrzny.
///
/// Dlatego decyzja zapada na ADRESIE IP, nie na nazwie hosta: nazwa moze wskazywac
/// dokadkolwiek (`localtest.me` rozwiazuje sie do 127.0.0.1), a rekord DNS moze sie
/// zmienic miedzy sprawdzeniem a polaczeniem. Wolajacy ma wiec pytac o adres, na
/// ktory FAKTYCZNIE sie laczy — patrz ConnectCallback w PobieraczStrony.
///
/// Czysta funkcja bez sieci, zeby cala tablica przypadkow byla testowalna offline.
/// </summary>
public static class PolitykaAdresu
{
    /// Tylko http i https. `file://`, `ftp://` i `gopher://` to trzy rozne sposoby
    /// na przeczytanie czegos, czego klient czytac nie ma prawa.
    public static bool CzyDozwolonySchemat(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    public static bool CzyDozwolony(IPAddress adres)
    {
        // KLASYCZNE OBEJSCIE: `::ffff:10.0.0.1` to ten sam adres 10.0.0.1 zapisany
        // jako IPv6. Bez odwzorowania w dol kazda regula IPv4 nizej mija sie z celem,
        // bo bajty nie pasuja. Rozpakowujemy i pytamy jeszcze raz.
        if (adres.IsIPv4MappedToIPv6) return CzyDozwolony(adres.MapToIPv4());

        return adres.AddressFamily switch
        {
            AddressFamily.InterNetwork => CzyDozwolonyV4(adres),
            AddressFamily.InterNetworkV6 => CzyDozwolonyV6(adres),
            _ => false,   // nic innego nie wysypuje sie w HTTP; domyslnie odmawiamy
        };
    }

    private static bool CzyDozwolonyV4(IPAddress adres)
    {
        var b = adres.GetAddressBytes();

        return (b[0], b[1]) switch
        {
            (0, _) => false,                       // 0.0.0.0/8 — „ta siec"
            (10, _) => false,                      // 10/8 prywatna
            (127, _) => false,                     // petla zwrotna
            (169, 254) => false,                   // link-local, czyli metadane chmury
            (172, >= 16 and <= 31) => false,       // 172.16/12 prywatna
            (192, 168) => false,                   // 192.168/16 prywatna
            (100, >= 64 and <= 127) => false,      // 100.64/10 CGNAT — tez nie publiczna
            (>= 224, _) => false,                  // multicast, zarezerwowane, broadcast
            _ => true,
        };
    }

    private static bool CzyDozwolonyV6(IPAddress adres)
    {
        if (IPAddress.IsLoopback(adres)) return false;                  // ::1
        if (adres.Equals(IPAddress.IPv6None)) return false;             // ::
        if (adres.IsIPv6LinkLocal || adres.IsIPv6SiteLocal) return false;
        if (adres.IsIPv6Multicast) return false;
        if (adres.IsIPv6UniqueLocal) return false;                      // fc00::/7

        return true;
    }

    /// <summary>
    /// Wybiera pierwszy dozwolony adres z rozwiazania DNS. Rzuca, gdy zaden nie
    /// przechodzi — komunikat ma nie zdradzac, KTORY adres wewnetrzny odrzucilismy
    /// (to sama w sobie informacja o naszej sieci dla kogos, kto sonduje).
    /// </summary>
    public static IPAddress Wybierz(IReadOnlyList<IPAddress> rozwiazane, string host)
    {
        foreach (var a in rozwiazane)
            if (CzyDozwolony(a)) return a;

        throw new AdresNiedozwolonyException(host);
    }
}

/// <summary>
/// Wspolna nadklasa wszystkiego, co moze pojsc nie tak przy pobieraniu strony klienta.
/// Jedna klasa, bo warstwa HTTP odpowiada na wszystkie te przypadki tak samo (422)
/// i klient dostaje ten sam komunikat: „nie udalo sie pobrac strony spod tego adresu".
/// Rozroznienie jest dla LOGU, nie dla klienta.
/// </summary>
public abstract class ZrodloException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Adres wskazuje na siec, ktorej klientowi czytac nie wolno.</summary>
public class AdresNiedozwolonyException(string host)
    : ZrodloException($"adres '{host}' wskazuje na siec niedostepna dla klienta");

/// <summary>Tego sie nie da nawet sprobowac pobrac: zly zapis albo zly schemat.</summary>
public class AdresNiepoprawnyException(string powod) : ZrodloException(powod);

/// <summary>Proba byla, nie wyszla: brak odpowiedzi, blad HTTP, nie-HTML, za duzo.</summary>
public class PobranieNieudaneException(string powod, Exception? inner = null)
    : ZrodloException(powod, inner);
