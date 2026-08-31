using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Services;

namespace Speech2Issues.Tests;

public sealed class OllamaLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task LocalWhisperTranscribesKnownWave()
    {
        var root = Path.Combine(Path.GetTempPath(), "Speech2Issues-WhisperTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var download = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var wav = await download.GetByteArrayAsync("https://github.com/ggml-org/whisper.cpp/raw/master/samples/jfk.wav");
            var samples = Speech2Issues.Core.Audio.PcmWave.DecodeMono16(wav);
            var whisper = new WhisperTranscriber(root, new SpeechRecognitionSettings { Model = "Tiny", Language = "en" });

            var transcript = await whisper.TranscribeAsync(samples);

            Assert.Contains("Americans", transcript, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task InstalledGemmaBuildsTaskFromTextOnly()
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(10) };
        var service = new OllamaService(client, "gemma4:12b");

        var draft = await service.BuildDraftAsync("Создать кнопку экспорта отчёта в PDF. Критерий: файл скачивается без ошибки.");

        Assert.False(string.IsNullOrWhiteSpace(draft.Title));
        Assert.NotEmpty(draft.AcceptanceCriteria);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task InstalledGemmaSplitsIndependentRequestsIntoSeparateTasks()
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(10) };
        var service = new OllamaService(client, "gemma4:12b");

        var drafts = await service.BuildDraftsAsync("Нужно сделать две независимые задачи: первая — исправить цвет кнопки входа, вторая — добавить экспорт отчёта в PDF. Для экспорта добавь проверку, что файл скачивается.");

        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, x => Assert.False(string.IsNullOrWhiteSpace(x.Title)));
        Assert.Contains(drafts, x => x.AcceptanceCriteria.Count > 0);
    }
}
