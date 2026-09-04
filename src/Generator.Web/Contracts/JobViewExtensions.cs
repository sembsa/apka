namespace Generator.Web.Contracts;

/// <summary>
/// Jedno miejsce prawdy o tym, co stan zadania znaczy DLA KLIENTA.
/// Powstalo z bledu znalezionego przeklikaniem: `JobStatus` pisal „Claude pracuje nad
/// Twoja strona…", a przycisk „Popraw to" w tym samym czasie byl aktywny, bo sprawdzal
/// tylko `Status == "running"` i nie widzial `Failure.Handling == "retrying"`. Dwa
/// miejsca liczyly to samo osobno i rozjechaly sie. Rozgalezienie idzie po `Handling`,
/// nigdy po przyczynie (kontrakt 4.3).
/// </summary>
public static class JobViewExtensions
{
    /// Powtorka jest dla klienta dalsza praca, nie awaria — stad `retrying` tutaj.
    public static bool WTokuDlaKlienta(this JobView? job) =>
        job is not null
        && (job.Status is "queued" or "running" || job.Failure?.Handling == "retrying");

    /// `halted`: zajmujemy sie tym my. Komunikat mowi „klikanie ponownie nic nie zmieni".
    public static bool Zatrzymane(this JobView? job) =>
        job?.Failure?.Handling == "halted";

    /// Przycisk nie moze zapraszac do klikania, gdy tekst obok mowi, ze to nic nie da.
    public static bool BlokujeNowaRunde(this JobView? job) =>
        job.WTokuDlaKlienta() || job.Zatrzymane();
}
