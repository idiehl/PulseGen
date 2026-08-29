using System.Text;
using System.Text.Json;
using PulseGen.Contracts;
using PulseGen.Engine.Client;
using Xunit;

namespace PulseGen.Engine.Client.Tests;

public sealed class PrimitiveAcousticsTests
{
    [Fact]
    public void ManagedSceneActivatesMaterialTransmissionAndRecordsTheAcousticModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-primitive-acoustics-{Guid.NewGuid():N}");
        var baselineDirectory = Path.Combine(root, "baseline");
        var surfaceDirectory = Path.Combine(root, "surface");
        try
        {
            var baseline = NativeSceneRenderer.Render(CreateScene(withSurface: false), baselineDirectory);
            var surface = NativeSceneRenderer.Render(CreateScene(withSurface: true), surfaceDirectory);
            var baselineSamples = ReadFloatSamples(Directory.GetFiles(baselineDirectory, "*.wav").Single());
            var surfaceSamples = ReadFloatSamples(Directory.GetFiles(surfaceDirectory, "*.wav").Single());

            var ratio = ChannelRms(surfaceSamples, 0) / ChannelRms(baselineSamples, 0);
            Assert.InRange(ratio, 0.498, 0.502);
            Assert.Equal((uint)1, baseline.FilesWritten);
            Assert.Equal((uint)1, surface.FilesWritten);
            Assert.StartsWith("0.1.0-alpha.3", NativeSceneRenderer.EngineVersion, StringComparison.Ordinal);

            using var receipt = JsonDocument.Parse(File.ReadAllText(surface.RenderReceiptPath));
            Assert.Equal(2, receipt.RootElement.GetProperty("receiptVersion").GetInt32());
            Assert.Equal("primitive-geometry-v1", receipt.RootElement.GetProperty("acousticModel").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SceneDocument CreateScene(bool withSurface)
    {
        var scene = SampleScenes.CreateFlyby();
        var material = scene.Materials[0] with
        {
            Name = "Quarter-energy panel",
            Absorption = [0, 0, 0, 0, 0, 0, 0, 0],
            Scattering = [0, 0, 0, 0, 0, 0, 0, 0],
            Transmission = [0.25, 0.25, 0.25, 0.25, 0.25, 0.25, 0.25, 0.25],
            Roughness = 0
        };
        scene.Materials[0] = material;
        scene.Geometry.Clear();
        if (withSurface)
        {
            scene.Geometry.Add(new AcousticGeometry
            {
                Id = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001"),
                Name = "Transmission panel",
                Kind = GeometryKind.Plane,
                MaterialId = material.Id,
                Transform = new Transform { PositionMetres = new(0, 0, 1) },
                DimensionsMetres = new(10, 10, 0),
                Occludes = true,
                Reflects = false
            });
        }

        var source = new Vector3d(0, 0, 2);
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Kind = MotionPathKind.Linear,
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = source },
                new MotionKeyframe { TimeSeconds = 1, PositionMetres = source }
            ]
        };
        var oscillator = scene.Emitters[0].SignalGraph.Nodes.First(node => node.Kind == SignalNodeKind.Oscillator);
        oscillator.NumericParameters["frequencyHz"] = 440;
        oscillator.NumericParameters["amplitude"] = 1;
        scene.Emitters[0] = scene.Emitters[0] with { GainLinear = 1, MinDistanceMetres = 0.01 };
        scene.ReceiverRigs.Clear();
        scene.ReceiverRigs.Add(new ReceiverRig
        {
            Id = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"),
            Name = "Test microphones",
            Kind = ReceiverRigKind.StereoMicrophone,
            Transform = new Transform { PositionMetres = Vector3d.Zero },
            SpacingMetres = 0.01,
            PolarPattern = PolarPattern.Omni
        });
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with
        {
            Quality = RenderQuality.Final,
            SampleRate = 48000,
            SampleFormat = RenderSampleFormat.Float32,
            Dither = DitherMode.None,
            ReflectionOrder = 0,
            Seed = 91
        };
        return scene with
        {
            Name = "Primitive Surface Integration",
            Atmosphere = new AtmosphereField { TemperatureCelsius = 20, RelativeHumidityPercent = 50, PressureKilopascals = 101.325 },
            Timeline = new EventTimeline { DurationSeconds = 1 }
        };
    }

    private static float[] ReadFloatSamples(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4)));
        _ = reader.ReadUInt32();
        Assert.Equal("WAVE", Encoding.ASCII.GetString(reader.ReadBytes(4)));
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            if (chunk == "fmt ")
            {
                Assert.Equal((ushort)3, reader.ReadUInt16());
                stream.Position += size - sizeof(ushort);
            }
            else if (chunk == "data")
            {
                var samples = new float[checked((int)(size / sizeof(float)))];
                for (var index = 0; index < samples.Length; index++)
                {
                    samples[index] = reader.ReadSingle();
                }
                return samples;
            }
            else
            {
                stream.Position += size;
            }
            if ((size & 1) != 0)
            {
                stream.Position++;
            }
        }
        throw new InvalidDataException("Rendered WAV did not contain a data chunk.");
    }

    private static double ChannelRms(IReadOnlyList<float> samples, int channel)
    {
        double sum = 0;
        var count = 0;
        for (var index = channel; index < samples.Count; index += 2)
        {
            sum += samples[index] * samples[index];
            count++;
        }
        return Math.Sqrt(sum / count);
    }
}
