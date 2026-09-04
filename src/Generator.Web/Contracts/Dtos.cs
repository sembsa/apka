namespace Generator.Web.Contracts;

public record VersionView(int Number, string PreviewUrl, IReadOnlyList<string> OrphanedAnchors);

public record ProjectView(
    string Id,
    string Token,
    string Status,              // "draft" | "active" | "frozen"
    int RoundsUsed,
    int RoundsLimit,
    IReadOnlyList<VersionView> Versions);

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
