using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Destinations;

public interface ITaskDestination
{
    DestinationKind Kind { get; }
    Task<ConnectionCheck> CheckConnectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DestinationTarget>> LoadTargetsAsync(CancellationToken cancellationToken = default);
    Task<CreatedTaskResult> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default);
}
