using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Services;

public interface IAiTaskProvider
{
    string DisplayName { get; }
    string Model { get; }
    Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<ConnectionCheck> CheckAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskDraft>> BuildDraftsAsync(string transcript, CancellationToken cancellationToken = default);
    Task<RoutingDecision> RoutePlankaAsync(
        TaskDraft draft,
        IReadOnlyList<DestinationTarget> allowedTargets,
        string? fallbackListId,
        CancellationToken cancellationToken = default);
}
