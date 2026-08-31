namespace Speech2Issues.Core.Models;

public sealed record DestinationTarget(
    string Id,
    string DisplayName,
    string? Parent = null,
    string? ProjectId = null,
    string? ProjectName = null,
    string? BoardName = null)
{
    public override string ToString() => DisplayName;
}

public sealed record ExternalProjectCandidate(
    DestinationKind Kind,
    string ExternalId,
    string DisplayName,
    IReadOnlyList<DestinationTarget> Targets);

public sealed record AiModelInfo(
    string Name,
    string ParameterSize,
    string Quantization,
    long Size,
    IReadOnlyList<string> Capabilities)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ParameterSize)
        ? Name
        : $"{Name}  ·  {ParameterSize} {Quantization}".TrimEnd();

    public override string ToString() => Name;
}

public sealed record RoutingDecision(string ListId, string DisplayName, bool UsedFallback, string? Reason = null);

public sealed record DeliveryPlan(
    string DraftId,
    string ProjectId,
    IReadOnlyList<string> BindingIds,
    IReadOnlyDictionary<string, RoutingDecision> Routes);

public sealed record DeliveryAttempt(
    string DraftId,
    string BindingId,
    DestinationKind Destination,
    string TargetId,
    string Target,
    HistoryStatus Status,
    string? ExternalUrl,
    string? Error);

public static class DeliveryCoordinator
{
    public static HistoryStatus AggregateStatus(IEnumerable<DeliveryAttempt> attempts)
    {
        var values = attempts.ToArray();
        if (values.Length == 0) return HistoryStatus.Failed;
        var sent = values.Count(x => x.Status == HistoryStatus.Sent);
        return sent == values.Length ? HistoryStatus.Sent : sent > 0 ? HistoryStatus.PartiallySent : HistoryStatus.Failed;
    }

    public static IReadOnlySet<string> PendingBindingIds(IEnumerable<DeliveryAttempt> attempts) =>
        attempts.Where(x => x.Status != HistoryStatus.Sent).Select(x => x.BindingId).ToHashSet(StringComparer.Ordinal);
}

public sealed record ConnectionCheck(bool IsSuccess, string Message);

public sealed record CreatedTaskResult(string Provider, string ExternalId, string Url, bool WasExisting = false);

public enum DestinationKind
{
    Planka,
    GitHub,
    Obsidian,
    Webhook,
}

public enum HistoryStatus
{
    Pending,
    Canceled,
    Sent,
    Failed,
    PartiallySent,
}

public sealed record HistoryEntry(
    string Id,
    DateTimeOffset CreatedAt,
    DestinationKind Destination,
    string Target,
    HistoryStatus Status,
    string Title,
    string Transcript,
    string DraftJson,
    string? ExternalUrl,
    string? Error,
    string? AudioPath,
    string? ProjectId = null);
