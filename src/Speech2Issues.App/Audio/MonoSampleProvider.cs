using NAudio.Wave;

namespace Speech2Issues.App.Audio;

internal sealed class MonoSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private float[] _sourceBuffer = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        if (_channels == 1)
        {
            return source.Read(buffer, offset, count);
        }
        var requested = count * _channels;
        if (_sourceBuffer.Length < requested)
        {
            _sourceBuffer = new float[requested];
        }
        var read = source.Read(_sourceBuffer, 0, requested);
        var frames = read / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += _sourceBuffer[frame * _channels + channel];
            }
            buffer[offset + frame] = sum / _channels;
        }
        return frames;
    }
}
