namespace Speech2Issues.Core.Audio;

public static class AudioMixer
{
    public static float[] Mix(
        ReadOnlySpan<float> microphone,
        ReadOnlySpan<float> playback,
        float microphoneGain = 1f,
        float playbackGain = 0.65f,
        int microphoneOffsetSamples = 0,
        int playbackOffsetSamples = 0)
    {
        microphoneOffsetSamples = Math.Max(0, microphoneOffsetSamples);
        playbackOffsetSamples = Math.Max(0, playbackOffsetSamples);
        var length = Math.Max(microphoneOffsetSamples + microphone.Length, playbackOffsetSamples + playback.Length);
        var mixed = new float[length];

        for (var i = 0; i < microphone.Length; i++)
        {
            mixed[i + microphoneOffsetSamples] += microphone[i] * microphoneGain;
        }

        for (var i = 0; i < playback.Length; i++)
        {
            mixed[i + playbackOffsetSamples] += playback[i] * playbackGain;
        }

        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = SoftLimit(mixed[i]);
        }

        return mixed;
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }

    public static bool IsSilent(ReadOnlySpan<float> samples, float rmsThreshold = 0.001f)
    {
        if (samples.IsEmpty)
        {
            return true;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length) < rmsThreshold;
    }

    private static float SoftLimit(float value)
    {
        if (Math.Abs(value) <= 0.92f)
        {
            return value;
        }

        return MathF.Tanh(value) / MathF.Tanh(1f);
    }
}
