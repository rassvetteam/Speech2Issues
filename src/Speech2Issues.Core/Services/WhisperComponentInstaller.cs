using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.Core.Services;

public sealed record ComponentInstallProgress(string Message, long BytesReceived = 0, long? TotalBytes = null)
{
    public double? Percentage => TotalBytes > 0 ? BytesReceived * 100d / TotalBytes.Value : null;
}

public sealed record InstalledComponentsManifest(
    int Version,
    string WhisperRuntimeVersion,
    WhisperRuntimeKind Runtime,
    string Model,
    DateTimeOffset InstalledAt);

public sealed record RuntimePackageSource(string Url, string Sha512);

public sealed class WhisperComponentInstaller(
    AppPaths paths,
    HttpClient httpClient,
    RuntimePackageSource? cpuSource = null,
    RuntimePackageSource? cudaSource = null)
{
    public const int CurrentSetupVersion = 1;
    public const string RuntimeVersion = "1.9.1";

    private const string CpuUrl = "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime/1.9.1/whisper.net.runtime.1.9.1.nupkg";
    private const string CpuSha512 = "oirjgKsR/CtqwA75asLFN4TyM9mgDoRXIpqrT623oTRMsek49dYvwhnEIT1w2ggYlaE/IdLEf8Bz2NEb0dOA7Q==";
    private const string CudaUrl = "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda.windows/1.9.1/whisper.net.runtime.cuda.windows.1.9.1.nupkg";
    private const string CudaSha512 = "J7ZyOZmhgrZCZLGNKNoIxWGcDQYYTsqiDYaHTqAiy/Yo3wN/7UAXqPEA9IICmGwp8X0WZBNkcD86934yALgEZQ==";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] CpuFiles = ["whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll"];
    private static readonly string[] CudaFiles = [.. CpuFiles, "ggml-cuda-whisper.dll"];

    public string CpuLibraryPath => Path.Combine(GetRuntimeFilesDirectory(WhisperRuntimeKind.Cpu), "whisper.dll");
    public string CudaLibraryPath => Path.Combine(GetRuntimeFilesDirectory(WhisperRuntimeKind.Cuda), "whisper.dll");

    public bool IsRuntimeInstalled(WhisperRuntimeKind kind)
    {
        var directory = GetRuntimeFilesDirectory(kind);
        var required = kind == WhisperRuntimeKind.Cuda ? CudaFiles : CpuFiles;
        return required.All(file => File.Exists(Path.Combine(directory, file)) && new FileInfo(Path.Combine(directory, file)).Length > 0);
    }

    public bool IsSetupComplete(AppSettings settings)
    {
        if (settings.SetupVersion < CurrentSetupVersion || !IsRuntimeInstalled(settings.SpeechRecognition.Runtime))
        {
            return false;
        }

        try
        {
            if (!File.Exists(paths.ComponentsManifestFile)) return false;
            var manifest = JsonSerializer.Deserialize<InstalledComponentsManifest>(File.ReadAllText(paths.ComponentsManifestFile), JsonOptions);
            if (manifest is null ||
                manifest.Version != CurrentSetupVersion ||
                !string.Equals(manifest.WhisperRuntimeVersion, RuntimeVersion, StringComparison.Ordinal) ||
                manifest.Runtime != settings.SpeechRecognition.Runtime ||
                !string.Equals(manifest.Model, settings.SpeechRecognition.Model, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var modelPath = WhisperTranscriber.GetModelPath(paths.ModelsDirectory, settings.SpeechRecognition.Model);
        return File.Exists(modelPath) && new FileInfo(modelPath).Length > 1024 * 1024;
    }

    public async Task EnsureRuntimeAsync(
        WhisperRuntimeKind kind,
        IProgress<ComponentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        MigrateInstalledRuntimeLayout(WhisperRuntimeKind.Cpu);
        if (!IsRuntimeInstalled(WhisperRuntimeKind.Cpu))
        {
            var source = cpuSource ?? new RuntimePackageSource(CpuUrl, CpuSha512);
            await DownloadAndInstallAsync("CPU runtime Whisper", source.Url, source.Sha512, "cpu", CpuFiles, progress, cancellationToken);
        }

        if (kind == WhisperRuntimeKind.Cuda && !IsRuntimeInstalled(WhisperRuntimeKind.Cuda))
        {
            MigrateInstalledRuntimeLayout(WhisperRuntimeKind.Cuda);
        }

        if (kind == WhisperRuntimeKind.Cuda && !IsRuntimeInstalled(WhisperRuntimeKind.Cuda))
        {
            var source = cudaSource ?? new RuntimePackageSource(CudaUrl, CudaSha512);
            await DownloadAndInstallAsync("CUDA runtime Whisper", source.Url, source.Sha512, "cuda", CudaFiles, progress, cancellationToken);
        }
    }

    public Task SaveManifestAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(
            paths.ComponentsManifestFile,
            new InstalledComponentsManifest(CurrentSetupVersion, RuntimeVersion, settings.SpeechRecognition.Runtime, settings.SpeechRecognition.Model, DateTimeOffset.UtcNow),
            JsonOptions,
            cancellationToken);

    private async Task DownloadAndInstallAsync(
        string displayName,
        string url,
        string expectedSha512,
        string destinationName,
        IReadOnlyCollection<string> requiredFiles,
        IProgress<ComponentInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.Combine(paths.DownloadsDirectory, destinationName + ".nupkg.download");
        var stagingDirectory = Path.Combine(paths.RuntimeDirectory, "." + destinationName + ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new($"Скачиваю {displayName}…"));
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                var buffer = new byte[1024 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new($"Скачиваю {displayName}…", received, total));
                }
                await target.FlushAsync(cancellationToken);
            }

            await VerifyHashAsync(packagePath, expectedSha512, cancellationToken);
            progress?.Report(new($"Устанавливаю {displayName}…"));
            var runtimeKind = destinationName == "cuda" ? WhisperRuntimeKind.Cuda : WhisperRuntimeKind.Cpu;
            var stagingFilesDirectory = Path.Combine(stagingDirectory, GetRuntimeRelativeDirectory(runtimeKind));
            Directory.CreateDirectory(stagingFilesDirectory);
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var prefix = "build/win-x64/";
                foreach (var fileName in requiredFiles)
                {
                    var entry = archive.Entries.FirstOrDefault(item =>
                        string.Equals(item.FullName.Replace('\\', '/'), prefix + fileName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"В пакете {displayName} отсутствует {fileName}.");
                    var targetPath = Path.Combine(stagingFilesDirectory, fileName);
                    await using var input = entry.Open();
                    await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                    await input.CopyToAsync(output, cancellationToken);
                }
            }

            ReplaceDirectory(stagingDirectory, Path.Combine(paths.RuntimeDirectory, destinationName));
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
    }

    public void MigrateInstalledRuntimeLayout(WhisperRuntimeKind kind)
    {
        if (IsRuntimeInstalled(kind)) return;

        var destinationName = kind == WhisperRuntimeKind.Cuda ? "cuda" : "cpu";
        var legacyDirectory = Path.Combine(paths.RuntimeDirectory, destinationName);
        var required = kind == WhisperRuntimeKind.Cuda ? CudaFiles : CpuFiles;
        if (!required.All(file => File.Exists(Path.Combine(legacyDirectory, file)) && new FileInfo(Path.Combine(legacyDirectory, file)).Length > 0))
        {
            return;
        }

        var stagingDirectory = Path.Combine(paths.RuntimeDirectory, "." + destinationName + ".migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stagingFilesDirectory = Path.Combine(stagingDirectory, GetRuntimeRelativeDirectory(kind));
            Directory.CreateDirectory(stagingFilesDirectory);
            foreach (var fileName in required)
            {
                File.Copy(Path.Combine(legacyDirectory, fileName), Path.Combine(stagingFilesDirectory, fileName));
            }

            ReplaceDirectory(stagingDirectory, legacyDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
    }

    private string GetRuntimeFilesDirectory(WhisperRuntimeKind kind) =>
        Path.Combine(paths.RuntimeDirectory, kind == WhisperRuntimeKind.Cuda ? "cuda" : "cpu", GetRuntimeRelativeDirectory(kind));

    private static string GetRuntimeRelativeDirectory(WhisperRuntimeKind kind) => kind == WhisperRuntimeKind.Cuda
        ? Path.Combine("runtimes", "cuda", "win-x64")
        : Path.Combine("runtimes", "win-x64");

    private static async Task VerifyHashAsync(string path, string expectedBase64, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(expectedBase64)))
        {
            throw new InvalidDataException("Контрольная сумма загруженного Whisper runtime не совпала.");
        }
    }

    private static void ReplaceDirectory(string stagingDirectory, string destinationDirectory)
    {
        var backup = destinationDirectory + ".old-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(destinationDirectory)) Directory.Move(destinationDirectory, backup);
            Directory.Move(stagingDirectory, destinationDirectory);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
        catch
        {
            if (!Directory.Exists(destinationDirectory) && Directory.Exists(backup)) Directory.Move(backup, destinationDirectory);
            throw;
        }
    }
}
