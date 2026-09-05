namespace Generator.Engine.IO;

/// <summary>
/// Zapis „albo caly, albo wcale": tresc idzie do pliku tymczasowego, a dopiero
/// `File.Move(overwrite)` podmienia cel. Przerwany zapis nie moze zostawic polowy
/// pliku tam, gdzie stal komplet danych klienta.
///
/// Dwie rzeczy ponad sam schemat tmp+Move, obie z raportu Przemka o tescie
/// `Nieudana_runda_przywraca_workdir_do_ostatniej_udanej_wersji`, ktory u niego
/// pada losowo z `UnauthorizedAccessException` przy `File.Move` (klasy testowe
/// biegna rownolegle, Windows):
///
/// 1. Nazwa pliku tymczasowego jest UNIKALNA. Wspolne `plik.tmp` znaczy, ze dwa
///    rownolegle zapisy tej samej sciezki pisza do tego samego pliku. Ten wyscig
///    jest POTWIERDZONY, takze na macOS: po przywroceniu wspolnej nazwy
///    `Rownolegle_zapisy_tej_samej_sciezki_nie_dziela_pliku_tymczasowego` pada
///    3 razy na 3 z `FileNotFoundException` — pierwszy watek zabral tmp Move'em,
///    zanim drugi zdazyl go przeniesc. Na Windows ten sam wyscig wychodzi jako
///    `UnauthorizedAccessException` (zrodlo trzyma jeszcze uchwyt drugiego watku),
///    czyli dokladnie tak, jak w raporcie.
/// 2. Podmiana jest ponawiana. Na Windows swiezo zamkniety plik potrafi byc przez
///    chwile zajety przez antywirusa albo indeksator, a `Move` konczy sie wtedy
///    `UnauthorizedAccessException` lub `IOException` — przejsciowo, nie na stale.
///
/// Co jest hipoteza, a co nie: sam wyscig o `plik.tmp` jest zmierzony (punkt 1),
/// ponawianie juz nie — przejsciowej blokady antywirusa nie da sie wywolac na macOS.
/// I nie wiadomo, KTORY z dwoch jest przyczyna porazki u Przemka. Miara nalezy do
/// maszyny, na ktorej to widac: pelny przebieg x10, ile czerwonych przed i po.
/// </summary>
public static class ZapisAtomowy
{
    public const int Proby = 5;

    public static void Zapisz(string sciezka, string tresc)
    {
        // Guid, nie PID+watek: identyfikator watku zarzadzanego bywa przydzielany
        // ponownie po zakonczeniu watku, a nazwa ma byc unikalna takze wtedy.
        var tmp = $"{sciezka}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, tresc);
        try
        {
            Ponow(() => File.Move(tmp, sciezka, overwrite: true), Proby, Odczekaj);
        }
        catch
        {
            // Nieudana podmiana zostawilaby smiec o unikalnej nazwie, ktorego juz nic
            // nigdy nie nadpisze. Sprzatanie nie moze przeslonic prawdziwego bledu.
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>
    /// Ponawia akcje, dopoki pada przejsciowym bledem wejscia-wyjscia. `odczekaj`
    /// jest parametrem, zeby test mogl sprawdzic sama logike ponawiania bez czekania
    /// i bez blokowania pliku — czyli tak samo na kazdym systemie.
    /// </summary>
    public static void Ponow(Action akcja, int proby, Action<int> odczekaj)
    {
        for (var proba = 1; ; proba++)
        {
            try
            {
                akcja();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && proba < proby)
            {
                // Ostatnia proba leci dalej nieopakowana: po piatym razie to juz nie
                // jest chwilowa blokada i wolajacy ma zobaczyc prawdziwy wyjatek.
                odczekaj(proba);
            }
        }
    }

    private static void Odczekaj(int proba) => Thread.Sleep(10 * proba);   // 10+20+30+40 = 100 ms
}
