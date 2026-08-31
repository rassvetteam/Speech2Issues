using System.Net;
using System.Text;
using Speech2Issues.Core.Services;

namespace Speech2Issues.Tests;

public sealed class OllamaServiceTests
{
    [Fact]
    public async Task BuildDraftUsesTextOnlyNativeStructuredEndpoint()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };
        var service = new OllamaService(client, "gemma4:12b");

        var draft = await service.BuildDraftAsync("Нужно исправить проблему авторизации.");

        Assert.Equal("Исправить проблему", draft.Title);
        Assert.Single(handler.Paths);
        Assert.Equal("/api/chat", handler.Paths[0]);
        Assert.DoesNotContain("input_audio", handler.Bodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Нужно исправить проблему авторизации", handler.Prompts[0]);
        Assert.Contains("acceptanceCriteria", handler.Bodies[0]);
        Assert.Contains("Never write Markdown checkboxes", handler.Prompts[0]);
    }

    [Fact]
    public async Task BuildDraftsReturnsSeveralIndependentTasks()
    {
        var handler = new RecordingHandler(twoTasks: true);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };

        var drafts = await new OllamaService(client, "gemma4:12b").BuildDraftsAsync("Исправить вход и добавить экспорт.");

        Assert.Equal(2, drafts.Count);
        Assert.Equal(new[] { "Исправить проблему", "Добавить экспорт" }, drafts.Select(x => x.Title));
        Assert.Equal(2, drafts.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public async Task PipelineUsesSeparateTranscriberBeforeGemma()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };
        var transcriber = new FakeTranscriber("точный текст из Whisper");
        var pipeline = new SpeechToIssuesService(transcriber, new OllamaService(client, "gemma4:12b"));

        var result = await pipeline.ProcessAsync(Enumerable.Repeat(0.1f, 16_000).ToArray());

        Assert.Equal("точный текст из Whisper", result.Transcript);
        Assert.Single(result.Drafts);
        Assert.Equal(1, transcriber.Calls);
        Assert.Single(handler.Paths);
        Assert.Contains("точный текст из Whisper", handler.Prompts[0]);
    }

    private sealed class FakeTranscriber(string transcript) : IAudioTranscriber
    {
        public int Calls { get; private set; }

        public Task<string> TranscribeAsync(
            float[] samples,
            IProgress<SpeechRecognitionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            progress?.Report(new("Готово", 1, 1));
            return Task.FromResult(transcript);
        }
    }

    private sealed class RecordingHandler(bool twoTasks = false) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string> Prompts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            using var requestJson = System.Text.Json.JsonDocument.Parse(Bodies[^1]);
            Prompts.Add(requestJson.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            if (request.RequestUri.AbsolutePath == "/api/chat")
            {
                var second = twoTasks
                    ? ", {\"id\":\"s2i-second\",\"title\":\"Добавить экспорт\",\"description\":\"Описание 2\",\"acceptanceCriteria\":[\"PDF скачивается\"],\"labels\":[\"feature\"],\"priority\":\"medium\",\"dueDate\":null}"
                    : string.Empty;
                var batch = "{\"tasks\":[{\"id\":\"s2i-test\",\"title\":\"Исправить проблему\",\"description\":\"Описание\",\"acceptanceCriteria\":[\"Готово\"],\"labels\":[\"bug\"],\"priority\":\"high\",\"dueDate\":null}" + second + "]}";
                return Json("{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(batch) + "}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
