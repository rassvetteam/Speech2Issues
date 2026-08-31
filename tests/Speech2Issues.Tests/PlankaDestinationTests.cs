using System.Net;
using System.Text;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Tests;

public sealed class PlankaDestinationTests
{
    [Fact]
    public async Task CreatesNativeChecklistAndLabelsWithoutVisibleMarker()
    {
        var handler = new PlankaHandler();
        using var client = new HttpClient(handler);
        var destination = new PlankaDestination(client, new PlankaSettings
        {
            BaseUrl = "http://planka.test",
            ListId = "list-1",
        }, "test-key");
        var draft = Draft();

        var result = await destination.CreateAsync(draft);

        Assert.Equal("42", result.ExternalId);
        Assert.DoesNotContain("speech2issues", handler.CardBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- [ ]", handler.CardBody);
        Assert.DoesNotContain("Приоритет", handler.CardBody);
        Assert.Contains("\"position\":32768", handler.PostBody);
        Assert.DoesNotContain("dueDate", handler.PostBody);
        Assert.Equal(2, handler.TaskBodies.Count);
        Assert.Contains(handler.TaskBodies, x => x.Contains("First check"));
        Assert.Contains(handler.TaskBodies, x => x.Contains("Second check"));
        Assert.Contains(handler.CreatedLabelBodies, x => x.Contains("Bug"));
        Assert.Contains(handler.CreatedLabelBodies, x => x.Contains("priority: high"));
        Assert.Equal(3, handler.AttachedLabelBodies.Count);
        Assert.True(handler.SawApiKey);
    }

    [Fact]
    public async Task IncludesDueDateWhenPresent()
    {
        var handler = new PlankaHandler();
        using var client = new HttpClient(handler);
        var destination = new PlankaDestination(client, new PlankaSettings
        {
            BaseUrl = "http://planka.test",
            ListId = "list-1",
        }, "test-key");

        await destination.CreateAsync(Draft() with { DueDate = "2024-01-31" });

        Assert.Contains("\"dueDate\":\"2024-01-31T23:59:00.000Z\"", handler.CardBody);
    }

    [Fact]
    public async Task CatalogSkipsListsWithoutNames()
    {
        using var client = new HttpClient(new PlankaCatalogHandler());
        var destination = new PlankaDestination(client, new PlankaSettings { BaseUrl = "http://planka.test" }, "test-key");

        var targets = await destination.LoadTargetsAsync();

        var target = Assert.Single(targets);
        Assert.Equal("list-named", target.Id);
        Assert.Equal("Project / Board / todo", target.DisplayName);
    }

    private static TaskDraft Draft() => new()
    {
        Id = "s2i-test",
        Title = "Test card",
        Description = "Description",
        Transcript = "Transcript",
        AcceptanceCriteria = ["First check", "Second check"],
        Labels = ["UI", "Bug"],
        Priority = "high",
    };

    private sealed class PlankaHandler : HttpMessageHandler
    {
        public string PostBody => CardBody;
        public string CardBody { get; private set; } = string.Empty;
        public List<string> TaskBodies { get; } = [];
        public List<string> CreatedLabelBodies { get; } = [];
        public List<string> AttachedLabelBodies { get; } = [];
        public bool SawApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SawApiKey |= request.Headers.TryGetValues("X-Api-Key", out var values) && values.Contains("test-key");
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/lists/list-1")
            {
                return Json("{\"item\":{\"id\":\"list-1\",\"boardId\":\"board-1\"},\"included\":{\"cards\":[{\"id\":\"1\",\"position\":16384,\"description\":\"old\"}]}}");
            }
            if (request.Method == HttpMethod.Get && path == "/api/boards/board-1")
                return Json("{\"included\":{\"labels\":[{\"id\":\"label-ui\",\"name\":\"ui\",\"position\":65536}]}}");

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path == "/api/lists/list-1/cards")
            {
                CardBody = body;
                return Json("{\"item\":{\"id\":\"42\"}}");
            }
            if (path == "/api/cards/42/task-lists") return Json("{\"item\":{\"id\":\"task-list-1\"}}");
            if (path == "/api/task-lists/task-list-1/tasks")
            {
                TaskBodies.Add(body);
                return Json("{\"item\":{\"id\":\"task-1\"}}");
            }
            if (path == "/api/boards/board-1/labels")
            {
                CreatedLabelBodies.Add(body);
                return Json($"{{\"item\":{{\"id\":\"label-{CreatedLabelBodies.Count}\"}}}}");
            }
            if (path == "/api/cards/42/card-labels")
            {
                AttachedLabelBodies.Add(body);
                return Json("{\"item\":{\"id\":\"card-label-1\"}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class PlankaCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/projects" => "{\"items\":[{\"id\":\"project-1\",\"name\":\"Project\"}],\"included\":{\"boards\":[{\"id\":\"board-1\",\"name\":\"Board\",\"projectId\":\"project-1\"}]}}",
                "/api/boards/board-1" => "{\"included\":{\"lists\":[{\"id\":\"list-empty\",\"name\":\"  \"},{\"id\":\"list-named\",\"name\":\"todo\"}]}}",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
