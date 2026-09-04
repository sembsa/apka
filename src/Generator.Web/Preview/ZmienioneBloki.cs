using System.Text;
using AngleSharp;
using AngleSharp.Dom;

namespace Generator.Web.Preview;

/// <summary>
/// Liczy, ktore bloki (`data-cmt-id`) roznia sie miedzy wersja a jej RODZICEM.
/// Kontrakt 5.3. Parsujemy prawdziwym parserem HTML, nie wyrazeniem regularnym:
/// tresc pisze model, wiec bedzie tam wszystko — cudzysłowy, zagniezdzenia, atrybuty
/// w dowolnej kolejnosci. Regex na tym gnije cicho i po czasie.
/// </summary>
public static class ZmienioneBloki
{
    private static readonly IBrowsingContext Kontekst =
        BrowsingContext.New(Configuration.Default);

    /// <param name="htmlRodzica">null = pierwsza wersja, nie ma z czym porownac.</param>
    public static async Task<IReadOnlyList<string>> Policz(string? htmlRodzica, string htmlWersji)
    {
        if (htmlRodzica is null) return [];

        var rodzic = await Bloki(htmlRodzica);
        var wersja = await Bloki(htmlWersji);

        // Bloki znikniete NIE trafiaja tutaj — to `orphanedAnchors` (3.4) i inny
        // komunikat dla klienta. Tu jest tylko „to sie zmienilo".
        return [.. wersja
            .Where(b => !rodzic.TryGetValue(b.Key, out var przed) || przed != b.Value)
            .Select(b => b.Key)
            .Order(StringComparer.Ordinal)];
    }

    private static async Task<Dictionary<string, string>> Bloki(string html)
    {
        var dokument = await Kontekst.OpenAsync(req => req.Content(html));
        var wynik = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in dokument.QuerySelectorAll("[data-cmt-id]"))
        {
            var id = element.GetAttribute("data-cmt-id");
            if (string.IsNullOrEmpty(id)) continue;

            // Ostatni wygrywa — duplikat id to blad generatora, nie powod do wyjatku
            // przy ogladaniu wlasnej strony.
            wynik[id] = Znormalizuj(element);
        }

        return wynik;
    }

    /// <summary>
    /// Postac kanoniczna bloku: nazwa elementu, atrybuty uporzadkowane po nazwie,
    /// zwiniete biale znaki, rekurencyjnie po dzieciach. Dzieki temu samo
    /// przeformatowanie nie jest zmiana, ale zmiana `style` przy identycznym tekscie
    /// juz tak — a to najczestsza prosba w tym produkcie („powieksz telefon").
    /// </summary>
    private static string Znormalizuj(IElement element)
    {
        var sb = new StringBuilder();
        Wypisz(element, sb);
        return sb.ToString();
    }

    private static void Wypisz(INode wezel, StringBuilder sb)
    {
        switch (wezel)
        {
            case IText tekst:
                var zwiniety = string.Join(' ', tekst.Data.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (zwiniety.Length > 0) sb.Append(zwiniety).Append(' ');
                break;

            case IElement el:
                sb.Append('<').Append(el.LocalName);
                foreach (var atrybut in el.Attributes
                             .OrderBy(a => a.Name, StringComparer.Ordinal))
                {
                    sb.Append(' ').Append(atrybut.Name).Append('=')
                      .Append('"').Append(atrybut.Value.Trim()).Append('"');
                }
                sb.Append('>');

                foreach (var dziecko in el.ChildNodes) Wypisz(dziecko, sb);

                sb.Append("</").Append(el.LocalName).Append('>');
                break;

                // Komentarze HTML pomijamy: mock wpisuje w snapshot „uwzgledniono: …",
                // co zmienialoby sie w kazdej wersji i podswietlalo cala strone.
        }
    }
}
