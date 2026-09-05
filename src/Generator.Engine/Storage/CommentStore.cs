using System.Text.Json;
using System.Text.Json.Serialization;
using Generator.Engine.Model;
using Generator.Engine.IO;

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

    /// Uzgodnij i ApplyResults to obie sekwencje czytaj-zmodyfikuj-zapisz na tym samym
    /// pliku, a CommentStore powstaje na kazde wywolanie (paths.Comments(id) => new),
    /// wiec blokada per egzemplarz nie chronilaby niczego. Klucz to sciezka pliku:
    /// dwa projekty nie blokuja sie nawzajem, a dwa watki tego samego projektu — tak.
    /// Zaobserwowane bez tego: uwaga zapisana i po chwili nadpisana przez raport
    /// konczacej sie rundy. Zdanie „uwagi nie gina" przestawalo byc prawdziwe.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Zamki = new();

    private object Zamek => Zamki.GetOrAdd(Path.GetFullPath(Path_), _ => new object());

    public IReadOnlyList<StoredComment> ForVersion(int version) =>
        Read().TryGetValue(version, out var lista) ? lista : [];

    /// <summary>
    /// UZGADNIA liste uwag `open` danej wersji z lista przychodzaca: to, co przyszlo,
    /// zostaje (albo powstaje), a uwagi `open`, ktorych w niej NIE MA, znikaja z dysku.
    /// Nazwa mowi „uzgodnij", a nie „dopisz", bo kasowanie jest tu czescia kontraktu,
    /// a nie skutkiem ubocznym.
    ///
    /// Dlaczego kasowanie jest bezpieczne — i konieczne. Frontend umie WYCOFAC uwage,
    /// dopoki jest `open` (kontrakt 3.3), ale robil to wylacznie w pamieci obwodu.
    /// Metoda, ktora umiala tylko dopisywac, zostawiala wycofana uwage w comments.json
    /// na zawsze: wracala przy kazdym `Reload`, odblokowywala przycisk „Popraw to"
    /// i wchodzila do nastepnej rundy — czyli klient placil runde za uwage, ktora
    /// sam odwolal. Przychodzaca lista JEST pelna lista uwag `open` tej wersji;
    /// to jedyna rzecz, ktora frontend o nich wie.
    ///
    /// Nietkniete zostaja uwagi `applied` i `rejected`. Te sa wlasnoscia SILNIKA
    /// (kontrakt 4.4.2: status zmienia wylacznie `ApplyResults`, wylacznie z raportu),
    /// stanowia raport „co zrobilismy z Twoimi uwagami" i nie ma ich w liscie
    /// przychodzacej z definicji — frontend wysyla tylko `open`.
    ///
    /// Kontrakt 3.2 zostaje w mocy: Id powstaje raz, wiec ponowne wyslanie tej samej
    /// uwagi jest bez skutku i nie kasuje statusu nadanego przez silnik.
    /// </summary>
    public void Uzgodnij(int version, IReadOnlyList<Comment> comments)
    {
        lock (Zamek)
        {
        var wszystkie = Read();
        var istniejace = wszystkie.TryGetValue(version, out var byly) ? byly : [];
        var przychodzace = comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        // Kolejnosc zostaje: kontrakt 3.2 mowi, ze przy sprzecznych uwagach wygrywa
        // OSTATNIA, wiec przestawienie listy zmienialoby wynik rundy.
        var lista = new List<StoredComment>(
            istniejace.Where(x => x.Status != CommentStatus.Open || przychodzace.Contains(x.Id)));

        foreach (var c in comments)
        {
            if (lista.Any(x => x.Id == c.Id)) continue;
            lista.Add(new StoredComment(c.Id, c.Anchor, c.Text, c.Viewport, c.CreatedAt,
                CommentStatus.Open, Note: null));
        }

        wszystkie[version] = lista;
        Write(wszystkie);
        }
    }

    /// Kontrakt 4.4.2: status zmienia WYLACZNIE ta metoda, wylacznie z raportu
    /// silnika. Wpis o Id, ktorego tu nie ma, jest ignorowany — raport powstaje
    /// z tekstu modelu, ktory czytal tresc pisana przez klienta.
    public void ApplyResults(int version, IReadOnlyList<CommentResult> results)
    {
        lock (Zamek)
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

    /// Ten sam zapis atomowy co ProjectStore.Save — dosłownie ten sam kod (ZapisAtomowy),
    /// nie drugi taki sam. Przerwany zapis nie moze zostawic polowy pliku tam, gdzie
    /// stal komplet uwag klienta.
    private void Write(Dictionary<int, List<StoredComment>> dane) =>
        ZapisAtomowy.Zapisz(Path_, JsonSerializer.Serialize(dane, Options));
}
