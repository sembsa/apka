using System.Security.Cryptography;
using System.Text;

namespace Generator.Web.Panel;

/// <summary>
/// Klucz do panelu. Jedyne miejsce w aplikacji, gdzie porownujemy sekret, wiec
/// jedyne, ktore ma prawo wiedziec, jak sie to robi.
///
/// Panel bez ustawionego `Admin:Token` NIE ISTNIEJE — trasy nie ma, adres oddaje 404.
/// Nie „formularz logowania", tylko brak trasy: panel wypisuje dane osobowe klientow
/// (opisy zawieraja adresy i telefony) oraz koszty, ktorych kontrakt 4.1 zabrania
/// pokazywac klientowi. Uruchomienie „z pudelka" nie ma prawa wystawic tego swiatu
/// dlatego, ze ktos nie doczytal README.
/// </summary>
public static class KluczPanelu
{
    public const string Klucz = "Admin:Token";

    /// <summary>
    /// Skonfigurowany klucz albo `null`, gdy panelu ma nie byc. Pusty i sam bialy
    /// znak licza sie jako BRAK: `"Token": ""` w appsettings to realna pomylka, a przy
    /// zwyklym porownaniu wpuscilaby kazdego, kto wysle puste pole formularza.
    /// </summary>
    public static string? Ustawiony(IConfiguration konfiguracja)
    {
        var token = konfiguracja[Klucz];
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>
    /// Porownanie w stalym czasie. `==` na stringach konczy sie na pierwszej roznej
    /// literze, co przy zdalnym pomiarze czasu pozwala zgadywac klucz znak po znaku.
    /// Tanie zabezpieczenie, a to jedyny sekret w tej aplikacji, ktory otwiera
    /// wszystkie projekty naraz.
    /// </summary>
    public static bool Zgadza(string oczekiwany, string? podany) =>
        !string.IsNullOrEmpty(podany) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(oczekiwany), Encoding.UTF8.GetBytes(podany));
}
