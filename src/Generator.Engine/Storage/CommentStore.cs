using System.Text.Json;
using System.Text.Json.Serialization;
using Generator.Engine.Model;

namespace Generator.Engine.Storage;

/// Uwaga po zapisie: Comment plus to, co dopisal do niej silnik (kontrakt 3.3).
public record StoredComment(
    string Id,
    string? Anchor,
    string Text,
    string Viewport,
    DateTimeOffset CreatedAt,
    CommentStatus Status,
    string? Note);

/// comments.json obok project.json. Klucz to numer wersji, ktorej uwaga dotyczy —
/// uwagi zyja PRZY wersji, bo to ja klient oglada, gdy je pisze.
public class CommentStore(string projectDir)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string Path_ => Path.Combine(projectDir, "comments.json");

    public IReadOnlyList<StoredComment> ForVersion(int version) =>
        Read().TryGetValue(version, out var lista) ? lista : [];

    /// Kontrakt 3.2: Id powstaje raz i nie zmienia sie przy ponowieniu, wiec
    /// ponowne wyslanie tej samej uwagi ma byc bez skutku — a nie ma prawa
    /// skasowac statusu, ktory silnik nadal jej w poprzedniej rundzie.
    public void Upsert(int version, IReadOnlyList<Comment> comments)
    {
        var wszystkie = Read();
        var lista = wszystkie.TryGetValue(version, out var istniejace)
            ? new List<StoredComment>(istniejace)
            : [];

        foreach (var c in comments)
        {
            if (lista.Any(x => x.Id == c.Id)) continue;
            lista.Add(new StoredComment(c.Id, c.Anchor, c.Text, c.Viewport, c.CreatedAt,
                CommentStatus.Open, Note: null));
        }

        wszystkie[version] = lista;
        Write(wszystkie);
    }

    /// Kontrakt 4.4.2: status zmienia WYLACZNIE ta metoda, wylacznie z raportu
    /// silnika. Wpis o Id, ktorego tu nie ma, jest ignorowany — raport powstaje
    /// z tekstu modelu, ktory czytal tresc pisana przez klienta.
    public void ApplyResults(int version, IReadOnlyList<CommentResult> results)
    {
        var wszystkie = Read();
        if (!wszystkie.TryGetValue(version, out var lista)) return;

        wszystkie[version] = [.. lista.Select(k =>
        {
            var r = results.FirstOrDefault(x => x.CommentId == k.Id);
            return r is null ? k : k with { Status = r.Status, Note = r.Note };
        })];
        Write(wszystkie);
    }

    private Dictionary<int, List<StoredComment>> Read()
    {
        if (!File.Exists(Path_)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, List<StoredComment>>>(
                File.ReadAllText(Path_), Options) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"comments.json jest uszkodzony w: {Path_}", ex);
        }
        catch (IOException ex)
        {
            // Symetria z ProjectStore.Load, ktory lapie oba. Bez tego IOException
            // (zablokowany plik, przejsciowy brak dostepu) przelatuje nieopakowany
            // i mija kod wywolujacy, ktory lapie InvalidDataException. Osobny
            // komunikat, bo "nie moge czytac" to nie to samo co "uszkodzony".
            throw new InvalidDataException($"Nie mozna czytac comments.json w: {Path_}", ex);
        }
    }

    /// Ten sam zapis atomowy co ProjectStore.Save: tmp + Move(overwrite). Przerwany
    /// zapis nie moze zostawic polowy pliku tam, gdzie stal komplet uwag klienta.
    private void Write(Dictionary<int, List<StoredComment>> dane)
    {
        var tmp = Path_ + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dane, Options));
        File.Move(tmp, Path_, overwrite: true);
    }
}
