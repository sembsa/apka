using System.Text.RegularExpressions;

namespace Generator.Engine.Gates;

/// Bramka MIEKKA z sekcji 5.2 planu. Po wyczerpaniu prob wersja jest dostarczana
/// z lista osieroconych (kontrakt 3.4), nie odrzucana.
public static partial class AnchorExtractor
{
    /// Jak w RenderValidator: zakomentowany blok nie powinien liczyc sie jako zywy
    /// anchor tej wersji.
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// Format WARTOSCI id wprost z kontraktu 3.1: [a-z][a-z0-9-]{0,39}. Regex go
    /// CELOWO egzekwuje — id niezgodne z formatem licza sie jako brakujace (osierocone),
    /// to nie jest blad do naprawienia.
    ///
    /// Nazwa ATRYBUTU to co innego niz format wartosci: to stala, ktora przegladarka
    /// normalizuje przy parsowaniu HTML (DOM traktuje "data-cmt-id" i "DATA-CMT-ID" jako
    /// ten sam atrybut), wiec dopasowujemy ja bez wzgledu na wielkosc liter — (?i:...) tylko
    /// wokol nazwy atrybutu, capture group z id zostaje case-sensitive. Bez tego strona,
    /// ktora renderuje sie i dziala poprawnie w przegladarce, wygenerowalaby dla klienta
    /// falszywy wpis w orphanedAnchors (kontrakt 3.4: ta lista jest widoczna w UI) tylko
    /// dlatego, ze model przypadkiem uzyl innej wielkosci liter w nazwie atrybutu.
    [GeneratedRegex("""(?i:data-cmt-id)\s*=\s*["']([a-z][a-z0-9-]{0,39})["']""")]
    private static partial Regex AnchorRegex();

    public static IReadOnlySet<string> Extract(string dir)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return ids;

        foreach (var file in Directory.EnumerateFiles(dir, "*.html", SearchOption.AllDirectories))
        {
            var html = CommentRegex().Replace(File.ReadAllText(file), " ");
            foreach (Match match in AnchorRegex().Matches(html))
                ids.Add(match.Groups[1].Value);
        }

        return ids;
    }

    /// Osierocone = obecne w poprzedniej wersji, nieobecne w tej (kontrakt 3.4).
    /// Pierwsza wersja nie ma poprzednika, wiec pusta lista wejsciowa daje pusta wyjsciowa.
    public static IReadOnlyList<string> Orphaned(IEnumerable<string> previous, IReadOnlySet<string> current) =>
        previous.Where(id => !current.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
}
