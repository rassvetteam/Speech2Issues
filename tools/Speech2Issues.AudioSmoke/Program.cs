using Speech2Issues.App.Audio;
using Speech2Issues.Core.Audio;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Services;
using Speech2Issues.Core.Storage;

if (args.Length >= 2 && args[0].Equals("--transcribe", StringComparison.OrdinalIgnoreCase))
{
    var inputPath = Path.GetFullPath(args[1]);
    var model = args.Length >= 3 ? args[2] : "LargeV3Turbo";
    var wav = await File.ReadAllBytesAsync(inputPath);
    var samples = PcmWave.DecodeMono16(wav);
    var appPaths = new AppPaths();
    appPaths.EnsureCreated();
    var progress = new Progress<SpeechRecognitionProgress>(value => Console.WriteLine(value.Message));
    var whisper = new WhisperTranscriber(
        appPaths.ModelsDirectory,
        new SpeechRecognitionSettings { Model = model, Language = "auto" });
    var transcript = await whisper.TranscribeAsync(samples, progress);
    Console.WriteLine();
    Console.WriteLine(transcript);
    return;
}

var seconds = args.Length > 0 && int.TryParse(args[0], out var parsed) ? Math.Clamp(parsed, 1, 30) : 3;
var smokeRoot = Path.Combine(Path.GetTempPath(), $"speech2issues-audio-smoke-{Guid.NewGuid():N}");
var paths = new AppPaths(smokeRoot);
paths.EnsureCreated();

try
{
    var microphones = AudioDeviceService.GetMicrophones();
    var playbackDevices = AudioDeviceService.GetPlaybackDevices();
    Console.WriteLine($"Microphone: {microphones.FirstOrDefault()}");
    Console.WriteLine($"Playback:   {playbackDevices.FirstOrDefault()}");

    using var session = new AudioRecordingSession(paths, new AudioSettings());
    var microphonePeak = 0f;
    var playbackPeak = 0f;
    session.LevelsChanged += (microphone, playback) =>
    {
        if (microphone >= 0) microphonePeak = Math.Max(microphonePeak, microphone);
        if (playback >= 0) playbackPeak = Math.Max(playbackPeak, playback);
    };
    session.Start();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    var result = await session.StopAsync();
    Console.WriteLine($"Samples: {result.Samples.Length}; duration: {result.Duration.TotalSeconds:F2}s; mixed peak: {AudioMixer.Peak(result.Samples):F4}");
    Console.WriteLine($"Microphone peak: {microphonePeak:F4}; playback peak: {playbackPeak:F4}");
    Console.WriteLine($"WAV: {result.WavPath}");
    Environment.ExitCode = result.Samples.Length > 0 ? 0 : 2;
}
finally
{
    if (Directory.Exists(smokeRoot))
    {
        Directory.Delete(smokeRoot, true);
    }
}
