using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Services;

namespace Speech2Issues.Tests;

public sealed class AiProviderTests
{
    [Fact]
    public async Task OpenAiCompatibleUsesModelsChatSchemaAndBearerToken()
    {
        var handler = new OpenAiHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://lm-studio/v1/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        var service = new OpenAiCompatibleService(client, "qwen", "LM Studio");

        var models = await service.ListModelsAsync();
        var drafts = await service.BuildDraftsAsync("Исправить кнопку.");

        Assert.Equal("qwen", Assert.Single(models).Name);
        Assert.Equal("Исправить кнопку", Assert.Single(drafts).Title);
        Assert.Equal(new[] { "/v1/models", "/v1/chat/completions" }, handler.Paths);
        Assert.All(handler.Authorizations, value => Assert.Equal("Bearer secret-token", value));
        Assert.Contains("\"type\":\"json_schema\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task UnsupportedJsonSchemaRetriesOnceWithJsonObject()
    {
        var handler = new OpenAiHandler(failFirstChat: HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") };

        var drafts = await new OpenAiCompatibleService(client, "model", "Provider").BuildDraftsAsync("Задача");

        Assert.Single(drafts);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"type\":\"json_object\"", handler.Bodies[1]);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotRetryWithFallback()
    {
        var handler = new OpenAiHandler(failFirstChat: HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new OpenAiCompatibleService(client, "model", "Provider").BuildDraftsAsync("Задача"));

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task OpenAiCompatibleRoutesOnlyToAllowedPlankaTarget()
    {
        var handler = new OpenAiHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://lm-studio/v1/") };
        var targets = new[] { new DestinationTarget("todo", "To do"), new DestinationTarget("fix", "Fix") };

        var route = await new OpenAiCompatibleService(client, "qwen", "LM Studio")
            .RoutePlankaAsync(new TaskDraft { Title = "Bug", Description = "Fix it" }, targets, "todo");

        Assert.Equal("fix", route.ListId);
        Assert.False(route.UsedFallback);
    }

    [Fact]
    public void LegacyOllamaSettingsMigrateToProviderSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"schemaVersion":2,"ollamaUrl":"http://server:11434","ollamaModel":"old-model"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(SettingsMigration.Migrate(settings));
        Assert.Equal(4, settings.SchemaVersion);
        Assert.Equal(5, settings.SpeechRecognition.ModelUnloadDelayMinutes);
        Assert.Equal(AiProviderKind.Ollama, settings.AiProvider.Kind);
        Assert.Equal("http://server:11434", settings.AiProvider.BaseUrl);
        Assert.Equal("old-model", settings.AiProvider.Model);
        Assert.Null(settings.LegacyOllamaUrl);
        Assert.Null(settings.LegacyOllamaModel);
    }

    private sealed class OpenAiHandler(HttpStatusCode? failFirstChat = null) : HttpMessageHandler
    {
        private int _chatCalls;
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string?> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get)
            {
                return Json("{\"data\":[{\"id\":\"qwen\"}]}");
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            _chatCalls++;
            if (_chatCalls == 1 && failFirstChat is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("unsupported") };
            }

            var content = Bodies[^1].Contains("speech2issues_route", StringComparison.Ordinal)
                ? "{\"listId\":\"fix\",\"reason\":\"bug\"}"
                : "{\"tasks\":[{\"id\":\"s2i-test\",\"title\":\"Исправить кнопку\",\"description\":\"Описание\",\"acceptanceCriteria\":[\"Готово\"],\"labels\":[\"bug\"],\"priority\":\"medium\",\"dueDate\":null}]}";
            return Json("{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
