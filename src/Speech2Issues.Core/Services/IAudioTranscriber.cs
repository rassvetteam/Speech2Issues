namespace Speech2Issues.Core.Services;

public sealed record SpeechRecognitionProgress(string Message, int Current = 0, int Total = 0)
{
    public bool IsIndeterminate => Total <= 0;
}

public interface IAudioTranscriber
{
    Task<string> TranscribeAsync(
        float[] samples,
        IProgress<SpeechRecognitionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
