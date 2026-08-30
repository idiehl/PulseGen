using PulseGen.ModelHost.Client;
using Xunit;

namespace PulseGen.ModelHost.Client.Tests;

public sealed class ModelHostClientTests
{
    [Fact]
    public async Task IsolatedHostInventoriesPacksAndCommitsAnalysis()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-host-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.wav");
            WriteStereoPcm16(source, 48000, 8192);
            await using var host = await ModelHostClient.StartAsync();
            var inventory = await host.GetPackInventoryAsync();
            Assert.Equal(2, inventory.GetArrayLength());

            var output = Path.Combine(root, "analysis");
            var progressEvents = 0;
            host.ProgressChanged += (_, _) => Interlocked.Increment(ref progressEvents);
            var result = await host.AnalyzeAsync(source, output, ModelHostChannelMode.MidSide, maxPartials: 12);

            Assert.Equal(output, result.OutputDirectory);
            Assert.True(File.Exists(Path.Combine(output, "analysis.json")));
            Assert.True(File.Exists(Path.Combine(output, "partial-tracks.json")));
            Assert.True(File.Exists(Path.Combine(output, "stochastic-residual.wav")));
            Assert.Equal("mid-side", result.Manifest.GetProperty("analysis").GetProperty("channelMode").GetString());
            Assert.Equal(12, result.Manifest.GetProperty("analysis").GetProperty("maxPartialsPerFrame").GetInt32());
            Assert.True(result.Manifest.GetProperty("reconstruction").GetProperty("timingAndChannelAligned").GetBoolean());
            Assert.InRange(result.Manifest.GetProperty("reconstruction").GetProperty("maxAbsoluteError").GetDouble(), 0, 1.0e-6);
            Assert.True(progressEvents > 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteStereoPcm16(string path, int sampleRate, int frames)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        const short channels = 2;
        const short bits = 16;
        var dataBytes = frames * channels * (bits / 8);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bits / 8));
        writer.Write((short)(channels * (bits / 8)));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (double)frame / sampleRate;
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * time) * 12000));
            writer.Write((short)(Math.Sin((2 * Math.PI * 660 * time) + 0.2) * 9000));
        }
    }
}
