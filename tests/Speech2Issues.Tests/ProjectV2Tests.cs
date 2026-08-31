using System.Net;
using System.Text;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Services;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.Tests;

public sealed class ProjectV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"speech2issues-v2-{Guid.NewGuid():N}");

    [Fact]
    public void MigratesLegacyPlankaTargetIntoLocalProject()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            Planka = new PlankaSettings
            {
                ListId = "list-add",
                TargetDisplayName = "echo radars / to do / добавить",
            },
        };

        var changed = SettingsMigration.Migrate(settings);

        Assert.True(changed);
        var project = Assert.Single(settings.Projects);
        Assert.Equal("echo radars", project.Name);
        Assert.Equal(project.Id, settings.LastActiveProjectId);
        var binding = Assert.Single(project.Destinations);
        Assert.Equal("list-add", binding.Planka!.FallbackListId);
        Assert.Equal("list-add", Assert.Single(binding.Planka.AllowedTargets).ListId);
    }

    [Fact]
    public async Task OllamaModelsExcludeEmbeddingOnlyEntries()
    {
        var handler = new OllamaV2Handler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };

        var models = await new OllamaService(client, "chat:latest").ListModelsAsync();

        var model = Assert.Single(models);
        Assert.Equal("chat:latest", model.Name);
        Assert.Equal("7B", model.ParameterSize);
    }

    [Fact]
    public async Task InvalidPlankaRouteFallsBackToConfiguredList()
    {
        var handler = new OllamaV2Handler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };
        var targets = new[] { new DestinationTarget("todo", "Project / Board / todo"), new DestinationTarget("fix", "Project / Board / fix") };

        var route = await new OllamaService(client, "chat:latest").RoutePlankaAsync(new TaskDraft { Title = "Bug" }, targets, "fix");

        Assert.True(route.UsedFallback);
        Assert.Equal("fix", route.ListId);
    }

    [Fact]
    public void DestinationTargetDisplaysItsReadableName()
    {
        var target = new DestinationTarget("list-id", "echo radars / to do / fix");

        Assert.Equal("echo radars / to do / fix", target.ToString());
    }

    [Fact]
    public async Task HistoryStoresIndependentDeliveryResults()
    {
        var repository = new HistoryRepository(new AppPaths(_root));
        await repository.InitializeAsync();
        await repository.UpsertDeliveryAsync(new DeliveryAttempt("draft", "planka", DestinationKind.Planka, "list", "PLANKA / fix", HistoryStatus.Sent, "https://planka/card", null));
        await repository.UpsertDeliveryAsync(new DeliveryAttempt("draft", "github", DestinationKind.GitHub, "owner/repo", "owner/repo", HistoryStatus.Failed, null, "rate limit"));

        var deliveries = await repository.GetDeliveriesAsync("draft");

        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, x => x.Destination == DestinationKind.Planka && x.Status == HistoryStatus.Sent);
        Assert.Contains(deliveries, x => x.Destination == DestinationKind.GitHub && x.Status == HistoryStatus.Failed);
        Assert.Equal(HistoryStatus.PartiallySent, DeliveryCoordinator.AggregateStatus(deliveries));
        Assert.Equal(new[] { "github" }, DeliveryCoordinator.PendingBindingIds(deliveries));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class OllamaV2Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
                return Json("{\"models\":[{\"name\":\"chat:latest\",\"size\":10,\"details\":{\"parameter_size\":\"7B\",\"quantization_level\":\"Q4\"}},{\"name\":\"embed:latest\",\"size\":5,\"details\":{\"parameter_size\":\"1B\",\"quantization_level\":\"F16\"}}]}");
            if (request.RequestUri.AbsolutePath == "/api/show")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(body.Contains("embed:latest", StringComparison.Ordinal) ? "{\"capabilities\":[\"embedding\"]}" : "{\"capabilities\":[\"completion\"]}");
            }
            if (request.RequestUri.AbsolutePath == "/api/chat")
                return Json("{\"message\":{\"content\":\"{\\\"listId\\\":\\\"invented\\\",\\\"reason\\\":\\\"test\\\"}\"}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
