using System.Text;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"speech2issues-storage-{Guid.NewGuid():N}");

    [Fact]
    public async Task DpapiSecretsRoundTripWithoutPlaintextOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = new AppPaths(_root);
        var store = new SettingsStore(paths);
        var secrets = new AppSecrets { PlankaApiKey = "definitely-not-plaintext", AiApiToken = "ai-token-not-plaintext" };

        await store.SaveSecretsAsync(secrets);
        var raw = await File.ReadAllBytesAsync(paths.SecretsFile);
        var loaded = await store.LoadSecretsAsync();

        Assert.DoesNotContain("definitely-not-plaintext", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("ai-token-not-plaintext", Encoding.UTF8.GetString(raw));
        Assert.Equal(secrets.PlankaApiKey, loaded.PlankaApiKey);
        Assert.Equal(secrets.AiApiToken, loaded.AiApiToken);
    }

    [Fact]
    public async Task HistoryUpsertRetainsLatestStatus()
    {
        var paths = new AppPaths(_root);
        var history = new HistoryRepository(paths);
        await history.InitializeAsync();
        var entry = new HistoryEntry("id", DateTimeOffset.UtcNow, DestinationKind.Planka, "target", HistoryStatus.Pending, "title", "text", "{}", null, null, "audio.wav");
        await history.UpsertAsync(entry);
        await history.UpsertAsync(entry with { Status = HistoryStatus.Sent, ExternalUrl = "https://example.test/task", AudioPath = null });

        var loaded = Assert.Single(await history.GetRecentAsync());
        Assert.Equal(HistoryStatus.Sent, loaded.Status);
        Assert.Null(loaded.AudioPath);
        Assert.Equal("https://example.test/task", loaded.ExternalUrl);
    }

    [Fact]
    public async Task CreatedTaskCountIncludesOnlySuccessfullyDeliveredDrafts()
    {
        var paths = new AppPaths(_root);
        var history = new HistoryRepository(paths);
        await history.InitializeAsync();
        var template = new HistoryEntry("sent", DateTimeOffset.UtcNow, DestinationKind.Planka, "target", HistoryStatus.Sent, "title", "text", "{}", null, null, null);
        await history.UpsertAsync(template);
        await history.UpsertAsync(template with { Id = "partial", Status = HistoryStatus.PartiallySent });
        await history.UpsertAsync(template with { Id = "failed", Status = HistoryStatus.Failed });
        await history.UpsertAsync(template with { Id = "pending", Status = HistoryStatus.Pending });
        await history.UpsertAsync(template with { Id = "canceled", Status = HistoryStatus.Canceled });

        Assert.Equal(2, await history.CountCreatedTasksAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
