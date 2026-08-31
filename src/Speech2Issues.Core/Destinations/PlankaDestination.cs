using System.Net.Http.Json;
using System.Text.Json;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Destinations;

public sealed class PlankaDestination(HttpClient httpClient, PlankaSettings settings, string apiKey) : ITaskDestination
{
    private const long PositionStep = 65536;
    private static readonly string[] LabelColors = ["lagoon-blue", "summer-sky", "fresh-salad", "sugar-plum", "pirate-gold", "coral-green", "light-orange", "lavender-fields"];
    public DestinationKind Kind => DestinationKind.Planka;

    public async Task<ConnectionCheck> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new(false, "Укажите API key PLANKA.");
        }
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/projects", null, cancellationToken);
            return response.IsSuccessStatusCode
                ? new(true, "PLANKA доступна, API key принят.")
                : new(false, $"PLANKA вернула HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<DestinationTarget>> LoadTargetsAsync(CancellationToken cancellationToken = default)
    {
        using var projectsResponse = await SendAsync(HttpMethod.Get, "api/projects", null, cancellationToken);
        await EnsureSuccessAsync(projectsResponse, cancellationToken);
        using var document = JsonDocument.Parse(await projectsResponse.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var projectNames = new Dictionary<string, string>();
        if (root.TryGetProperty("items", out var projects))
        {
            foreach (var project in projects.EnumerateArray())
            {
                projectNames[project.GetProperty("id").GetString()!] = project.GetProperty("name").GetString() ?? "Project";
            }
        }

        var boards = root.TryGetProperty("included", out var included) && included.TryGetProperty("boards", out var boardArray)
            ? boardArray.EnumerateArray().Select(x => new
            {
                Id = x.GetProperty("id").GetString()!,
                Name = x.GetProperty("name").GetString() ?? "Board",
                ProjectId = x.GetProperty("projectId").GetString()!,
            }).ToArray()
            : [];

        var targets = new List<DestinationTarget>();
        foreach (var board in boards)
        {
            using var boardResponse = await SendAsync(HttpMethod.Get, $"api/boards/{Uri.EscapeDataString(board.Id)}", null, cancellationToken);
            await EnsureSuccessAsync(boardResponse, cancellationToken);
            using var boardDocument = JsonDocument.Parse(await boardResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!boardDocument.RootElement.TryGetProperty("included", out var boardIncluded) ||
                !boardIncluded.TryGetProperty("lists", out var lists))
            {
                continue;
            }
            foreach (var list in lists.EnumerateArray())
            {
                var listId = list.GetProperty("id").GetString()!;
                var rawListName = list.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(rawListName)) continue;
                var listName = rawListName.Trim();
                projectNames.TryGetValue(board.ProjectId, out var projectName);
                targets.Add(new(
                    listId,
                    $"{projectName ?? "Project"} / {board.Name} / {listName}",
                    board.Id,
                    board.ProjectId,
                    projectName,
                    board.Name));
            }
        }
        return targets.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<CreatedTaskResult> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ListId))
        {
            throw new InvalidOperationException("В настройках PLANKA не выбран список.");
        }

        using var listResponse = await SendAsync(HttpMethod.Get, $"api/lists/{Uri.EscapeDataString(settings.ListId)}", null, cancellationToken);
        await EnsureSuccessAsync(listResponse, cancellationToken);
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken));
        var boardId = listDocument.RootElement.GetProperty("item").GetProperty("boardId").GetString()
            ?? throw new InvalidDataException("PLANKA did not return a board ID for the selected list.");
        var cards = listDocument.RootElement.GetProperty("included").GetProperty("cards");
        double maxPosition = 0;
        foreach (var card in cards.EnumerateArray())
        {
            if (card.TryGetProperty("position", out var position) && position.TryGetDouble(out var value))
            {
                maxPosition = Math.Max(maxPosition, value);
            }
        }

        var targetPosition = (long)Math.Min(Math.Pow(2, 50), maxPosition + Math.Pow(2, 14));
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "project",
            ["position"] = targetPosition,
            ["name"] = draft.Title,
            ["description"] = TaskMarkdown.BuildBody(draft, includeMarker: false, includeChecklist: false, includeMetadata: false),
        };
        if (draft.DueDate is not null)
        {
            payload["dueDate"] = $"{draft.DueDate}T23:59:00.000Z";
        }
        using var createResponse = await SendAsync(HttpMethod.Post, $"api/lists/{Uri.EscapeDataString(settings.ListId)}/cards", payload, cancellationToken);
        await EnsureSuccessAsync(createResponse, cancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var id = created.GetProperty("item").GetProperty("id").GetString()!;
        await CreateTaskListAsync(id, draft.AcceptanceCriteria, cancellationToken);
        await ApplyLabelsAsync(boardId, id, draft, cancellationToken);
        return new("Planka", id, BuildCardUrl(id));
    }

    private async Task CreateTaskListAsync(string cardId, IReadOnlyList<string> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        using var listResponse = await SendAsync(HttpMethod.Post, $"api/cards/{Uri.EscapeDataString(cardId)}/task-lists", new
        {
            position = PositionStep,
            name = "Критерии готовности",
            showOnFrontOfCard = true,
            hideCompletedTasks = false,
        }, cancellationToken);
        await EnsureSuccessAsync(listResponse, cancellationToken);
        var taskList = await listResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var taskListId = taskList.GetProperty("item").GetProperty("id").GetString()!;
        for (var index = 0; index < items.Count; index++)
        {
            var name = items[index].Trim();
            if (name.Length > 1024) name = name[..1024].TrimEnd();
            using var taskResponse = await SendAsync(HttpMethod.Post, $"api/task-lists/{Uri.EscapeDataString(taskListId)}/tasks", new
            {
                position = PositionStep * (index + 1L),
                name,
                isCompleted = false,
            }, cancellationToken);
            await EnsureSuccessAsync(taskResponse, cancellationToken);
        }
    }

    private async Task ApplyLabelsAsync(string boardId, string cardId, TaskDraft draft, CancellationToken cancellationToken)
    {
        using var boardResponse = await SendAsync(HttpMethod.Get, $"api/boards/{Uri.EscapeDataString(boardId)}", null, cancellationToken);
        await EnsureSuccessAsync(boardResponse, cancellationToken);
        using var boardDocument = JsonDocument.Parse(await boardResponse.Content.ReadAsStringAsync(cancellationToken));
        var labels = boardDocument.RootElement.GetProperty("included").GetProperty("labels").EnumerateArray()
            .Select(x => new PlankaLabel(
                x.GetProperty("id").GetString()!,
                x.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                x.TryGetProperty("position", out var position) && position.TryGetDouble(out var value) ? value : 0))
            .ToList();
        var desiredLabels = draft.Labels
            .Where(x => !x.StartsWith("priority:", StringComparison.OrdinalIgnoreCase))
            .Append($"priority: {draft.Priority}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nextPosition = labels.Count == 0 ? PositionStep : (long)labels.Max(x => x.Position) + PositionStep;

        foreach (var labelName in desiredLabels)
        {
            var label = labels.FirstOrDefault(x => string.Equals(x.Name, labelName, StringComparison.CurrentCultureIgnoreCase));
            if (label is null)
            {
                using var createResponse = await SendAsync(HttpMethod.Post, $"api/boards/{Uri.EscapeDataString(boardId)}/labels", new
                {
                    position = nextPosition,
                    name = labelName,
                    color = LabelColor(labelName, draft.Priority),
                }, cancellationToken);
                await EnsureSuccessAsync(createResponse, cancellationToken);
                var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                label = new(created.GetProperty("item").GetProperty("id").GetString()!, labelName, nextPosition);
                labels.Add(label);
                nextPosition += PositionStep;
            }
            using var attachResponse = await SendAsync(HttpMethod.Post, $"api/cards/{Uri.EscapeDataString(cardId)}/card-labels", new { labelId = label.Id }, cancellationToken);
            await EnsureSuccessAsync(attachResponse, cancellationToken);
        }
    }

    private static string LabelColor(string labelName, string priority)
    {
        if (labelName.StartsWith("priority:", StringComparison.OrdinalIgnoreCase))
        {
            return priority switch
            {
                "critical" => "berry-red",
                "high" => "pumpkin-orange",
                "low" => "fresh-salad",
                _ => "summer-sky",
            };
        }
        uint hash = 2166136261;
        foreach (var character in labelName.ToUpperInvariant()) hash = (hash ^ character) * 16777619;
        return LabelColors[hash % LabelColors.Length];
    }

    private sealed record PlankaLabel(string Id, string? Name, double Position);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken)
    {
        var baseUrl = settings.BaseUrl.TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativeUrl));
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private string BuildCardUrl(string id) => $"{settings.BaseUrl.TrimEnd('/')}/cards/{id}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"PLANKA HTTP {(int)response.StatusCode}: {body}");
        }
    }
}
