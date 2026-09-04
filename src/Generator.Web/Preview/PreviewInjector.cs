namespace Generator.Web.Preview;

/// <summary>
/// Dokleja skrypt zbierający kliknięcia PRZY SERWOWANIU podglądu.
/// Snapshot na dysku i ZIP klienta zostają czyste (plan produktu, decyzja 4:
/// prosta strona ma być prosta również w środku).
/// </summary>
public static class PreviewInjector
{
    public static string Inject(string html, string scriptUrl)
    {
        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase)) return html;

        var tag = $"""<script src="{scriptUrl}"></script>""";
        var i = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? html.Insert(i, tag) : html + tag;
    }
}
