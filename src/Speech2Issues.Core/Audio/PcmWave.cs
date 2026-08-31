using System.Buffers.Binary;

namespace Speech2Issues.Core.Audio;

public static class PcmWave
{
    public const int SampleRate = 16_000;

    public static byte[] EncodeMono16(ReadOnlySpan<float> samples)
    {
        var dataLength = samples.Length * sizeof(short);
        var bytes = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + dataLength);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), SampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), dataLength);

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), (short)Math.Round(clamped * short.MaxValue));
        }

        return bytes;
    }

    public static float[] DecodeMono16(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Unsupported WAV file.");
        }

        var channels = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(22, 2));
        var bits = BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(34, 2));
        if (channels != 1 || bits != 16)
        {
            throw new InvalidDataException("Expected mono 16-bit PCM WAV.");
        }

        var dataOffset = FindDataOffset(wav);
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(dataOffset + 4, 4));
        var sampleData = wav.Slice(dataOffset + 8, Math.Min(dataLength, wav.Length - dataOffset - 8));
        var samples = new float[sampleData.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(sampleData.Slice(i * 2, 2)) / 32768f;
        }
        return samples;
    }

    private static int FindDataOffset(ReadOnlySpan<byte> wav)
    {
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            if (wav.Slice(offset, 4).SequenceEqual("data"u8))
            {
                return offset;
            }
            var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            offset += 8 + chunkLength + (chunkLength & 1);
        }
        throw new InvalidDataException("WAV data chunk is missing.");
    }
}
