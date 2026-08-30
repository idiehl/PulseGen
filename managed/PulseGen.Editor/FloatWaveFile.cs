using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace PulseGen.Editor;

internal sealed record FloatWaveBuffer(int SampleRate, int Channels, float[] InterleavedSamples)
{
    public int Frames => InterleavedSamples.Length / Channels;
    public double DurationSeconds => (double)Frames / SampleRate;
}

internal static class FloatWaveFile
{
    private const long MaximumBytes = 1024L * 1024 * 1024;

    public static FloatWaveBuffer Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 44 || file.Length > MaximumBytes || file.Length > int.MaxValue)
        {
            throw new InvalidDataException("Spectral audio must be a non-empty WAVE file no larger than 1 GiB.");
        }

        var bytes = File.ReadAllBytes(file.FullName);
        var data = bytes.AsSpan();
        if (!data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Spectral audio must use the RIFF WAVE container.");
        }

        ushort? formatCode = null;
        ushort? channels = null;
        int? sampleRate = null;
        ushort? blockAlign = null;
        ushort? bitsPerSample = null;
        ReadOnlySpan<byte> audioBytes = default;
        var cursor = 12;
        while (cursor <= data.Length - 8)
        {
            var chunkId = data.Slice(cursor, 4);
            var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 4, 4)));
            cursor += 8;
            if (chunkSize < 0 || cursor > data.Length - chunkSize)
            {
                throw new InvalidDataException("Spectral WAVE contains a truncated chunk.");
            }
            var chunk = data.Slice(cursor, chunkSize);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    throw new InvalidDataException("Spectral WAVE format information is incomplete.");
                }
                formatCode = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                audioBytes = chunk;
            }
            cursor = checked(cursor + chunkSize + (chunkSize & 1));
        }

        if (formatCode != 3 || channels != 2 || bitsPerSample != 32 || blockAlign != 8 ||
            sampleRate is < 8000 or > 192000 || audioBytes.IsEmpty || audioBytes.Length % 8 != 0)
        {
            throw new InvalidDataException("Spectral layers must be two-channel, 32-bit floating-point WAVE audio.");
        }

        var samples = new float[audioBytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(audioBytes).CopyTo(samples);
        if (samples.Any(sample => !float.IsFinite(sample)))
        {
            throw new InvalidDataException("Spectral WAVE contains a non-finite sample.");
        }
        return new(sampleRate.GetValueOrDefault(), channels.GetValueOrDefault(), samples);
    }

    public static void WriteAtomic(string path, FloatWaveBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.SampleRate is < 8000 or > 192000 || buffer.Channels != 2 ||
            buffer.InterleavedSamples.Length == 0 || buffer.InterleavedSamples.Length % 2 != 0 ||
            buffer.InterleavedSamples.Any(sample => !float.IsFinite(sample)))
        {
            throw new ArgumentException("Float WAVE output requires finite interleaved stereo samples and a supported sample rate.", nameof(buffer));
        }

        var dataBytes = checked(buffer.InterleavedSamples.Length * sizeof(float));
        var riffBytes = checked(36 + dataBytes);
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                Span<byte> header = stackalloc byte[44];
                "RIFF"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)riffBytes));
                "WAVE"u8.CopyTo(header[8..]);
                "fmt "u8.CopyTo(header[12..]);
                BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
                BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 3);
                BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
                BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)buffer.SampleRate));
                BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked((uint)(buffer.SampleRate * 8)));
                BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 8);
                BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);
                "data"u8.CopyTo(header[36..]);
                BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)dataBytes));
                stream.Write(header);
                stream.Write(MemoryMarshal.AsBytes(buffer.InterleavedSamples.AsSpan()));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
