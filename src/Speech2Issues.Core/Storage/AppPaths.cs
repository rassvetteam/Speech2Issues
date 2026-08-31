namespace Speech2Issues.Core.Storage;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Speech2Issues");
        SettingsFile = Path.Combine(Root, "settings.json");
        SecretsFile = Path.Combine(Root, "secrets.bin");
        HistoryFile = Path.Combine(Root, "history.db");
        RecordingsDirectory = Path.Combine(Root, "recordings");
        ModelsDirectory = Path.Combine(Root, "models");
        RuntimeDirectory = Path.Combine(Root, "runtime");
        DownloadsDirectory = Path.Combine(Root, "downloads");
        ComponentsManifestFile = Path.Combine(Root, "components.json");
        LogsDirectory = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string SettingsFile { get; }
    public string SecretsFile { get; }
    public string HistoryFile { get; }
    public string RecordingsDirectory { get; }
    public string ModelsDirectory { get; }
    public string RuntimeDirectory { get; }
    public string DownloadsDirectory { get; }
    public string ComponentsManifestFile { get; }
    public string LogsDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RecordingsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
