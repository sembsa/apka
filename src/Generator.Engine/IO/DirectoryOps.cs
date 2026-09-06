namespace Generator.Engine.IO;

/// Operacje na katalogach wspoldzielone miedzy VersionStore (snapshoty) i
/// GenerationService (kopia zapasowa/przywracanie WorkDir podczas naprawy kotwic
/// i po nieudanej rundzie). Trzymane w JEDNYM miejscu celowo: obie strony musza
/// zgadzac sie CO wchodzi do kopii (jakie pliki, jakie podkatalogi) — rozjazd
/// (np. jedna strona zaczyna pomijac ukryte pliki albo linki symboliczne) zepsulby
/// snapshot i kopie zapasowa niezaleznie od siebie, w rozny sposob, bez ostrzezenia
/// (patrz raport, punkt 9).
public static class DirectoryOps
{
    /// Prosta rekurencyjna kopia: tworzy "to", kopiuje pliki i podkatalogi z "from".
    /// Brak "from" daje pusty (ale istniejacy) katalog docelowy — celowe: WorkDir
    /// moze jeszcze nie istniec przy pierwszym Commit projektu.
    public static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;

        foreach (var file in Directory.EnumerateFiles(from))
        {
            var cel = Path.Combine(to, Path.GetFileName(file));
            // Ponawiane z tego samego powodu co podmiana w ZapisAtomowy: na Windows
            // swiezo zamkniety plik potrafi byc przez chwile zajety (antywirus,
            // indeksator), a `File.Copy` konczy sie wtedy `IOException` albo
            // `UnauthorizedAccessException` — przejsciowo, nie na stale. Ponawiamy
            // TYLKO ten lisc, nie rekurencje: dwa poziomy ponawiania mnozylyby proby
            // (5x5) i zamienialy chwilowa blokade w kilkusekundowy zastoj.
            ZapisAtomowy.Ponow(() => File.Copy(file, cel, overwrite: true));
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
            Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// Zastepuje CALY katalog "to" zawartoscia "from" w kolejnosci bezpiecznej dla
    /// awarii w polowie kopiowania: najpierw kopia do katalogu tymczasowego OBOK
    /// "to" (Copy moze rzucic w dowolnym miejscu — brak uprawnien, dysk pelny —
    /// i wtedy "to" zostaje NIETKNIETY), dopiero po jej pelnym sukcesie podmiana
    /// (Directory.Move to rename w obrebie tego samego katalogu nadrzednego, wiec
    /// tego samego wolumenu — praktycznie atomowy), a stara zawartosc "to" kasowana
    /// na koncu. Jesli sama podmiana zawiedzie (rzadkie: wyscig z systemem plikow),
    /// oddajemy stary stan na miejsce, zanim wyjatek poleci dalej — "to" nigdy nie
    /// zostaje katalogiem nieistniejacym.
    ///
    /// Naiwna kolejnosc "skasuj to, potem kopiuj" (uzywana wciaz w VersionStore.Commit,
    /// gdzie ofiara jest odtwarzalny snapshot — kopiowanie mozna powtorzyc z WorkDir,
    /// ktory ta operacja nie dotyka) tutaj nie wchodzi w gre: "to" bywa WorkDir,
    /// JEDYNA mutowalna prawda systemu (patrz diagnoza w raporcie) — awaria w polowie
    /// kopiowania nie moze zostawic go nieistniejacym ani w stanie mieszanym.
    public static void SafeReplace(string from, string to)
    {
        var staging = to + ".staging-" + Guid.NewGuid().ToString("N");
        Delete(staging);

        try
        {
            Copy(from, staging);
        }
        catch (Exception)
        {
            // Nieudana kopia nie moze zostawiac smiecia obok "to" — sprzatamy
            // czesciowo skopiowany staging, zanim wyjatek poleci dalej (to jest
            // wlasnie ta sciezka, ktora ma zostawic "to" NIETKNIETYM, wiec sam
            // staging tez nie powinien przetrwac).
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (Exception) { }
            throw;
        }

        string? previous = null;
        if (Directory.Exists(to))
        {
            previous = to + ".old-" + Guid.NewGuid().ToString("N");
            var zrodlo = to;
            ZapisAtomowy.Ponow(() => Directory.Move(zrodlo, previous));
        }

        try
        {
            ZapisAtomowy.Ponow(() => Directory.Move(staging, to));
        }
        catch (Exception)
        {
            // "to" chwilowo nie istnieje (przenieslismy je do "previous" wyzej) —
            // oddajemy je na miejsce, zeby ta funkcja nigdy nie zostawiala "to"
            // nieistniejacym, nawet gdy sam Move zawiedzie. "staging" tez nie moze
            // zostac smieciem obok "to" (Directory.Move, ktory zawodzi, zwykle
            // zostawia zrodlo nietkniete).
            if (previous is not null && !Directory.Exists(to))
                Directory.Move(previous, to);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (Exception) { }
            throw;
        }

        if (previous is not null)
            Delete(previous);
    }

    /// Kasowanie katalogu z rekurencja, ponawiane i odporne na brak katalogu.
    /// Powstalo z pomiaru Przemka: pelny `dotnet test` dawal 1 czerwony na 25
    /// przebiegow, ZA KAZDYM RAZEM w innym tescie — a to podpis awarii w SPRZATANIU,
    /// nie w scieżce produkcyjnej (xUnit przypisuje wyjatek z teardownu temu testowi,
    /// ktory wlasnie przebiegl). Ta sama chwilowa blokada, ktora psuje `File.Move`
    /// w ZapisAtomowy, trafia tu w `Directory.Delete`.
    ///
    /// Tolerowanie braku katalogu jest tu wlasciwe, a nie wygodne: kazde wywolanie
    /// w tym pliku kasuje katalog, ktorego nieistnienie JEST oczekiwanym stanem
    /// koncowym (staging po nieudanej kopii, poprzednia zawartosc po podmianie).
    public static void Delete(string path)
    {
        ZapisAtomowy.Ponow(() =>
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        });
    }
}
