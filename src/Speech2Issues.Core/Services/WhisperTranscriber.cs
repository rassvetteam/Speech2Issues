using System.Text.RegularExpressions;
using Speech2Issues.Core.Configuration;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace Speech2Issues.Core.Services;

public sealed partial class WhisperTranscriber(
    string modelsDirectory,
    SpeechRecognitionSettings settings,
    string? runtimeDirectory = null) : IAudioTranscriber, IDisposable
{
    private static readonly SemaphoreSlim ModelDownloadLock = new(1, 1);
    private readonly object _modelGate = new();
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
    private WhisperFactory? _cachedFactory;
    private Timer? _unloadTimer;
    private int _unloadGeneration;
    private int _activeModelUsers;
    private bool _disposed;

    public string ModelPath => GetModelPath(modelsDirectory, settings.Model);

    public static string GetModelPath(string modelsDirectory, string model) =>
        Path.Combine(modelsDirectory, GetModelFileName(ParseModel(model)));

    public async Task<string> PrepareAsync(
        IProgress<SpeechRecognitionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelType = ParseModel(settings.Model);
        Directory.CreateDirectory(modelsDirectory);
        await EnsureModelAsync(modelType, progress, cancellationToken);

        progress?.Report(new("Проверяю локальный runtime Whisper…"));
        await Task.Run(() =>
        {
            using var factory = CreateFactory(ModelPath);
            using var processor = CreateProcessor(factory);
        }, cancellationToken);
        return $"Whisper готов: {modelType}, язык {NormalizeLanguage(settings.Language)}, runtime {RuntimeOptions.LoadedLibrary}.";
    }

    public async Task<string> DownloadModelAsync(
        IProgress<SpeechRecognitionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelType = ParseModel(settings.Model);
        Directory.CreateDirectory(modelsDirectory);
        await EnsureModelAsync(modelType, progress, cancellationToken);
        return $"Модель Whisper {modelType} готова.";
    }

    public async Task<string> TranscribeAsync(
        float[] samples,
        IProgress<SpeechRecognitionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
        {
            throw new InvalidDataException("Whisper получил пустую запись.");
        }

        var modelType = ParseModel(settings.Model);
        Directory.CreateDirectory(modelsDirectory);
        await EnsureModelAsync(modelType, progress, cancellationToken);

        return await Task.Run(
            () => TranscribeCoreAsync(samples, modelType, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<string> TranscribeCoreAsync(
        float[] samples,
        GgmlType modelType,
        IProgress<SpeechRecognitionProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _transcriptionLock.WaitAsync(cancellationToken);
        var modelUseRegistered = false;
        try
        {
            WhisperFactory factory;
            lock (_modelGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                CancelScheduledUnloadLocked();
                _activeModelUsers++;
                modelUseRegistered = true;
                if (_cachedFactory is null)
                {
                    progress?.Report(new($"Whisper {modelType}: загружаю модель…"));
                    _cachedFactory = CreateFactory(ModelPath);
                }
                else
                {
                    progress?.Report(new($"Whisper {modelType}: использую загруженную модель…"));
                }
                factory = _cachedFactory;
            }

            using var processor = CreateProcessor(factory);
            var totalSeconds = Math.Max(1, (int)Math.Ceiling(samples.Length / 16_000d));
            var segments = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
            {
                var text = NormalizeWhitespace().Replace(segment.Text, " ").Trim();
                if (text.Length > 0)
                {
                    segments.Add(text);
                }

                progress?.Report(new(
                    $"Whisper распознаёт запись: {segment.End:mm\\:ss} из {TimeSpan.FromSeconds(totalSeconds):mm\\:ss}",
                    Math.Min(totalSeconds, Math.Max(0, (int)Math.Ceiling(segment.End.TotalSeconds))),
                    totalSeconds));
            }

            var transcript = string.Join(' ', segments).Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new InvalidDataException("Whisper не обнаружил разборчивую речь в записи.");
            }

            progress?.Report(new($"Whisper завершил распознавание ({RuntimeOptions.LoadedLibrary}).", totalSeconds, totalSeconds));
            return transcript;
        }
        finally
        {
            lock (_modelGate)
            {
                if (modelUseRegistered)
                {
                    _activeModelUsers--;
                    if (_disposed)
                    {
                        DisposeCachedFactoryLocked();
                    }
                    else
                    {
                        ScheduleModelUnloadLocked();
                    }
                }
            }
            _transcriptionLock.Release();
        }
    }

    private void ScheduleModelUnloadLocked()
    {
        CancelScheduledUnloadLocked();
        var delayMinutes = Math.Clamp(settings.ModelUnloadDelayMinutes, 0, 1440);
        if (delayMinutes == 0)
        {
            DisposeCachedFactoryLocked();
            return;
        }

        var generation = _unloadGeneration;
        _unloadTimer = new Timer(
            _ =>
            {
                lock (_modelGate)
                {
                    if (!_disposed && _activeModelUsers == 0 && generation == _unloadGeneration)
                    {
                        DisposeCachedFactoryLocked();
                        _unloadTimer?.Dispose();
                        _unloadTimer = null;
                    }
                }
            },
            null,
            TimeSpan.FromMinutes(delayMinutes),
            Timeout.InfiniteTimeSpan);
    }

    private void CancelScheduledUnloadLocked()
    {
        _unloadGeneration++;
        _unloadTimer?.Dispose();
        _unloadTimer = null;
    }

    private void DisposeCachedFactoryLocked()
    {
        _cachedFactory?.Dispose();
        _cachedFactory = null;
    }

    public void Dispose()
    {
        lock (_modelGate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelScheduledUnloadLocked();
            if (_activeModelUsers == 0)
            {
                DisposeCachedFactoryLocked();
            }
        }
    }

    private async Task EnsureModelAsync(
        GgmlType modelType,
        IProgress<SpeechRecognitionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsUsableModel(ModelPath))
        {
            return;
        }

        await ModelDownloadLock.WaitAsync(cancellationToken);
        try
        {
            if (IsUsableModel(ModelPath))
            {
                return;
            }

            var temporaryPath = ModelPath + ".download";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            progress?.Report(new($"Скачиваю модель Whisper {modelType} (это требуется один раз)…"));
            try
            {
                await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    modelType,
                    cancellationToken: cancellationToken);
                await using (var target = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, 1024 * 1024, cancellationToken);
                    await target.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, ModelPath, false);
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
                throw;
            }
        }
        finally
        {
            ModelDownloadLock.Release();
        }
    }

    private WhisperProcessor CreateProcessor(WhisperFactory factory)
    {
        var builder = factory.CreateBuilder()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 12));
        var language = NormalizeLanguage(settings.Language);
        return (language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language)).Build();
    }

    private WhisperFactory CreateFactory(string modelPath)
    {
        if (!string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            var runtimeName = settings.Runtime == WhisperRuntimeKind.Cuda ? "cuda" : "cpu";
            var runtimeRoot = Path.Combine(runtimeDirectory, runtimeName);
            var runtimeFilesDirectory = settings.Runtime == WhisperRuntimeKind.Cuda
                ? Path.Combine(runtimeRoot, "runtimes", "cuda", "win-x64")
                : Path.Combine(runtimeRoot, "runtimes", "win-x64");
            var libraryPath = Path.Combine(runtimeFilesDirectory, "whisper.dll");
            if (!File.Exists(libraryPath))
            {
                throw new FileNotFoundException($"Whisper runtime {runtimeName} не установлен.", libraryPath);
            }

            // Whisper.net uses LibraryPath as an anchor and probes the official
            // NuGet layout relative to its directory; it does not load that file directly.
            RuntimeOptions.LibraryPath = Path.Combine(runtimeRoot, "Speech2Issues.runtime-anchor");
            RuntimeOptions.RuntimeLibraryOrder = settings.Runtime == WhisperRuntimeKind.Cuda
                ? [RuntimeLibrary.Cuda]
                : [RuntimeLibrary.Cpu];
        }

        return WhisperFactory.FromPath(
            modelPath,
            new WhisperFactoryOptions
            {
                UseGpu = settings.Runtime == WhisperRuntimeKind.Cuda,
                GpuDevice = 0,
                UseFlashAttention = settings.Runtime == WhisperRuntimeKind.Cuda,
            });
    }

    private static bool IsUsableModel(string path) => File.Exists(path) && new FileInfo(path).Length > 1024 * 1024;

    private static string NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim().ToLowerInvariant();

    private static GgmlType ParseModel(string? model)
    {
        if (Enum.TryParse<GgmlType>(model, true, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Неизвестная модель Whisper: {model}.");
    }

    private static string GetModelFileName(GgmlType model) => model switch
    {
        GgmlType.LargeV1 => "ggml-large-v1.bin",
        GgmlType.LargeV2 => "ggml-large-v2.bin",
        GgmlType.LargeV3 => "ggml-large-v3.bin",
        GgmlType.LargeV3Turbo => "ggml-large-v3-turbo.bin",
        GgmlType.TinyEn => "ggml-tiny.en.bin",
        GgmlType.BaseEn => "ggml-base.en.bin",
        GgmlType.SmallEn => "ggml-small.en.bin",
        GgmlType.MediumEn => "ggml-medium.en.bin",
        _ => $"ggml-{model.ToString().ToLowerInvariant()}.bin",
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex NormalizeWhitespace();
}
