using Speech2Issues.Core.Audio;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Services;

public sealed class SpeechToIssuesService(IAudioTranscriber transcriber, IAiTaskProvider taskBuilder)
{
    public async Task<(string Transcript, IReadOnlyList<TaskDraft> Drafts)> ProcessAsync(
        float[] samples,
        IProgress<SpeechRecognitionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (AudioMixer.IsSilent(samples))
        {
            throw new InvalidOperationException("Запись слишком тихая: речь или звук ПК не обнаружены.");
        }

        var transcript = await transcriber.TranscribeAsync(samples, progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new InvalidDataException("Сервис распознавания не вернул транскрипцию.");
        }

        progress?.Report(new($"{taskBuilder.DisplayName} · {taskBuilder.Model}: формирую задачи…"));
        return (transcript, await taskBuilder.BuildDraftsAsync(transcript, cancellationToken));
    }
}
