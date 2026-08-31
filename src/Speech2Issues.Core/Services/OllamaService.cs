using System.Net.Http.Json;
using System.Text.Json;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Services;

public sealed class OllamaService(HttpClient httpClient, string model, string outputLanguage = "Russian") : IAiTaskProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string DisplayName => "Ollama";
    public string Model => model;

    public async Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<JsonElement>("api/tags", cancellationToken);
        if (!response.TryGetProperty("models", out var models))
        {
            return [];
        }

        var result = new List<AiModelInfo>();
        foreach (var item in models.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            IReadOnlyList<string> capabilities = [];
            try
            {
                using var showResponse = await httpClient.PostAsJsonAsync("api/show", new { model = name }, cancellationToken);
                if (showResponse.IsSuccessStatusCode)
                {
                    var show = await showResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                    if (show.TryGetProperty("capabilities", out var caps))
                    {
                        capabilities = caps.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToArray();
                    }
                }
            }
            catch (HttpRequestException)
            {
                // A model is still useful when an older Ollama does not expose capabilities.
            }

            if (capabilities.Count > 0 && !capabilities.Contains("completion", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var details = item.TryGetProperty("details", out var detailValue) ? detailValue : default;
            result.Add(new AiModelInfo(
                name,
                details.ValueKind == JsonValueKind.Object && details.TryGetProperty("parameter_size", out var parameter) ? parameter.GetString() ?? string.Empty : string.Empty,
                details.ValueKind == JsonValueKind.Object && details.TryGetProperty("quantization_level", out var quantization) ? quantization.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                capabilities));
        }

        return result.OrderByDescending(x => string.Equals(x.Name, model, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ConnectionCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await httpClient.GetFromJsonAsync<JsonElement>("api/version", cancellationToken);
            using var response = await httpClient.PostAsJsonAsync("api/show", new { model }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, $"Ollama отвечает, но модель {model} недоступна: {(int)response.StatusCode}");
            }
            var show = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var capabilities = show.TryGetProperty("capabilities", out var caps)
                ? string.Join(", ", caps.EnumerateArray().Select(x => x.GetString()))
                : "не указаны";
            return new(true, $"Ollama {version.GetProperty("version").GetString()}, {model}: {capabilities}");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    public async Task<TaskDraft> BuildDraftAsync(string transcript, CancellationToken cancellationToken = default) =>
        (await BuildDraftsAsync(transcript, cancellationToken))[0];

    public async Task<IReadOnlyList<TaskDraft>> BuildDraftsAsync(string transcript, CancellationToken cancellationToken = default)
    {
        var taskSchema = new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string" },
                title = new { type = "string" },
                description = new { type = "string" },
                acceptanceCriteria = new { type = "array", items = new { type = "string" } },
                labels = new { type = "array", items = new { type = "string" } },
                priority = new { type = "string", @enum = new[] { "critical", "high", "medium", "low" } },
                dueDate = new { type = new[] { "string", "null" } },
            },
            required = new[] { "id", "title", "description", "acceptanceCriteria", "labels", "priority", "dueDate" },
        };
        var schema = new
        {
            type = "object",
            properties = new
            {
                tasks = new { type = "array", minItems = 1, maxItems = 10, items = taskSchema },
            },
            required = new[] { "tasks" },
        };

        var prompt = $"""
            Convert the complete conversation below into one or more actionable software or work-management tasks.
            Treat all speakers and both audio sources as equally important context.
            Split the result into separate tasks when the conversation asks for distinct changes or deliverables that can be completed independently. Do not split one coherent change into artificial fragments.
            Write every title, description and acceptance criterion in {outputLanguage}; preserve technical identifiers verbatim.
            Put actionable substeps, checks and checkbox-style items only in acceptanceCriteria as plain text. Never write Markdown checkboxes such as "- [ ]" in description.
            Infer up to 5 short useful category labels from the content (for example: bug, UI, backend, documentation). Do not include priority as a label because the application adds it automatically.
            Do not invent dates, requirements or facts. Use null when no due date was spoken. Choose priority from critical, high, medium or low based on urgency; use medium when urgency is not stated or implied.
            Set each id to a different stable-looking value beginning with "s2i-".

            Conversation:
            {transcript}
            """;

        var request = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
            think = false,
            format = schema,
            options = new { temperature = 0.1 },
        };

        using var response = await httpClient.PostAsJsonAsync("api/chat", request, JsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Ollama task request failed ({(int)response.StatusCode}): {body}");
        }

        using var outer = JsonDocument.Parse(body);
        var content = outer.RootElement.GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("Ollama returned an empty task.");
        var batch = JsonSerializer.Deserialize<TaskDraftBatch>(content, JsonOptions)
            ?? throw new InvalidDataException("Ollama task JSON is invalid.");
        var drafts = batch.Tasks
            .Take(10)
            .Select(draft => (draft with { Id = $"s2i-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow }).Normalize(transcript))
            .ToArray();
        return drafts.Length > 0 ? drafts : throw new InvalidDataException("Ollama did not return any tasks.");
    }

    public async Task<RoutingDecision> RoutePlankaAsync(
        TaskDraft draft,
        IReadOnlyList<DestinationTarget> allowedTargets,
        string? fallbackListId,
        CancellationToken cancellationToken = default)
    {
        if (allowedTargets.Count == 0)
        {
            throw new InvalidOperationException("Для проекта не разрешён ни один список PLANKA.");
        }

        var fallback = allowedTargets.FirstOrDefault(x => x.Id == fallbackListId) ?? allowedTargets[0];
        if (allowedTargets.Count == 1)
        {
            return new(fallback.Id, fallback.DisplayName, false, "Единственный разрешённый список.");
        }

        var schema = new
        {
            type = "object",
            properties = new
            {
                listId = new { type = "string", @enum = allowedTargets.Select(x => x.Id).ToArray() },
                reason = new { type = "string" },
            },
            required = new[] { "listId", "reason" },
        };
        var candidates = string.Join(Environment.NewLine, allowedTargets.Select(x => $"- {x.Id}: {x.DisplayName}"));
        var prompt = $"""
            Choose the single best PLANKA list for this task. Return only one ID from the allowed list.
            Prefer a semantic category such as bug, fix, feature or documentation. Do not invent an ID.

            Task title: {draft.Title}
            Task description: {draft.Description}

            Allowed lists:
            {candidates}
            """;
        var request = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
            think = false,
            format = schema,
            options = new { temperature = 0 },
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync("api/chat", request, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(fallback.Id, fallback.DisplayName, true, $"Ollama HTTP {(int)response.StatusCode}");
            }
            using var outer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = outer.RootElement.GetProperty("message").GetProperty("content").GetString();
            using var route = JsonDocument.Parse(content ?? "{}");
            var listId = route.RootElement.TryGetProperty("listId", out var selected) ? selected.GetString() : null;
            var target = allowedTargets.FirstOrDefault(x => string.Equals(x.Id, listId, StringComparison.Ordinal));
            var reason = route.RootElement.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
            return target is null
                ? new(fallback.Id, fallback.DisplayName, true, "Модель вернула список вне разрешённого набора.")
                : new(target.Id, target.DisplayName, false, reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            return new(fallback.Id, fallback.DisplayName, true, ex.Message);
        }
    }

}
