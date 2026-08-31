using Speech2Issues.Core.Audio;

namespace Speech2Issues.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void MixerCombinesBothSourcesAndLimitsPeak()
    {
        var microphone = new[] { 0.5f, 0.5f, 0f };
        var playback = new[] { 0f, 0.5f, 1f };
        var result = AudioMixer.Mix(microphone, playback, 1f, 1f);
        Assert.Equal(3, result.Length);
        Assert.Equal(0.5f, result[0], 3);
        Assert.True(result[1] > 0.9f && result[1] <= 1f);
        Assert.True(result[2] > 0.9f && result[2] <= 1f);
    }

    [Fact]
    public void MixerHonorsIndependentOffsets()
    {
        var result = AudioMixer.Mix(new[] { 1f }, new[] { 0.5f }, microphoneOffsetSamples: 2, playbackOffsetSamples: 0);
        Assert.Equal(3, result.Length);
        Assert.Equal(0.325f, result[0], 3);
        Assert.Equal(0f, result[1]);
        Assert.True(result[2] > 0.9f);
    }

    [Fact]
    public void ChunkerUsesTwentyFiveSecondsWithOneSecondOverlap()
    {
        var samples = new float[PcmWave.SampleRate * 50];
        var chunks = AudioChunker.Split(samples);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(PcmWave.SampleRate * 25, chunks[0].Length);
        Assert.Equal(PcmWave.SampleRate * 25, chunks[1].Length);
        Assert.Equal(PcmWave.SampleRate * 2, chunks[2].Length);
    }

    [Fact]
    public void TranscriptMergerRemovesOverlap()
    {
        var result = TranscriptMerger.Merge(["исправить критический баг в рендерере", "баг в рендерере и добавить тест"]);
        Assert.Equal("исправить критический баг в рендерере и добавить тест", result);
    }

    [Fact]
    public void PcmWaveRoundTripsSamples()
    {
        var input = new[] { -1f, -0.5f, 0f, 0.5f, 1f };
        var decoded = PcmWave.DecodeMono16(PcmWave.EncodeMono16(input));
        Assert.Equal(input.Length, decoded.Length);
        for (var i = 0; i < input.Length; i++)
        {
            Assert.InRange(Math.Abs(input[i] - decoded[i]), 0, 0.0001f);
        }
    }
}
