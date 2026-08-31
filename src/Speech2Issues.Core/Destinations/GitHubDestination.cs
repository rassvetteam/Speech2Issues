using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Destinations;

public sealed class GitHubDestination(HttpClient httpClient, GitHubSettings settings, string configuredToken) : ITaskDestination
{
    public DestinationKind Kind => DestinationKind.GitHub;

    public async Task<ConnectionCheck> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var repository = ValidateRepository();
            using var response = await SendAsync(HttpMethod.Get, $"repos/{repository}", null, cancellationToken);
            return response.IsSuccessStatusCode
                ? new(true, $"GitHub repository {repository} доступен.")
                : new(false, $"GitHub вернул HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    public Task<IReadOnlyList<DestinationTarget>> LoadTargetsAsync(CancellationToken cancellationToken = default)
    {
        var repository = ValidateRepository();
        return Task.FromResult<IReadOnlyList<DestinationTarget>>([new(repository, repository)]);
    }

    public async Task<CreatedTaskResult> CreateAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        var repository = ValidateRepository();
        var marker = TaskMarkdown.Marker(draft);
        using var existingResponse = await SendAsync(HttpMethod.Get, $"repos/{repository}/issues?state=all&per_page=100&sort=created&direction=desc", null, cancellationToken);
        if (existingResponse.IsSuccessStatusCode)
        {
            var existing = await existingResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            foreach (var issue in existing.EnumerateArray())
            {
                if (issue.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String &&
                    body.GetString()!.Contains(marker, StringComparison.Ordinal))
                {
                    return new("GitHub", issue.GetProperty("number").GetInt32().ToString(), issue.GetProperty("html_url").GetString()!, true);
                }
            }
        }

        var allowedLabels = await LoadLabelsAsync(repository, cancellationToken);
        var labels = draft.Labels.Where(label => allowedLabels.Contains(label)).ToArray();
        var filteredDraft = draft with { Labels = labels };
        var payload = new { title = draft.Title, body = TaskMarkdown.BuildBody(filteredDraft), labels };
        using var response = await SendAsync(HttpMethod.Post, $"repos/{repository}/issues", payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub HTTP {(int)response.StatusCode}: {responseBody}");
        }
        using var document = JsonDocument.Parse(responseBody);
        return new("GitHub", document.RootElement.GetProperty("number").GetInt32().ToString(), document.RootElement.GetProperty("html_url").GetString()!);
    }

    private async Task<HashSet<string>> LoadLabelsAsync(string repository, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"repos/{repository}/labels?per_page=100", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return json.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken)
    {
        var token = string.IsNullOrWhiteSpace(configuredToken) ? await ResolveGhTokenAsync(cancellationToken) : configuredToken.Trim();
        using var request = new HttpRequestMessage(method, new Uri(new Uri("https://api.github.com/"), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Speech2Issues/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private string ValidateRepository()
    {
        var value = settings.Repository.Trim().Trim('/');
        var parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("GitHub repository должен иметь формат owner/repo.");
        }
        return $"{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
    }

    public static async Task<string> ResolveGhTokenAsync(CancellationToken cancellationToken)
    {
        var knownPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        var executable = File.Exists(knownPath) ? knownPath : "gh";
        var startInfo = new ProcessStartInfo(executable, "auth token")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить GitHub CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            var error = (await errorTask).Trim();
            throw new InvalidOperationException($"GitHub CLI не авторизован. {error}");
        }
        return output;
    }
}
