using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Services;

public sealed class OpenAiCompatibleService(
    HttpClient httpClient,
    string model,
    string displayName,
    string outputLanguage = "Russian") : IAiTaskProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string DisplayName => displayName;
    public string Model => model;

    public async Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{DisplayName} models request failed ({(int)response.StatusCode}): {body}", null, response.StatusCode);
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new AiModelInfo(id!, string.Empty, string.Empty, 0, ["completion"]))
            .OrderByDescending(item => string.Equals(item.Name, model, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ConnectionCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(cancellationToken);
            if (models.Count > 0 && !models.Any(item => string.Equals(item.Name, model, StringComparison.OrdinalIgnoreCase)))
            {
                return new(false, $"{DisplayName} отвечает, но модель {model} не найдена.");
            }

            return new(true, $"{DisplayName}: модель {model} доступна.");
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed })
            {
                try
                {
                    var schema = new
                    {
                        type = "object",
                        properties = new { ok = new { type = "string" } },
                        required = new[] { "ok" },
                        additionalProperties = false,
                    };
                    var content = await SendStructuredAsync("speech2issues_check", schema, "Return JSON with ok set to ready.", 0, cancellationToken);
                    using var result = JsonDocument.Parse(content);
                    return result.RootElement.TryGetProperty("ok", out _)
                        ? new(true, $"{DisplayName}: модель {model} отвечает.")
                        : new(false, $"{DisplayName} вернул некорректный проверочный ответ.");
                }
                catch (Exception checkException)
                {
                    return new(false, checkException.Message);
                }
            }
            return new(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<TaskDraft>> BuildDraftsAsync(string transcript, CancellationToken cancellationToken = default)
    {
        var schema = CreateTaskBatchSchema();
        var prompt = $"""
            Convert the complete conversation below into one or more actionable software or work-management tasks.
            Treat all speakers and both audio sources as equally important context.
            Split the result into separate tasks when the conversation asks for distinct changes or deliverables that can be completed independently. Do not split one coherent change into artificial fragments.
            Write every title, description and acceptance criterion in {outputLanguage}; preserve technical identifiers verbatim.
            Put actionable substeps, checks and checkbox-style items only in acceptanceCriteria as plain text. Never write Markdown checkboxes such as "- [ ]" in description.
            Infer up to 5 short useful category labels from the content. Do not include priority as a label.
            Do not invent dates, requirements or facts. Use null when no due date was spoken. Choose priority from critical, high, medium or low; use medium when urgency is not stated.
            Set each id to a different stable-looking value beginning with "s2i-".

            Conversation:
            {transcript}
            """;

        var content = await SendStructuredAsync("speech2issues_task_batch", schema, prompt, 0.1, cancellationToken);
        var batch = JsonSerializer.Deserialize<TaskDraftBatch>(content, JsonOptions)
            ?? throw new InvalidDataException($"{DisplayName} returned invalid task JSON.");
        var drafts = batch.Tasks
            .Take(10)
            .Select(draft => (draft with { Id = $"s2i-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow }).Normalize(transcript))
            .ToArray();
        return drafts.Length > 0 ? drafts : throw new InvalidDataException($"{DisplayName} did not return any tasks.");
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

        var fallback = allowedTargets.FirstOrDefault(item => item.Id == fallbackListId) ?? allowedTargets[0];
        if (allowedTargets.Count == 1)
        {
            return new(fallback.Id, fallback.DisplayName, false, "Единственный разрешённый список.");
        }

        var schema = new
        {
            type = "object",
            properties = new
            {
                listId = new { type = "string", @enum = allowedTargets.Select(item => item.Id).ToArray() },
                reason = new { type = "string" },
            },
            required = new[] { "listId", "reason" },
            additionalProperties = false,
        };
        var candidates = string.Join(Environment.NewLine, allowedTargets.Select(item => $"- {item.Id}: {item.DisplayName}"));
        var prompt = $"""
            Choose the single best PLANKA list for this task. Return only one ID from the allowed list.
            Prefer a semantic category such as bug, fix, feature or documentation. Do not invent an ID.

            Task title: {draft.Title}
            Task description: {draft.Description}

            Allowed lists:
            {candidates}
            """;

        try
        {
            var content = await SendStructuredAsync("speech2issues_route", schema, prompt, 0, cancellationToken);
            using var route = JsonDocument.Parse(content);
            var listId = route.RootElement.TryGetProperty("listId", out var selected) ? selected.GetString() : null;
            var target = allowedTargets.FirstOrDefault(item => string.Equals(item.Id, listId, StringComparison.Ordinal));
            var reason = route.RootElement.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
            return target is null
                ? new(fallback.Id, fallback.DisplayName, true, "Модель вернула список вне разрешённого набора.")
                : new(target.Id, target.DisplayName, false, reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            return new(fallback.Id, fallback.DisplayName, true, ex.Message);
        }
    }

    private async Task<string> SendStructuredAsync(
        string schemaName,
        object schema,
        string prompt,
        double temperature,
        CancellationToken cancellationToken)
    {
        var strictRequest = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = schemaName, strict = true, schema },
            },
            temperature,
            stream = false,
        };

        using var firstResponse = await httpClient.PostAsJsonAsync("chat/completions", strictRequest, JsonOptions, cancellationToken);
        if (firstResponse.IsSuccessStatusCode)
        {
            return await ReadContentAsync(firstResponse, cancellationToken);
        }

        var firstBody = await firstResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!CanFallback(firstResponse.StatusCode))
        {
            throw new HttpRequestException($"{DisplayName} chat request failed ({(int)firstResponse.StatusCode}): {firstBody}");
        }

        var fallbackRequest = new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt + Environment.NewLine + Environment.NewLine +
                        "Return one JSON object only. It must conform exactly to this JSON Schema:" + Environment.NewLine +
                        JsonSerializer.Serialize(schema, JsonOptions),
                },
            },
            response_format = new { type = "json_object" },
            temperature,
            stream = false,
        };
        using var fallbackResponse = await httpClient.PostAsJsonAsync("chat/completions", fallbackRequest, JsonOptions, cancellationToken);
        var fallbackBody = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!fallbackResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{DisplayName} chat request failed ({(int)fallbackResponse.StatusCode}): {fallbackBody}");
        }

        return ReadContent(fallbackBody);
    }

    private static async Task<string> ReadContentAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ReadContent(await response.Content.ReadAsStringAsync(cancellationToken));

    private static string ReadContent(string body)
    {
        using var outer = JsonDocument.Parse(body);
        return outer.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("OpenAI-compatible provider returned an empty response.");
    }

    private static bool CanFallback(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    private static object CreateTaskBatchSchema()
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
            additionalProperties = false,
        };
        return new
        {
            type = "object",
            properties = new
            {
                tasks = new { type = "array", minItems = 1, maxItems = 10, items = taskSchema },
            },
            required = new[] { "tasks" },
            additionalProperties = false,
        };
    }
}
