using System.Collections.Concurrent;
using Generator.Web.Contracts;

namespace Generator.Web.Mock;

public enum MockJobOutcome { Success, Retrying, Halted }

/// <summary>
/// Backend w pamięci, żeby frontend nie czekał na Plan B.
/// Odwzorowuje te zachowania kontraktu, które widzi UI: zadania zamiast odpowiedzi
/// synchronicznych, asymetrię liczników (4.1), gwarancję 4.4, frozen (4.5).
/// </summary>
public class MockProjectApi : IProjectApi
{
    private readonly ConcurrentDictionary<string, ProjectView> _projects = new();
    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly ConcurrentDictionary<(string, int), List<CommentDto>> _comments = new();

    public MockJobOutcome NextJobOutcome { get; set; } = MockJobOutcome.Success;
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.FromSeconds(2);

    public Task<ProjectView> CreateAsync(string source, string description)
    {
        var p = new ProjectView(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString("N"),
            "draft",
            RoundsUsed: 0,
            RoundsLimit: 15,
            Versions: []);
        _projects[p.Id] = p;
        return Task.FromResult(p);
    }

    public Task<ProjectView> GetAsync(string id) => Task.FromResult(_projects[id]);

    public void ForceFrozen(string id) => _projects[id] = _projects[id] with { Status = "frozen" };

    public void ForceOrphaned(string id, int version, IReadOnlyList<string> anchors)
    {
        var p = _projects[id];
        _projects[id] = p with
        {
            Versions = [.. p.Versions.Select(v =>
                v.Number == version ? v with { OrphanedAnchors = anchors } : v)],
        };
    }

    /// <summary>
    /// Zasiewa uwagi w statusie open, bez przechodzenia przez klik w iframe.
    /// Test gwarancji z 4.4 musi mieć co stracić — bez uwag przycisk „Popraw to"
    /// jest disabled i test przeszedłby próżno.
    /// </summary>
    public void SeedComments(string id, int version, params string[] teksty)
    {
        _comments[(id, version)] = [.. teksty.Select((t, i) => new CommentDto(
            Id: $"seed-{i}",
            Anchor: "hero",
            Text: t,
            Viewport: "desktop",
            CreatedAt: DateTimeOffset.UnixEpoch,
            Status: "open",
            Note: null))];
    }

    public async Task<string> RequestProposalsAsync(string id)
    {
        await Task.Delay(SimulatedDelay);
        var jobId = Guid.NewGuid().ToString();
        _jobs[jobId] = new JobView(jobId, "succeeded", null);
        return jobId;
    }

    public Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id) =>
        Task.FromResult<IReadOnlyList<ProposalView>>(
        [
            new("p-1", "Ciepły i prosty", "<html><body style='font-family:system-ui'><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>"),
            new("p-2", "Nowoczesny, ciemny", "<html><body style='background:#111;color:#eee'><h1 data-cmt-id=\"hero\">Fryzjer</h1></body></html>"),
            new("p-3", "Klasyczny z cennikiem", "<html><body><h1 data-cmt-id=\"hero\">Fryzjer</h1><ul data-cmt-id=\"cennik\"><li>Strzyżenie 60 zł</li></ul></body></html>"),
        ]);

    public Task ChooseProposalAsync(string id, string proposalId)
    {
        _projects[id] = _projects[id] with { Status = "active" };
        return Task.CompletedTask;
    }

    public async Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments)
    {
        if (_projects[id].Status == "frozen")
            throw new InvalidOperationException("projekt zamrozony (409)");   // kontrakt 4.5

        _comments[(id, version)] = [.. comments];

        var jobId = Guid.NewGuid().ToString();
        _jobs[jobId] = new JobView(jobId, "running", null);
        await Task.Delay(SimulatedDelay);

        // Projekt czytamy PO opoznieniu: przez te 2 sekundy stan mogl sie zmienic
        // (np. ForceFrozen z testu), a zapis ze starej kopii cicho by to skasowal.
        var project = _projects[id];

        switch (NextJobOutcome)
        {
            case MockJobOutcome.Success:
                _comments[(id, version)] =
                [
                    .. comments.Select(c => c with
                    {
                        Status = "applied",
                        Note = $"Zrobione: {c.Text}",
                    }),
                ];
                _projects[id] = project with
                {
                    RoundsUsed = project.RoundsUsed + 1,
                    Versions = [.. project.Versions,
                        new VersionView(project.Versions.Count + 1, $"/preview/{id}/{project.Versions.Count + 1}", [])],
                };
                _jobs[jobId] = new JobView(jobId, "succeeded", null);
                break;

            // Kontrakt 4.4: nieudana runda NIE zuzywa rundy i NIE rusza statusow.
            case MockJobOutcome.Retrying:
                _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView("retrying", 1));
                break;

            case MockJobOutcome.Halted:
                _jobs[jobId] = new JobView(jobId, "failed", new JobFailureView("halted", 2));
                break;
        }

        return jobId;
    }

    public Task<JobView> GetJobAsync(string jobId) => Task.FromResult(_jobs[jobId]);

    public Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version) =>
        Task.FromResult<IReadOnlyList<CommentDto>>(
            _comments.TryGetValue((id, version), out var list) ? list : []);
}
