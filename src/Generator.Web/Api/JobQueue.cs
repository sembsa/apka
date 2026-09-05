using System.Collections.Concurrent;
using System.Threading.Channels;
using Generator.Web.Contracts;

namespace Generator.Web.Api;

/// Kolejka zadan: jedno na projekt naraz (plan, sekcja 7), jeden watek konsumenta.
///
/// NIE PONAWIA NICZEGO (ruling 3). GenerationService zuzywa swoja jedna umowna
/// powtorke wewnatrz i zwraca wylacznie Halted; druga warstwa powtorek spalilaby
/// budzet na identyczny wynik. Zadanie stoi w `running` przez caly wewnetrzny
/// retry — to jest wlasnie „widzi jedno »pracuje nad tym«" z kontraktu 4.3.
public class JobQueue : IDisposable
{
    private record Zadanie(string Id, string ProjectId, Func<string, CancellationToken, Task> Praca);

    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _aktywne = new();  // projectId -> jobId

    /// jobId -> projectId. Zyje DLUZEJ niz `_aktywne`, ktore znika z chwila konca
    /// zadania: `GET /api/jobs/{id}` musi sprawdzic token wlasciwego projektu takze
    /// (a wlasciwie zwlaszcza) wtedy, gdy zadanie juz sie skonczylo — po to klient
    /// o nie pyta. Nie sprzatamy tego przez cale zycie procesu, tak samo jak `_jobs`.
    private readonly ConcurrentDictionary<string, string> _zadaniaProjektow = new();
    private readonly Channel<Zadanie> _kanal = Channel.CreateUnbounded<Zadanie>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _konsument;
    private readonly ILogger<JobQueue>? _logger;

    /// Logger opcjonalny, bo `new JobQueue()` stoi w kilkunastu testach, a kolejka
    /// nie ma powodu wymagac kontenera. W aplikacji wstrzykuje go DI.
    public JobQueue(ILogger<JobQueue>? logger = null)
    {
        _logger = logger;
        _konsument = Task.Run(Konsumuj);
    }

    public bool MaAktywne(string projectId) => _aktywne.ContainsKey(projectId);

    /// <exception cref="JobRunningException">projekt ma juz zadanie w kolejce</exception>
    public string Enqueue(string projectId, Func<string, CancellationToken, Task> praca)
    {
        var jobId = Guid.NewGuid().ToString();
        if (!_aktywne.TryAdd(projectId, jobId)) throw new JobRunningException(projectId);

        // `_jobs` przed `TryWrite`, nigdy odwrotnie: konsument moze podniesc zadanie
        // natychmiast, a wtedy odwrotna kolejnosc nadpisalaby jego `running` przez
        // nasze `queued`.
        _jobs[jobId] = new JobView(jobId, "queued", null);
        _zadaniaProjektow[jobId] = projectId;

        // Po `Dispose` kanal jest zamkniety i `TryWrite` zwraca `false`. Ignorowanie
        // tego oddawalo klientowi `jobId` zadania, ktorego nikt nigdy nie podniesie:
        // stan `queued` na zawsze, rezerwacja projektu zajeta na zawsze, wiec projekt
        // do konca zycia procesu odmawia kazdej nastepnej rundy. Sprzatamy oba wpisy
        // i mowimy glosno.
        if (!_kanal.Writer.TryWrite(new Zadanie(jobId, projectId, praca)))
        {
            _aktywne.TryRemove(new KeyValuePair<string, string>(projectId, jobId));
            _jobs.TryRemove(jobId, out _);
            _zadaniaProjektow.TryRemove(jobId, out _);
            throw new ObjectDisposedException(nameof(JobQueue),
                "kolejka jest zamknieta — zadanie nie zostalo przyjete");
        }

        return jobId;
    }

    public JobView? Get(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

    /// Do ktorego projektu nalezy zadanie; `null` = nie znamy takiego zadania.
    /// Dzieki temu warstwa HTTP potrafi sprawdzic token WLASCIWEGO projektu,
    /// zamiast wpuszczac kazdego, kto zna sam `jobId`.
    public string? Projekt(string jobId) =>
        _zadaniaProjektow.TryGetValue(jobId, out var projectId) ? projectId : null;

    /// Zadanie odklada wynik TUTAJ, a nie przez wyjatek: awaria modelu jest
    /// normalnym wynikiem (kontrakt 4.3), nie bledem programu.
    public void Fail(string jobId, string handling, int attempts) =>
        _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView(handling, attempts));

    private async Task Konsumuj()
    {
        await foreach (var z in _kanal.Reader.ReadAllAsync(_stop.Token))
        {
            _jobs[z.Id] = new JobView(z.Id, "running", null);
            try
            {
                await z.Praca(z.Id, _stop.Token);

                // Zwolnienie projektu PRZED ogloszeniem stanu koncowego, nie w
                // `finally` po nim. Klient (i test) pyta o zadanie w petli i rusza
                // dalej, gdy tylko zobaczy `succeeded` — gdyby wpis w `_aktywne`
                // zyl jeszcze chwile po tej publikacji, kolejne zadanie tego samego
                // projektu dostaloby JobRunningException za nic. `finally` zostaje
                // jako siatka na sciezki, ktore tu nie doszly.
                _aktywne.TryRemove(z.ProjectId, out _);

                // Praca mogla juz oznaczyc zadanie jako failed (kontrakt 4.3).
                if (_jobs[z.Id].Status == "running")
                    _jobs[z.Id] = new JobView(z.Id, "succeeded", null);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Wyjatek to nasza awaria, wiec halted: powtorka da to samo.
                //
                // DRUGA droga, ktora gubila przyczyne — i grozniejsza od tej
                // w ProjectApi, bo tutaj nie ma nawet `JobFailure` z RunGate:
                // trafia tu InvalidDataException z uszkodzonego project.json,
                // brak pliku wybranego szkicu, blad zapisu snapshotu. Klient
                // dostaje „halted" i tyle; bez tego wpisu nie zostaje nic.
                _logger?.LogError(ex, "zadanie {JobId} projektu {ProjectId} rzucilo wyjatkiem",
                    z.Id, z.ProjectId);

                _aktywne.TryRemove(z.ProjectId, out _);
                Fail(z.Id, "halted", 1);
            }
            finally
            {
                // Kasujemy po PARZE (projekt, zadanie), nie po samym kluczu. Powyzej
                // zwolnilismy rezerwacje przed ogloszeniem stanu koncowego, wiec zanim
                // ten `finally` sie wykona, inne zadanie tego samego projektu moze juz
                // miec swoja wlasna rezerwacje — a `TryRemove(klucz)` zdjalby CUDZA.
                // Skutek byl realny: po takim zdjeciu `MaAktywne` klamie „wolne", trzeci
                // `Enqueue` przechodzi i dwie rundy jada rownolegle po tym samym work/,
                // kazda liczac `version != CurrentVersion` na stanie sprzed zatwierdzenia
                // drugiej. Klient placi dwa razy i traci dwie rundy z pietnastu.
                // Wariant z KeyValuePair kasuje tylko wtedy, gdy wartosc nadal jest nasza.
                _aktywne.TryRemove(new KeyValuePair<string, string>(z.ProjectId, z.Id));
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _kanal.Writer.TryComplete();
        try { _konsument.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
