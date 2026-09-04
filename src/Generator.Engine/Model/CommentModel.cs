namespace Generator.Engine.Model;

public enum CommentStatus { Open, Applied, Rejected }

/// Kształt z kontraktu 3.2 — powstaje w przeglądarce, typy C# idą za nim.
/// anchor == null oznacza komentarz globalny; brak anchora JEST znaczeniem.
public record Comment(
    string Id,
    string? Anchor,
    string Text,
    string Viewport,
    DateTimeOffset CreatedAt);

/// Kontrakt 3.3: status bierzemy z raportu per komentarz, nigdy z prozy modelu.
public record CommentResult(string CommentId, CommentStatus Status, string Note);
