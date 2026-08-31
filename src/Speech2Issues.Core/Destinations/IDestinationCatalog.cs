using Speech2Issues.Core.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Speech2Issues.Core.Destinations;

public interface IDestinationCatalog
{
    DestinationKind Kind { get; }
    Task<IReadOnlyList<ExternalProjectCandidate>> DiscoverProjectsAsync(CancellationToken cancellationToken = default);
}

public sealed class PlankaCatalog(PlankaDestination destination) : IDestinationCatalog
{
    public DestinationKind Kind => DestinationKind.Planka;

    public async Task<IReadOnlyList<ExternalProjectCandidate>> DiscoverProjectsAsync(CancellationToken cancellationToken = default)
    {
        var targets = await destination.LoadTargetsAsync(cancellationToken);
        return targets
            .GroupBy(x => new { Id = x.ProjectId ?? x.ProjectName ?? "planka", Name = x.ProjectName ?? "PLANKA" })
            .Select(x => new ExternalProjectCandidate(Kind, x.Key.Id, x.Key.Name, x.ToArray()))
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}

public sealed class ObsidianCatalog(ObsidianDestination destination) : IDestinationCatalog
{
    public DestinationKind Kind => DestinationKind.Obsidian;

    public async Task<IReadOnlyList<ExternalProjectCandidate>> DiscoverProjectsAsync(CancellationToken cancellationToken = default)
    {
        var targets = await destination.LoadTargetsAsync(cancellationToken);
        return targets.Select(x => new ExternalProjectCandidate(Kind, x.Id, x.DisplayName, [x])).ToArray();
    }
}

public sealed class GitHubCatalog(HttpClient httpClient, string configuredToken) : IDestinationCatalog
{
    public DestinationKind Kind => DestinationKind.GitHub;

    public async Task<IReadOnlyList<ExternalProjectCandidate>> DiscoverProjectsAsync(CancellationToken cancellationToken = default)
    {
        var token = string.IsNullOrWhiteSpace(configuredToken)
            ? await GitHubDestination.ResolveGhTokenAsync(cancellationToken)
            : configuredToken.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Speech2Issues/2.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var repos = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return repos.EnumerateArray()
            .Where(x => !x.TryGetProperty("has_issues", out var hasIssues) || hasIssues.GetBoolean())
            .Select(x => x.GetProperty("full_name").GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new ExternalProjectCandidate(Kind, x!, x!, [new DestinationTarget(x!, x!)]))
            .ToArray();
    }
}
