namespace Generator.Web.Contracts;

/// <summary>
/// Tryb podgladu z kontraktu 3.2, w jednym miejscu. Prog zyl kiedys w JS wewnatrz
/// iframe'a i klamal przy kazdym kliknieciu: nieostylowany iframe ma 300px, wiec
/// wszystko szlo jako „mobile", takze z monitora 1920px. Teraz JS podaje sama
/// szerokosc okna, a decyzje podejmuje C# — jedno miejsce, nie dwa progi w dwoch
/// plikach, ktore musialy sie rozjechac.
/// </summary>
public static class Podglad
{
    public const string Desktop = "desktop";
    public const string Mobile = "mobile";

    /// Ponizej tej szerokosci klient jest na telefonie.
    public const int ProgMobile = 768;

    public static string TrybDlaOkna(int szerokoscOkna) =>
        szerokoscOkna > 0 && szerokoscOkna < ProgMobile ? Mobile : Desktop;

    /// Szerokosc, jaka nadajemy podgladowi w trybie telefonu (typowy telefon).
    public const int SzerokoscMobile = 390;
}
