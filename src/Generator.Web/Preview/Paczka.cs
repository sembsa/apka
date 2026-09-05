using System.IO.Compression;

namespace Generator.Web.Preview;

/// <summary>
/// ZIP ze snapshotem wersji — to, co klient dostaje „na wlasnosc" (decyzja 2).
///
/// Jedno miejsce, bo sa DWIE drogi do tej samej paczki: `/api/.../zip` dla klienta
/// spoza naszego UI (z tokenem) i `/pobierz/...` dla przycisku w edytorze (z linku
/// nie da sie wyslac naglowka `X-Project-Token`, wiec tamta trasa jest dla przegladarki
/// nieosiagalna). Dwie kopie tej petli rozjechalyby sie na pierwszej poprawce, a
/// poprawka, ktora tu siedzi, jest niewidoczna golym okiem — patrz nizej.
/// </summary>
public static class Paczka
{
    /// <summary>
    /// Buduje archiwum W PAMIECI i oddaje je przewiniete na poczatek. Do pamieci, nie do
    /// pliku tymczasowego: strona z decyzji 4 to kilka plikow tekstowych, a plik
    /// tymczasowy trzeba by sprzatac takze wtedy, gdy klient zerwie polaczenie w polowie
    /// pobierania. Cale archiwum powstaje PRZED wyslaniem pierwszego bajtu — inaczej
    /// wyjatek w polowie budowania wyladowalby w srodku odpowiedzi 200 i klient
    /// dostalby uciety plik bez sladu bledu.
    /// </summary>
    public static MemoryStream Zbuduj(string katalog)
    {
        var bufor = new MemoryStream();
        using (var zip = new ZipArchive(bufor, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var plik in Directory.EnumerateFiles(katalog, "*", SearchOption.AllDirectories))
            {
                // Nazwy wpisow ZAWSZE z ukosnikiem w przod. `GetRelativePath` oddaje
                // separator SYSTEMU, wiec ZIP zbudowany na Windows (a obie nasze maszyny
                // to Windows) mialby wpis „css\styl.css" — po rozpakowaniu jeden plik
                // o dziwnej nazwie zamiast katalogu `css`, i strona klienta bez stylu.
                var wpis = Path.GetRelativePath(katalog, plik)
                    .Replace(Path.DirectorySeparatorChar, '/');
                zip.CreateEntryFromFile(plik, wpis);
            }
        }
        bufor.Position = 0;
        return bufor;
    }

    /// Nazwa pliku, ktora klient zobaczy w „Pobrane". Jedna dla obu tras.
    public static string Nazwa(int wersja) => $"strona-v{wersja}.zip";
}
