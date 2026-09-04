using System.Text.RegularExpressions;

namespace Generator.Engine.Gates;

public record RenderCheck(bool Ok, IReadOnlyList<string> Problems);

/// Bramka TWARDA z sekcji 5.2 planu. Bez parsera HTML (zero pakietow NuGet w Engine) —
/// "renderuje sie" definiujemy sprawdzalnie: index.html istnieje NA KORZENIU katalogu
/// wersji (to wlasnie odda serwer statyczny pod "/" — index.html schowany w podkatalogu
/// nie wystarcza), jest niepusty, zawiera <html i </html>, a kazdy lokalny href/src
/// wskazuje istniejacy plik WEWNATRZ tego katalogu. Wersja, ktora tego nie przejdzie,
/// nie dochodzi do klienta i nie zostaje snapshotem (kontrakt 4.3/4.4).
public static partial class RenderValidator
{
    /// Usuwamy komentarze HTML przed obiema sprawdzanymi wlasciwosciami: zakomentowany
    /// zepsuty link nie powinien falszywie wywalac dzialajacej strony, a zakomentowany
    /// <html>/</html> nie powinien falszywie akceptowac strony bez prawdziwych znacznikow.
    /// Regex na komentarz to wciaz nie parser HTML — jedna prosta zamiana z góry.
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex("""(?:href|src)\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    public static RenderCheck Check(string dir)
    {
        var problems = new List<string>();
        var indexPath = Path.Combine(dir, "index.html");

        if (!File.Exists(indexPath))
            return new RenderCheck(false, ["brak index.html"]);

        var raw = File.ReadAllText(indexPath);
        if (string.IsNullOrWhiteSpace(raw))
            return new RenderCheck(false, ["index.html jest pusty"]);

        var html = CommentRegex().Replace(raw, " ");

        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase))
            problems.Add("index.html nie zawiera <html");
        if (!html.Contains("</html>", StringComparison.OrdinalIgnoreCase))
            problems.Add("index.html nie zawiera </html>");

        // TrimEndingDirectorySeparator: Path.GetFullPath zachowuje koncowy separator, jesli
        // byl w "dir" (np. wywolanie z "dir/"). Bez przycięcia siteRoot konczylby sie na "/",
        // "siteRoot + separator" mialby PODWOJNY separator i IsInsideSiteRoot odrzucalby
        // KAZDY prawidlowy lokalny link jako "poza katalogiem wersji".
        var siteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

        foreach (Match match in LinkRegex().Matches(html))
        {
            var target = match.Groups[1].Value.Trim();
            if (IsExternal(target)) continue;

            var local = target.Split('?', '#')[0];
            if (local.Length == 0) continue;

            // "/style.css" adresuje korzen STRONY (siteRoot), nie korzen dysku hosta —
            // bez TrimStart Path.Combine z wiodacym "/" zwrocilby sciezke bezwzgledna
            // i zignorowal siteRoot (tak dziala Path.Combine na Unix), przez co realnie
            // istniejacy plik strony wygladalby jak brakujacy.
            var relative = local.TrimStart('/');
            var resolved = Path.GetFullPath(Path.Combine(siteRoot, relative));

            // "../poza-katalog/x.css" moze wskazywac na plik, ktory istnieje na dysku
            // dewelopera, ale nie wejdzie do snapshotu/ZIP-a tej wersji (snapshotem jest
            // caly siteRoot, nic poza nim). Takie odwolanie traktujemy jako problem, a nie
            // jako "plik istnieje" — inaczej bramka przepuscilaby strone, ktora u klienta
            // i tak sie nie wyrenderuje.
            if (!IsInsideSiteRoot(resolved, siteRoot))
            {
                problems.Add($"link wychodzi poza katalog wersji: {local}");
                continue;
            }

            // href="/" , href="./" , href="galeria/" adresuja KATALOG, nie plik — serwer
            // statyczny serwuje dla nich "<katalog>/index.html". Bez tego kroku kazdy taki,
            // zupelnie normalny w generowanym HTML-u link zglaszalby "nieistniejacy plik"
            // (File.Exists na katalogu zwraca false), a bramka twarda odrzucalaby wersje,
            // ktora u klienta renderuje sie poprawnie.
            if (Directory.Exists(resolved))
                resolved = Path.Combine(resolved, "index.html");

            if (!File.Exists(resolved))
                problems.Add($"link wskazuje nieistniejacy plik: {local}");
        }

        return new RenderCheck(problems.Count == 0, problems);
    }

    private static bool IsInsideSiteRoot(string resolved, string siteRoot) =>
        resolved == siteRoot ||
        resolved.StartsWith(siteRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsExternal(string target) =>
        target.StartsWith('#') ||
        target.StartsWith("//") ||
        target.Contains("://") ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
}
