using System.Text;
using AngleSharp;
using AngleSharp.Dom;

namespace Generator.Engine.Sources;

/// <summary>
/// Zamienia HTML obecnej strony klienta w tekst, ktory ma sens w promptcie. Nie chodzi
/// o „goly tekst": model ma odtworzyc FAKTY (nazwa, adres, telefon, oferta, godziny)
/// i zobaczyc STRUKTURE (co bylo naglowkiem, co lista), a nie dostac zupe slow, w ktorej
/// „Kontakt" i numer telefonu nie maja ze soba nic wspolnego.
///
/// Prawdziwy parser, nie regex — z tego samego powodu co w ZmienioneBloki, tylko
/// mocniejszego: to HTML CUDZY, pisany latami przez nieznane narzedzia.
///
/// Wyrzucamy `script`, `style`, `noscript` i komentarze. NIE wyrzucamy stopki ani
/// naglowka nawigacji: w stronach malych firm to wlasnie tam siedzi adres, telefon
/// i godziny otwarcia — czyli material najcenniejszy.
/// </summary>
public static class EkstraktorTresci
{
    /// Gorna granica tego, co wchodzi do promptu. Duza strona firmowa potrafi miec
    /// 200 kB tekstu; caly nie jest potrzebny, a placimy za kazdy token. 12 tys. znakow
    /// to z grubsza strona-wizytowka w calosci albo czolowka wiekszego serwisu.
    public const int MaxZnakow = 12_000;

    private static readonly IBrowsingContext Kontekst =
        BrowsingContext.New(Configuration.Default);

    private static readonly string[] DoWyrzucenia = ["script", "style", "noscript", "template", "svg"];

    public static async Task<string> Wyciagnij(string html)
    {
        var dokument = await Kontekst.OpenAsync(r => r.Content(html));

        foreach (var nazwa in DoWyrzucenia)
            foreach (var element in dokument.QuerySelectorAll(nazwa).ToList())
                element.Remove();

        var sb = new StringBuilder();

        var tytul = Jednolinijkowo(dokument.Title ?? "");
        if (tytul.Length > 0) sb.AppendLine($"TYTUL: {tytul}");

        var opis = dokument.QuerySelector("meta[name=description]")?.GetAttribute("content");
        var opisJedno = Jednolinijkowo(opis ?? "");
        if (opisJedno.Length > 0) sb.AppendLine($"OPIS: {opisJedno}");

        // `body` bywa null przy skrajnie zepsutym wejsciu — wtedy zostaje sam tytul.
        foreach (var korzen in Korzenie(dokument)) Przejdz(korzen, sb);

        var wynik = sb.ToString().Trim();
        return wynik.Length <= MaxZnakow ? wynik : wynik[..MaxZnakow];
    }

    /// <summary>
    /// Od czego zaczac chodzenie po drzewie. Zmierzone na zywej stronie: dla
    /// pl.wikipedia.org caly limit 12 tys. znakow schodzil na menu („Przejdz do
    /// zawartosci", „Losuj artykul", „Portal wikipedystow"), zanim ekstraktor dotarl
    /// do jednego zdania o miescie. Strona z `main`/`article` sama mowi, gdzie ma
    /// tresc — korzystamy z tego.
    ///
    /// Stopka dolacza ZAWSZE, takze wtedy: to tam siedzi adres, telefon i godziny.
    /// Strona bez `main` (czyli typowa strona malej firmy, o ktora w tym trybie chodzi)
    /// idzie po calym `body` jak dotad — tam nawigacja jest krotka i tez cos znaczy.
    /// </summary>
    private static IEnumerable<IElement> Korzenie(IDocument dokument)
    {
        var glowna = dokument.QuerySelector("main") ?? dokument.QuerySelector("article");
        if (glowna is null)
            return dokument.Body is null ? [] : [dokument.Body];

        return [glowna, .. dokument.QuerySelectorAll("footer").Where(f => !glowna.Contains(f))];
    }

    /// <summary>
    /// Jedno przejscie po drzewie zamiast selektorow per typ elementu: kolejnosc na
    /// stronie NIESIE ZNACZENIE („Oferta", potem jej lista), a zbieranie osobno
    /// wszystkich h2 i osobno wszystkich li rozsypuje ja bezpowrotnie.
    /// </summary>
    private static void Przejdz(IElement element, StringBuilder sb)
    {
        switch (element.LocalName)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                Dopisz(sb, $"{element.LocalName.ToUpperInvariant()}: {Jednolinijkowo(element.TextContent)}");
                return;

            case "li":
                Dopisz(sb, $"- {Jednolinijkowo(element.TextContent)}");
                return;

            case "p" or "td" or "th" or "figcaption" or "blockquote" or "address":
                Dopisz(sb, Jednolinijkowo(element.TextContent));
                return;

            case "br":
                return;
        }

        // Element bez wlasnej roli tekstowej: schodzimy nizej. Tekst lezacy luzem
        // miedzy dziecmi (czeste w starym HTML-u: `<div>Tel. 600 100 200<br>...`)
        // przepadlby, gdybysmy patrzyli wylacznie na elementy.
        foreach (var dziecko in element.ChildNodes)
        {
            if (dziecko is IElement e) Przejdz(e, sb);
            else if (dziecko.NodeType == NodeType.Text) Dopisz(sb, Jednolinijkowo(dziecko.TextContent));
        }
    }

    private static void Dopisz(StringBuilder sb, string linia)
    {
        if (linia.Length > 0) sb.AppendLine(linia);
    }

    private static string Jednolinijkowo(string tekst) =>
        Prompts.PromptBuilder.NormalizeWhitespace(tekst.Replace(' ', ' '));
}
