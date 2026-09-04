using Generator.Web.Contracts;

namespace Generator.Web.Preview;

/// <summary>
/// Dokleja skrypt zbierający kliknięcia PRZY SERWOWANIU podglądu.
/// Snapshot na dysku i ZIP klienta zostają czyste (plan produktu, decyzja 4:
/// prosta strona ma być prosta również w środku).
/// </summary>
public static class PreviewInjector
{
    /// <param name="podswietl">
    /// Kotwice blokow zmienionych wzgledem rodzica (kontrakt 5.3). Niezaufane —
    /// pochodza z HTML-a wygenerowanego przez model — wiec kazda musi przejsc
    /// `Kotwica.Poprawna` przed wstawieniem do CSS. Bez tego selektor jest dziura,
    /// przez ktora tresc strony wstrzykuje wlasny arkusz.
    /// </param>
    public static string Inject(
        string html, string scriptUrl, IReadOnlyList<string>? podswietl = null)
    {
        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase)) return html;

        var doklejka = Styl(podswietl) + $"""<script src="{scriptUrl}"></script>""";
        var i = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? html.Insert(i, doklejka) : html + doklejka;
    }

    private static string Styl(IReadOnlyList<string>? podswietl)
    {
        var kotwice = (podswietl ?? []).Where(Kotwica.Poprawna).Distinct(StringComparer.Ordinal).ToList();
        if (kotwice.Count == 0) return string.Empty;

        var selektory = string.Join(",", kotwice.Select(k => $"[data-cmt-id=\"{k}\"]"));

        // `outline`, nie `border`: nie zmienia rozmiarow, wiec podswietlenie nie
        // przestawia ukladu strony, ktory klient wlasnie oglada.
        return "<style data-cmt-podswietlenie>"
            + selektory + "{outline:3px solid #c2410c;outline-offset:3px}"
            + "</style>";
    }
}
