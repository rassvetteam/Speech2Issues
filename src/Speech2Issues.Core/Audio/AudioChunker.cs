namespace Speech2Issues.Core.Audio;

public static class AudioChunker
{
    public static IReadOnlyList<float[]> Split(ReadOnlySpan<float> samples, int sampleRate = PcmWave.SampleRate, int seconds = 25, int overlapSeconds = 1)
    {
        if (seconds <= 0 || overlapSeconds < 0 || overlapSeconds >= seconds)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        if (samples.IsEmpty)
        {
            return [];
        }

        var chunkSize = checked(sampleRate * seconds);
        var step = checked(sampleRate * (seconds - overlapSeconds));
        var chunks = new List<float[]>();
        for (var offset = 0; offset < samples.Length; offset += step)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            chunks.Add(samples.Slice(offset, length).ToArray());
            if (offset + length >= samples.Length)
            {
                break;
            }
        }
        return chunks;
    }
}
