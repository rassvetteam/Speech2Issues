using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Speech2Issues.Core.Audio;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.App.Audio;

public sealed record RecordedAudio(float[] Samples, string WavPath, TimeSpan Duration, string? CaptureWarning = null);

public sealed class AudioRecordingSession : IDisposable
{
    private readonly WasapiCapture _microphone;
    private readonly WasapiLoopbackCapture _playback;
    private readonly WaveFileWriter _microphoneWriter;
    private readonly WaveFileWriter _playbackWriter;
    private readonly string _tempDirectory;
    private readonly string _microphonePath;
    private readonly string _playbackPath;
    private readonly AppPaths _paths;
    private readonly AudioSettings _settings;
    private readonly Stopwatch _stopwatch = new();
    private readonly TaskCompletionSource _microphoneStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _playbackStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _microphoneFirstTick;
    private long _playbackFirstTick;
    private Exception? _microphoneFailure;
    private Exception? _playbackFailure;
    private bool _started;
    private bool _stopped;

    public AudioRecordingSession(AppPaths paths, AudioSettings settings)
    {
        _paths = paths;
        _settings = settings;
        _tempDirectory = Path.Combine(paths.RecordingsDirectory, $"capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _microphonePath = Path.Combine(_tempDirectory, "microphone.wav");
        _playbackPath = Path.Combine(_tempDirectory, "playback.wav");

        var microphoneDevice = AudioDeviceService.ResolveMicrophone(settings.MicrophoneDeviceId);
        var playbackDevice = AudioDeviceService.ResolvePlayback(settings.PlaybackDeviceId);
        _microphone = new WasapiCapture(microphoneDevice);
        _playback = new WasapiLoopbackCapture(playbackDevice);
        _microphoneWriter = new WaveFileWriter(_microphonePath, _microphone.WaveFormat);
        _playbackWriter = new WaveFileWriter(_playbackPath, _playback.WaveFormat);

        _microphone.DataAvailable += OnMicrophoneData;
        _playback.DataAvailable += OnPlaybackData;
        _microphone.RecordingStopped += OnMicrophoneStopped;
        _playback.RecordingStopped += OnPlaybackStopped;
    }

    public event Action<float, float>? LevelsChanged;
    public event Action<string>? CaptureWarning;

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("Recording has already started.");
        }
        _started = true;
        _stopwatch.Start();
        _playback.StartRecording();
        _microphone.StartRecording();
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _stopped)
        {
            throw new InvalidOperationException("Recording is not active.");
        }
        _stopped = true;
        StopMicrophone();
        StopPlayback();
        await Task.WhenAll(_microphoneStopped.Task, _playbackStopped.Task).WaitAsync(cancellationToken);
        _stopwatch.Stop();
        _microphoneWriter.Dispose();
        _playbackWriter.Dispose();

        var microphone = ReadMono16K(_microphonePath);
        var playback = ReadMono16K(_playbackPath);
        var firstTick = new[] { _microphoneFirstTick, _playbackFirstTick }.Where(x => x > 0).DefaultIfEmpty(0).Min();
        var microphoneOffset = ToSamples(_microphoneFirstTick - firstTick);
        var playbackOffset = ToSamples(_playbackFirstTick - firstTick);
        var mixed = AudioMixer.Mix(microphone, playback, _settings.MicrophoneGain, _settings.PlaybackGain, microphoneOffset, playbackOffset);
        if (mixed.Length == 0)
        {
            throw new InvalidOperationException("Аудиоустройства остановились до получения записи.");
        }
        var outputPath = Path.Combine(_paths.RecordingsDirectory, $"recording-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(outputPath, PcmWave.EncodeMono16(mixed), cancellationToken);
        TryDeleteCaptureDirectory();
        return new(mixed, outputPath, TimeSpan.FromSeconds((double)mixed.Length / PcmWave.SampleRate), BuildCaptureWarning());
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs e)
    {
        Interlocked.CompareExchange(ref _microphoneFirstTick, Stopwatch.GetTimestamp(), 0);
        _microphoneWriter.Write(e.Buffer, 0, e.BytesRecorded);
        LevelsChanged?.Invoke(CalculatePeak(e.Buffer, e.BytesRecorded, _microphone.WaveFormat), -1);
    }

    private void OnPlaybackData(object? sender, WaveInEventArgs e)
    {
        Interlocked.CompareExchange(ref _playbackFirstTick, Stopwatch.GetTimestamp(), 0);
        _playbackWriter.Write(e.Buffer, 0, e.BytesRecorded);
        LevelsChanged?.Invoke(-1, CalculatePeak(e.Buffer, e.BytesRecorded, _playback.WaveFormat));
    }

    private void OnMicrophoneStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && Interlocked.CompareExchange(ref _microphoneFailure, e.Exception, null) is null)
        {
            CaptureWarning?.Invoke(DescribeCaptureFailure("Микрофон", "звук компьютера", e.Exception));
        }
        _microphoneStopped.TrySetResult();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && Interlocked.CompareExchange(ref _playbackFailure, e.Exception, null) is null)
        {
            CaptureWarning?.Invoke(DescribeCaptureFailure("Устройство вывода", "микрофон", e.Exception));
        }
        _playbackStopped.TrySetResult();
    }

    private void StopMicrophone()
    {
        try { _microphone.StopRecording(); }
        catch (Exception ex) { OnMicrophoneStopped(this, new StoppedEventArgs(ex)); }
    }

    private void StopPlayback()
    {
        try { _playback.StopRecording(); }
        catch (Exception ex) { OnPlaybackStopped(this, new StoppedEventArgs(ex)); }
    }

    private string? BuildCaptureWarning()
    {
        if (_microphoneFailure is null && _playbackFailure is null)
        {
            return null;
        }

        if (_microphoneFailure is not null && _playbackFailure is not null)
        {
            return "Во время записи стали недоступны микрофон и устройство вывода. Сохранена доступная часть записи.";
        }

        return _microphoneFailure is not null
            ? "Микрофон стал недоступен во время записи. Сохранен звук компьютера и доступная часть микрофона."
            : "Устройство вывода изменилось во время записи. Сохранен микрофон и доступная часть звука компьютера.";
    }

    private static string DescribeCaptureFailure(string failedSource, string continuingSource, Exception exception) =>
        unchecked((uint)exception.HResult) == 0x88890004
            ? $"{failedSource} стало недоступно или изменилось. Запись {continuingSource} продолжается."
            : $"Захват «{failedSource}» остановился. Запись {continuingSource} продолжается.";

    private static float[] ReadMono16K(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = new MonoSampleProvider(reader);
        if (provider.WaveFormat.SampleRate != PcmWave.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, PcmWave.SampleRate);
        }
        var result = new List<float>();
        var buffer = new float[PcmWave.SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return result.ToArray();
    }

    private static float CalculatePeak(byte[] buffer, int length, WaveFormat format)
    {
        var peak = 0f;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < length; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 1 < length; i += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f));
            }
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    private static int ToSamples(long ticks) => ticks <= 0 ? 0 : (int)Math.Round((double)ticks / Stopwatch.Frequency * PcmWave.SampleRate);

    private void TryDeleteCaptureDirectory()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_started && !_stopped)
        {
            try { _microphone.StopRecording(); } catch { }
            try { _playback.StopRecording(); } catch { }
        }
        _microphone.DataAvailable -= OnMicrophoneData;
        _playback.DataAvailable -= OnPlaybackData;
        _microphone.RecordingStopped -= OnMicrophoneStopped;
        _playback.RecordingStopped -= OnPlaybackStopped;
        _microphone.Dispose();
        _playback.Dispose();
        _microphoneWriter.Dispose();
        _playbackWriter.Dispose();
    }
}
