namespace Generator.Web.Contracts;

/// <summary>
/// Kształt kontraktu §1. Blazor wola to W PROCESIE — postep idzie obwodem SignalR,
/// nie pollingiem po HTTP (§1). Endpointy /api/* sa cienka warstwa nad ta sama
/// usluga, dla klientow spoza naszego UI. MockProjectApi zostaje za
/// `Projects:UseMock` do pracy nad UI bez zainstalowanego `claude`.
/// </summary>
public interface IProjectApi
{
    Task<ProjectView> CreateAsync(string source, string description);
    Task<ProjectView> GetAsync(string id);
    Task<string> RequestProposalsAsync(string id);
    Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id);
    Task ChooseProposalAsync(string id, string proposalId);

    /// <summary>
    /// Kontrakt 1: `POST /api/projects/{id}/versions` — wersja 1 z wybranej
    /// propozycji. Osobne zadanie, nie skutek uboczny wyboru: prawdziwa generacja
    /// trwa minuty, a wybor ma byc natychmiastowy.
    /// </summary>
    Task<string> CreateFirstVersionAsync(string id);

    Task<string> ApplyCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments);
    Task<JobView> GetJobAsync(string jobId);

    /// <summary>
    /// Kontrakt 5.1: przestawia `currentVersion` na podana wersje. Historia zostaje,
    /// nic nie jest usuwane, runda sie nie zuzywa. Nie jest to zadanie — model sie
    /// nie uruchamia, wiec nie ma po co zwracac jobId.
    /// </summary>
    Task RollbackAsync(string id, int version);
    Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version);
}
