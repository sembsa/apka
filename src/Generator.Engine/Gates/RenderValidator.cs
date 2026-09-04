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

    // "(?:^|[\s<])" wymaga, zeby "href"/"src" bylo SAMODZIELNYM atrybutem (poprzedzonym
    // biala spacja albo poczatkiem tagu), a nie sufiksem innej nazwy — bez tego regex
    // lapal tez "data-src"/"data-href" (standard przy leniwym ladowaniu obrazkow), przez
    // co bramka sprawdzalaby plik, ktorego model swiadomie jeszcze nie dolaczyl.
    [GeneratedRegex("""(?:^|[\s<])(?:href|src)\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    /// Skladnia schematu URI z RFC 3986 (scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )).
    /// Zamiast wyliczac schematy po jednym (mailto/tel/data — a model potrafi wypisac
    /// "javascript:", "sms:", "about:", "blob:" i cokolwiek jeszcze), rozpoznajemy KAZDY
    /// schemat URI naraz. To wprost wymagane zamiast listy prefiksow.
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemeRegex();

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

            // Na Windows "\x" albo "C:\x" przeszlyby przez TrimStart('/') nietkniete —
            // to sciezki zakorzenione we WLASNEJ konwencji tamtej platformy (backslash,
            // litera dysku), ktorych TrimStart('/') nie zdejmuje. Path.Combine z drugim
            // argumentem zakorzenionym ODRZUCA pierwszy argument (dokladnie ten sam blad,
            // ktory juz raz naprawilismy dla wiodacego "/" na Unix) — wiec zamiast skladac
            // sciezke, zglaszamy to jako problem. Path.IsPathRooted jest CELOWO zalezne od
            // platformy uruchomieniowej — to wlasnie dzieki temu dziala poprawnie na
            // maszynie wspoltworcy z Windows. Dodatkowe jawne StartsWith('\\') jest
            // niezalezne od platformy: "C:\x" i tak zostanie zlapane wczesniej przez
            // SchemeRegex w IsExternal (litera + dwukropek to poprawna skladnia schematu
            // URI), wiec jedyny przypadek, ktory realnie dociera az tutaj, to sam wiodacy
            // backslash — a ten sprawdzamy jawnie, zeby fix byl mutation-testowalny takze
            // na Unix (Path.IsPathRooted("\x") zwraca tu false z definicji platformy).
            if (Path.IsPathRooted(relative) || relative.StartsWith('\\'))
            {
                problems.Add($"link wskazuje na sciezke zakorzeniona: {local}");
                continue;
            }

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
        SchemeRegex().IsMatch(target);
}
