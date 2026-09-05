using Generator.Engine.IO;
using Xunit;

namespace Generator.Engine.Tests;

public class DirectoryOpsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gen-dirops-").FullName;

    public void Dispose()
    {
        // Plik bez uprawnien odczytu bywa niekasowalny bez przywrocenia trybu -
        // przywracamy wszystko na 0700, zeby recursive delete nie padlo w Dispose.
        if (Directory.Exists(_root))
        {
            if (!OperatingSystem.IsWindows())
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SafeReplace_gdy_kopiowanie_pada_w_polowie_zostawia_cel_nietkniety()
    {
        // Punkt 5 raportu: naiwna kolejnosc "skasuj cel, potem kopiuj" zamienia
        // awarie w polowie kopiowania w utrate JEDYNEJ kopii (cel przestaje istniec
        // w jakiejkolwiek wersji). SafeReplace kopiuje do katalogu tymczasowego
        // OBOK celu i podmienia dopiero po pelnym sukcesie — cel musi przetrwac
        // nietkniety, gdy Copy() rzuci w polowie.
        if (OperatingSystem.IsWindows())
            return; // chmod/UnixFileMode nie ma odpowiednika w tym tescie na Windows

        var from = Path.Combine(_root, "from");
        var to = Path.Combine(_root, "to");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);

        // "from" ma dwa pliki; drugi jest nieczytelny, wiec Copy() rzuci w POLOWIE
        // (po skopiowaniu pierwszego), a nie od razu na starcie.
        File.WriteAllText(Path.Combine(from, "a-czytelny.txt"), "a");
        var unreadable = Path.Combine(from, "b-nieczytelny.txt");
        File.WriteAllText(unreadable, "b");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);

        // "to" ma oryginalna zawartosc, ktora NIE MOZE zniknac, jesli kopiowanie pada.
        File.WriteAllText(Path.Combine(to, "oryginal.txt"), "jedyna kopia jaka mamy");

        Assert.ThrowsAny<UnauthorizedAccessException>(() => DirectoryOps.SafeReplace(from, to));

        Assert.True(Directory.Exists(to), "cel zniknal po nieudanej probie kopiowania");
        Assert.True(File.Exists(Path.Combine(to, "oryginal.txt")),
            "oryginalna zawartosc celu przepadla po nieudanej probie kopiowania");
        Assert.Equal("jedyna kopia jaka mamy", File.ReadAllText(Path.Combine(to, "oryginal.txt")));

        // Nieudana proba nie moze zostawiac smiecia obok "to" — katalog tymczasowy
        // ".staging-*" (kopiowany PRZED podmiana) musi zostac posprzatany, gdy Copy()
        // rzuci w polowie, tak samo jak przy naiwnym "skasuj-potem-kopiuj" cel po
        // prostu by nie istnial (nic by nie zostawil).
        var leftovers = Directory.EnumerateDirectories(_root)
            .Where(d => Path.GetFileName(d).Contains(".staging-") || Path.GetFileName(d).Contains(".old-"))
            .ToList();
        Assert.Empty(leftovers);

        File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void SafeReplace_po_sukcesie_cel_ma_dokladnie_zawartosc_zrodla()
    {
        var from = Path.Combine(_root, "from");
        var to = Path.Combine(_root, "to");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "nowy.txt"), "nowa tresc");
        File.WriteAllText(Path.Combine(to, "stary.txt"), "ma zniknac");

        DirectoryOps.SafeReplace(from, to);

        Assert.True(File.Exists(Path.Combine(to, "nowy.txt")));
        Assert.False(File.Exists(Path.Combine(to, "stary.txt")));
    }
}
