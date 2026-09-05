using Generator.Engine.Model;

namespace Generator.Engine.Jobs;

/// Granica Plan A / Plan B. Kolejka i ProjectApi widza tylko to; testy warstwy
/// web podstawiaja atrape zamiast kopiowac do Web.Tests fejkowy runner piszacy
/// pliki. GenerationService jest jedyna prawdziwa implementacja.
public interface IRoundRunner
{
    Task<ProposalsOutcome> RunProposalsAsync(ProjectMeta meta, CancellationToken ct = default);
    Task<GenerationOutcome> RunFirstVersionAsync(ProjectMeta meta, CancellationToken ct = default);
    Task<GenerationOutcome> RunRoundAsync(ProjectMeta meta, IReadOnlyList<Comment> comments,
        CancellationToken ct = default);
}
