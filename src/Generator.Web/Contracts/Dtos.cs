namespace Generator.Web.Contracts;

/// <param name="BasedOn">
/// Numer wersji, Z KTOREJ ta powstala (null dla pierwszej). Nie `Number - 1`:
/// po powrocie do v1 kolejna runda tworzy v3, ktorej rodzicem jest v1, nie v2.
/// Bez tego pola podswietlanie zmian dawaloby poprawne wyniki w kazdym przypadku
/// liniowym — czyli w kazdym, ktory testujemy — i cicho klamaloby dokladnie tam,
/// gdzie rollback ma sens.
/// </param>
/// <param name="ChangedAnchors">
/// Bloki rozne od rodzica (kontrakt 5.3). Liczy je ten, kto ma snapshoty — silnik,
/// a u nas mock; frontend tylko pokazuje.
/// </param>
/// <param name="CreatedAt">
/// Kontrakt 6.2. `null` dla wersji zapisanych, zanim silnik zaczal stemplowac date —
/// UI ma wtedy pokazac sam numer, nie zmyslona date. Koszt rundy w te liste NIE
/// wchodzi (4.1): zmienia rozmowe z „czy strona jest dobra" na „czy warto bylo wydac".
/// </param>
public record VersionView(
    int Number,
    string PreviewUrl,
    IReadOnlyList<string> OrphanedAnchors,
    int? BasedOn = null,
    IReadOnlyList<string>? ChangedAnchors = null,
    DateTimeOffset? CreatedAt = null);

/// <param name="CurrentVersion">
/// Wersja, ktora klient oglada. Po powrocie do starszej „aktualna" przestaje znaczyc
/// „ostatnia", wiec `Versions[^1]` jest bledem — podglad i ZIP ida ZA tym polem.
/// </param>
public record ProjectView(
    string Id,
    string Token,
    string Status,              // "draft" | "active" | "frozen"
    int RoundsUsed,
    int RoundsLimit,
    IReadOnlyList<VersionView> Versions,
    int CurrentVersion = 0);    // 0 = nie ma jeszcze zadnej wersji

/// <summary>
/// Kształt z kontraktu 3.2. Id powstaje RAZ, w chwili dodania komentarza, i nie zmienia
/// się przy ponowieniu — na tym stoi idempotencja podwójnego „Popraw to" przy 202+jobId.
/// Kontrakt mówi „GUID nadany w przeglądarce"; przy Blazor Server kod komponentu biegnie
/// na serwerze, więc dosłownie to nieprawda — ale stan obwodu jest per karta przeglądarki,
/// więc własność, o którą chodziło, zostaje. Zdanie w §3.2 wymaga korekty.
/// </summary>
public record CommentDto(
    string Id,
    string? Anchor,             // null = komentarz globalny
    string Text,
    string Viewport,            // "desktop" | "mobile"
    DateTimeOffset CreatedAt,
    string Status,              // "open" | "applied" | "rejected"
    string? Note);              // zdanie PRZY komentarzu, nie zbiorcze

public record ProposalView(string Id, string Name, string PreviewHtml);

/// <summary>Kontrakt 4.3: UI rozgałęzia się po Handling. Cause tu nie ma i mieć nie będzie.</summary>
public record JobFailureView(string Handling, int Attempts);

public record JobView(string Id, string Status, JobFailureView? Failure);
