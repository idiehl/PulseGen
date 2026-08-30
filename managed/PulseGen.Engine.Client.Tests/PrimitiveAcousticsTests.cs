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
            Assert.StartsWith("0.1.0-alpha.8", NativeSceneRenderer.EngineVersion, StringComparison.Ordinal);

            using var receipt = JsonDocument.Parse(File.ReadAllText(surface.RenderReceiptPath));
            Assert.Equal(5, receipt.RootElement.GetProperty("receiptVersion").GetInt32());
            Assert.Equal("bounded-propagation-v5", receipt.RootElement.GetProperty("acousticModel").GetString());
            Assert.Equal("layered-bounded-wind-v1", receipt.RootElement.GetProperty("atmosphereModel").GetString());
            Assert.Contains(
                receipt.RootElement.GetProperty("dependencies").EnumerateArray(),
                dependency => dependency.GetProperty("name").GetString() == "Assimp" &&
                    dependency.GetProperty("version").GetString() == "6.0.5");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LayeredBoundedWindFieldChangesTransitTimeAndRendersDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-atmosphere-field-{Guid.NewGuid():N}");
        try
        {
            var scene = SampleScenes.CreateAtmosphereField();
            var firstDirectory = Path.Combine(root, "first");
            var secondDirectory = Path.Combine(root, "second");
            var first = NativeSceneRenderer.Render(scene, firstDirectory);
            NativeSceneRenderer.Render(scene, secondDirectory);

            var firstFiles = Directory.GetFiles(firstDirectory, "*.wav").Order(StringComparer.Ordinal).ToArray();
            var secondFiles = Directory.GetFiles(secondDirectory, "*.wav").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(2, firstFiles.Length);
            Assert.Equal(firstFiles.Select(Path.GetFileName), secondFiles.Select(Path.GetFileName));
            for (var index = 0; index < firstFiles.Length; index++)
            {
                Assert.Equal(ReadFloatSamples(firstFiles[index]), ReadFloatSamples(secondFiles[index]));
            }

            var warm = ReadFloatSamples(firstFiles.Single(path => path.Contains("Warm_tailwind", StringComparison.Ordinal)));
            var cold = ReadFloatSamples(firstFiles.Single(path => path.Contains("Cold_headwind", StringComparison.Ordinal)));
            var warmArrival = FirstActiveFrame(warm);
            var coldArrival = FirstActiveFrame(cold);
            Assert.True(warmArrival < coldArrival, $"Expected tailwind arrival {warmArrival} before headwind arrival {coldArrival}.");
            Assert.True(coldArrival - warmArrival > 100, "The bounded layer-and-wind comparison should produce a clearly resolved transit-time difference.");
            Assert.Equal((ulong)161803, first.DeterministicSeed);

            using var receipt = JsonDocument.Parse(File.ReadAllText(first.RenderReceiptPath));
            Assert.Equal(5, receipt.RootElement.GetProperty("receiptVersion").GetInt32());
            Assert.Equal("bounded-propagation-v5", receipt.RootElement.GetProperty("acousticModel").GetString());
            Assert.Equal("layered-bounded-wind-v1", receipt.RootElement.GetProperty("atmosphereModel").GetString());
            Assert.Equal(2, receipt.RootElement.GetProperty("atmosphereLayerCount").GetInt32());
            Assert.Equal(2, receipt.RootElement.GetProperty("windZoneCount").GetInt32());
            Assert.Equal(48, receipt.RootElement.GetProperty("atmosphereIntegrationSegments").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MovingReceiverPathProducesAnalyticListenerDopplerAndStableStereoTake()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-moving-receiver-{Guid.NewGuid():N}");
        try
        {
            var staticScene = CreateReceiverDopplerScene(moving: false);
            var movingScene = CreateReceiverDopplerScene(moving: true);
            var staticDirectory = Path.Combine(root, "static");
            var firstDirectory = Path.Combine(root, "moving-first");
            var secondDirectory = Path.Combine(root, "moving-second");
            NativeSceneRenderer.Render(staticScene, staticDirectory);
            var first = NativeSceneRenderer.Render(movingScene, firstDirectory);
            NativeSceneRenderer.Render(movingScene, secondDirectory);
            var staticSamples = ReadFloatSamples(Directory.GetFiles(staticDirectory, "*.wav").Single());
            var firstSamples = ReadFloatSamples(Directory.GetFiles(firstDirectory, "*.wav").Single());
            var secondSamples = ReadFloatSamples(Directory.GetFiles(secondDirectory, "*.wav").Single());

            const int startFrame = 24000;
            const int endFrame = 48000;
            var staticFrequency = EstimateFrequency(staticSamples, 0, startFrame, endFrame, 48000);
            var movingFrequency = EstimateFrequency(firstSamples, 0, startFrame, endFrame, 48000);
            var expectedFrequency = 440.0 * ((344.04 + 20.0) / 344.04);
            Assert.InRange(staticFrequency, 438.0, 442.0);
            Assert.InRange(movingFrequency, expectedFrequency - 2.1, expectedFrequency + 2.1);
            Assert.True(movingFrequency > staticFrequency);
            Assert.Equal(firstSamples, secondSamples);
            Assert.Equal((ulong)73, first.DeterministicSeed);
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                Assert.Equal(firstSamples[frame * 2], firstSamples[(frame * 2) + 1]);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ManagedIndependentCapsulesDriveSeparateChannelsAndMeasuredHrtfModesFailClearly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-independent-capsules-{Guid.NewGuid():N}");
        try
        {
            var scene = CreateScene(withSurface: false);
            scene.MotionPaths[0] = scene.MotionPaths[0] with
            {
                Keyframes =
                [
                    new MotionKeyframe { TimeSeconds = 0, PositionMetres = new Vector3d(0, 0, -2) },
                    new MotionKeyframe { TimeSeconds = 1, PositionMetres = new Vector3d(0, 0, -2) }
                ]
            };
            var microphones = scene.ReceiverRigs[0];
            scene.ReceiverRigs[0] = microphones with
            {
                SpacingMetres = 0.01,
                LeftCapsule = new MicrophoneCapsule
                {
                    RelativeTransform = new Transform { PositionMetres = new Vector3d(-0.005, 0, 0) },
                    PolarPattern = PolarPattern.Cardioid
                },
                RightCapsule = new MicrophoneCapsule
                {
                    RelativeTransform = new Transform { PositionMetres = new Vector3d(0.005, 0, 0) },
                    PolarPattern = PolarPattern.Omni
                }
            };
            NativeSceneRenderer.Render(scene, Path.Combine(root, "capsules"));
            var samples = ReadFloatSamples(Directory.GetFiles(Path.Combine(root, "capsules"), "*.wav").Single());
            var leftRms = ChannelRms(samples, 0);
            var rightRms = ChannelRms(samples, 1);
            Assert.True(leftRms < rightRms * 2.0e-6, $"Expected rear-cardioid rejection; left={leftRms:R}, right={rightRms:R}.");

            var hrtfHash = new string('d', 64);
            var gatedScene = CreateScene(withSurface: false);
            gatedScene.Assets.Add(new AssetRef
            {
                Sha256 = hrtfHash,
                Kind = AssetKind.Hrtf,
                FileName = "measured.sofa",
                MediaType = "application/sofa",
                SizeBytes = 2048
            });
            gatedScene.ReceiverRigs[0] = new ReceiverRig
            {
                Id = gatedScene.ReceiverRigs[0].Id,
                Name = "Measured listener",
                Kind = ReceiverRigKind.BinauralHead,
                Transform = gatedScene.ReceiverRigs[0].Transform,
                HrtfMode = HrtfMode.ExactSofa,
                SofaAssetHash = hrtfHash
            };

            var exception = Assert.Throws<NotSupportedException>(() =>
                NativeSceneRenderer.Render(gatedScene, Path.Combine(root, "measured")));
            Assert.Contains("Steam Audio", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContentAddressedObjMeshOccludesDeterministicallyAndRejectsTamperingOrReflectionFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-imported-mesh-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "MeshWorld.pulseproj");
        var sourcePath = Path.Combine(root, "wall.obj");
        const string obj = """
            o BoundedWall
            v -5 -5 0
            v 5 -5 0
            v 5 5 0
            v -5 5 0
            f 1 2 3
            f 1 3 4
            """;
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(sourcePath, obj, Encoding.UTF8);
            var scene = CreateScene(withSurface: false);
            await ProjectStore.SaveAsync(scene, project);
            var asset = await ProjectStore.ImportAssetAsync(project, sourcePath, AssetKind.Mesh, "model/obj", "test fixture", "CC0-1.0");
            scene.Assets.Add(asset);
            scene.Geometry.Add(new AcousticGeometry
            {
                Id = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000003"),
                Name = "Imported transmission wall",
                Kind = GeometryKind.ImportedMesh,
                MaterialId = scene.Materials[0].Id,
                MeshAssetHash = asset.Sha256,
                Transform = new Transform { PositionMetres = new(0, 0, 1) },
                DimensionsMetres = new(10, 10, 0.01),
                Occludes = true,
                Reflects = false
            });
            await ProjectStore.SaveAsync(scene, project);

            var baselineDirectory = Path.Combine(root, "baseline");
            var firstDirectory = Path.Combine(root, "mesh-first");
            var secondDirectory = Path.Combine(root, "mesh-second");
            NativeSceneRenderer.Render(CreateScene(withSurface: false), baselineDirectory);
            var first = NativeSceneRenderer.Render(scene, firstDirectory, projectDirectory: project);
            var second = NativeSceneRenderer.Render(scene, secondDirectory, projectDirectory: project);
            var baselineSamples = ReadFloatSamples(Directory.GetFiles(baselineDirectory, "*.wav").Single());
            var firstSamples = ReadFloatSamples(Directory.GetFiles(firstDirectory, "*.wav").Single());
            var secondSamples = ReadFloatSamples(Directory.GetFiles(secondDirectory, "*.wav").Single());

            Assert.InRange(ChannelRms(firstSamples, 0) / ChannelRms(baselineSamples, 0), 0.498, 0.502);
            Assert.Equal(firstSamples, secondSamples);
            Assert.Equal(first.DeterministicSeed, second.DeterministicSeed);
            using (var receipt = JsonDocument.Parse(File.ReadAllText(first.RenderReceiptPath)))
            {
                Assert.Equal(5, receipt.RootElement.GetProperty("receiptVersion").GetInt32());
                Assert.Contains(
                    receipt.RootElement.GetProperty("dependencies").EnumerateArray(),
                    dependency => dependency.GetProperty("name").GetString() == "Assimp");
            }

            Assert.Throws<InvalidOperationException>(() => NativeSceneRenderer.Render(scene, Path.Combine(root, "missing-project")));
            var reflectionScene = scene with { Geometry = [scene.Geometry[0] with { Reflects = true }] };
            Assert.Throws<NotSupportedException>(() =>
                NativeSceneRenderer.Render(reflectionScene, Path.Combine(root, "reflection"), projectDirectory: project));

            var storedAssetPath = ProjectStore.ResolveAssetPath(project, asset.Sha256);
            var tampered = await File.ReadAllBytesAsync(storedAssetPath);
            tampered[0] ^= 0x01;
            await File.WriteAllBytesAsync(storedAssetPath, tampered);
            Assert.Throws<InvalidDataException>(() =>
                NativeSceneRenderer.Render(scene, Path.Combine(root, "tampered"), projectDirectory: project));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProjectStore.ImportAssetAsync(project, sourcePath, AssetKind.Mesh, "model/obj"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RenderProfileRayCountActivatesADeterministicScatteredTail()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsegen-diffuse-tail-{Guid.NewGuid():N}");
        try
        {
            var scene = CreateDiffuseImpulseScene();
            var noDiffuseProfile = scene.RenderProfiles[0] with { RayCount = 0, ImpulseResponseSeconds = 0.2 };
            var diffuseProfile = scene.RenderProfiles[0] with { RayCount = 4096, ImpulseResponseSeconds = 0.2, Seed = 424242 };
            NativeSceneRenderer.Render(scene, Path.Combine(root, "no-diffuse"), noDiffuseProfile);
            var first = NativeSceneRenderer.Render(scene, Path.Combine(root, "diffuse-first"), diffuseProfile);
            NativeSceneRenderer.Render(scene, Path.Combine(root, "diffuse-second"), diffuseProfile);

            var noDiffuse = ReadFloatSamples(Directory.GetFiles(Path.Combine(root, "no-diffuse"), "*.wav").Single());
            var diffuseFirst = ReadFloatSamples(Directory.GetFiles(Path.Combine(root, "diffuse-first"), "*.wav").Single());
            var diffuseSecond = ReadFloatSamples(Directory.GetFiles(Path.Combine(root, "diffuse-second"), "*.wav").Single());
            var firstDiffuseFrame = FirstDifferenceFrame(noDiffuse, diffuseFirst);
            var diffuseTailPeak = PeakDifferenceAfterFrame(noDiffuse, diffuseFirst, firstDiffuseFrame);
            Assert.True(
                firstDiffuseFrame is > 1000 and < 12000,
                $"Expected the scattered response to arrive after direct and clear reflected sound; diffuse={firstDiffuseFrame}.");
            Assert.True(diffuseTailPeak > 1.0e-4, $"Expected an audible scattered tail; peak={diffuseTailPeak:R}.");
            Assert.Equal(diffuseFirst, diffuseSecond);

            using var receipt = JsonDocument.Parse(File.ReadAllText(first.RenderReceiptPath));
            Assert.Equal(4096, receipt.RootElement.GetProperty("rayCount").GetInt32());
            Assert.Equal(0.2, receipt.RootElement.GetProperty("impulseResponseSeconds").GetDouble());
            Assert.Equal(424242, receipt.RootElement.GetProperty("seed").GetInt32());
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

    private static SceneDocument CreateReceiverDopplerScene(bool moving)
    {
        var scene = CreateScene(withSurface: false);
        var sourcePosition = new Vector3d(0, 0, 100);
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = sourcePosition },
                new MotionKeyframe { TimeSeconds = 1, PositionMetres = sourcePosition }
            ]
        };
        var receiverPathId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000004");
        if (moving)
        {
            scene.MotionPaths.Add(new MotionPath
            {
                Id = receiverPathId,
                Name = "Microphones approach source",
                Kind = MotionPathKind.Linear,
                Keyframes =
                [
                    new MotionKeyframe { TimeSeconds = 0, PositionMetres = Vector3d.Zero },
                    new MotionKeyframe { TimeSeconds = 2, PositionMetres = new(0, 0, 40) }
                ]
            });
            scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { MotionPathId = receiverPathId };
        }
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { Seed = 73 };
        return scene with { Name = moving ? "Moving Receiver" : "Static Receiver" };
    }

    private static SceneDocument CreateDiffuseImpulseScene()
    {
        var scene = CreateScene(withSurface: false);
        var material = scene.Materials[0] with
        {
            Name = "Scattering test wall",
            Absorption = [0, 0, 0, 0, 0, 0, 0, 0],
            Scattering = [0.75, 0.75, 0.75, 0.75, 0.75, 0.75, 0.75, 0.75],
            Transmission = [0, 0, 0, 0, 0, 0, 0, 0],
            Roughness = 0
        };
        scene.Materials[0] = material;
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000005"),
            Name = "Diffuse reflection plane",
            Kind = GeometryKind.Plane,
            MaterialId = material.Id,
            Transform = new Transform { PositionMetres = new Vector3d(0, 0, 3) },
            DimensionsMetres = new Vector3d(10, 10, 0),
            Occludes = false,
            Reflects = true
        });
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = new Vector3d(1, 0, 0) },
                new MotionKeyframe { TimeSeconds = 0.25, PositionMetres = new Vector3d(1, 0, 0) }
            ]
        };
        var nodes = scene.Emitters[0].SignalGraph.Nodes.ToList();
        var oscillatorIndex = nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
        nodes[oscillatorIndex] = nodes[oscillatorIndex] with
        {
            Waveform = WaveformKind.Impulse,
            NumericParameters = new Dictionary<string, double>
            {
                ["frequencyHz"] = 1,
                ["amplitude"] = 1
            }
        };
        scene.Emitters[0] = scene.Emitters[0] with
        {
            SignalGraph = scene.Emitters[0].SignalGraph with { Nodes = nodes }
        };
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with
        {
            ReflectionOrder = 1,
            RayCount = 4096,
            ImpulseResponseSeconds = 0.2,
            Seed = 424242
        };
        return scene with
        {
            Name = "Diffuse Tail Integration",
            Timeline = new EventTimeline { DurationSeconds = 0.25 }
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

    private static int FirstDifferenceFrame(
        IReadOnlyList<float> baseline,
        IReadOnlyList<float> comparison,
        float threshold = 1.0e-8f)
    {
        Assert.Equal(baseline.Count, comparison.Count);
        for (var frame = 0; frame < baseline.Count / 2; frame++)
        {
            if (Math.Abs(baseline[frame * 2] - comparison[frame * 2]) > threshold ||
                Math.Abs(baseline[(frame * 2) + 1] - comparison[(frame * 2) + 1]) > threshold)
            {
                return frame;
            }
        }
        return baseline.Count / 2;
    }

    private static double PeakDifferenceAfterFrame(
        IReadOnlyList<float> baseline,
        IReadOnlyList<float> comparison,
        int startFrame)
    {
        Assert.Equal(baseline.Count, comparison.Count);
        double peak = 0;
        for (var index = Math.Max(0, startFrame) * 2; index < baseline.Count; index++)
        {
            peak = Math.Max(peak, Math.Abs(baseline[index] - comparison[index]));
        }
        return peak;
    }

    private static int FirstActiveFrame(IReadOnlyList<float> samples, float threshold = 1.0e-7f)
    {
        for (var frame = 0; frame < samples.Count / 2; frame++)
        {
            if (Math.Abs(samples[frame * 2]) > threshold || Math.Abs(samples[(frame * 2) + 1]) > threshold)
            {
                return frame;
            }
        }
        return samples.Count / 2;
    }

    private static double EstimateFrequency(
        IReadOnlyList<float> samples,
        int channel,
        int startFrame,
        int endFrame,
        int sampleRate)
    {
        var upwardCrossings = 0;
        for (var frame = startFrame + 1; frame < endFrame; frame++)
        {
            if (samples[((frame - 1) * 2) + channel] <= 0 && samples[(frame * 2) + channel] > 0)
            {
                upwardCrossings++;
            }
        }
        return upwardCrossings / ((endFrame - startFrame) / (double)sampleRate);
    }
}
