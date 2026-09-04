using System.Text.RegularExpressions;

namespace Generator.Web.Contracts;

/// <summary>
/// Format `data-cmt-id` z kontraktu 3.1, w jednym miejscu. Kotwice przychodza z HTML-a
/// wygenerowanego przez model z danych klienta — sa NIEZAUFANE. Kazde miejsce, ktore
/// wstawia kotwice do wyjscia (CSS podgladu, selektor), musi ja najpierw przepuscic tutaj.
/// </summary>
public static partial class Kotwica
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex Format();

    public static bool Poprawna(string? kotwica) =>
        kotwica is not null && Format().IsMatch(kotwica);
}
