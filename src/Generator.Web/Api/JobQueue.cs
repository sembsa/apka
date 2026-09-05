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
    private readonly Channel<Zadanie> _kanal = Channel.CreateUnbounded<Zadanie>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _konsument;

    public JobQueue() => _konsument = Task.Run(Konsumuj);

    public bool MaAktywne(string projectId) => _aktywne.ContainsKey(projectId);

    /// <exception cref="JobRunningException">projekt ma juz zadanie w kolejce</exception>
    public string Enqueue(string projectId, Func<string, CancellationToken, Task> praca)
    {
        var jobId = Guid.NewGuid().ToString();
        if (!_aktywne.TryAdd(projectId, jobId)) throw new JobRunningException(projectId);

        _jobs[jobId] = new JobView(jobId, "queued", null);
        _kanal.Writer.TryWrite(new Zadanie(jobId, projectId, praca));
        return jobId;
    }

    public JobView? Get(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

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
            catch (Exception)
            {
                // Wyjatek to nasza awaria, wiec halted: powtorka da to samo.
                _aktywne.TryRemove(z.ProjectId, out _);
                Fail(z.Id, "halted", 1);
            }
            finally
            {
                _aktywne.TryRemove(z.ProjectId, out _);
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
