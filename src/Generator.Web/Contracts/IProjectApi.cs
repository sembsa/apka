namespace Generator.Web.Contracts;

/// <summary>
/// Kształt kontraktu §1. MockProjectApi teraz, klient HTTP po Planie B —
/// strony nie zauważą podmiany.
/// </summary>
public interface IProjectApi
{
    Task<ProjectView> CreateAsync(string source, string description);
    Task<ProjectView> GetAsync(string id);
    Task<string> RequestProposalsAsync(string id);
    Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id);
    Task ChooseProposalAsync(string id, string proposalId);
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
