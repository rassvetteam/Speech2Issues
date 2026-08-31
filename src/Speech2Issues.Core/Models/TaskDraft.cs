using System.Text.Json.Serialization;

namespace Speech2Issues.Core.Models;

public sealed record TaskDraft
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = $"s2i-{Guid.NewGuid():N}";

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("acceptanceCriteria")]
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels { get; init; } = [];

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "medium";

    [JsonPropertyName("dueDate")]
    public string? DueDate { get; init; }

    [JsonPropertyName("transcript")]
    public string Transcript { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public TaskDraft Normalize(string transcript)
    {
        var title = string.IsNullOrWhiteSpace(Title) ? "Новая задача из записи" : Title.Trim();
        if (title.Length > 240)
        {
            title = title[..240].Trim();
        }

        var priority = Priority.Trim().ToLowerInvariant() switch
        {
            "critical" => "critical",
            "high" => "high",
            "low" => "low",
            _ => "medium",
        };

        var dueDate = DateOnly.TryParse(DueDate, out var parsed) ? parsed.ToString("yyyy-MM-dd") : null;

        return this with
        {
            Id = string.IsNullOrWhiteSpace(Id) ? $"s2i-{Guid.NewGuid():N}" : Id,
            Title = title,
            Description = Description.Trim(),
            AcceptanceCriteria = AcceptanceCriteria.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray(),
            Labels = Labels.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Where(x => x.Length <= 128)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray(),
            Priority = priority,
            DueDate = dueDate,
            Transcript = transcript.Trim(),
        };
    }
}

public sealed record TaskDraftBatch
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskDraft> Tasks { get; init; } = [];
}
