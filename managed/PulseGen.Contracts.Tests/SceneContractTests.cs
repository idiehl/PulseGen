using PulseGen.Contracts;
using Xunit;
using System.Text.Json;

namespace PulseGen.Contracts.Tests;

public sealed class SceneContractTests
{
    [Fact]
    public void FlybySceneIsValid()
    {
        var issues = SceneValidator.Validate(SampleScenes.CreateFlyby());
        Assert.Empty(issues);
    }

    [Fact]
    public void SurfaceEchoSceneIsValid()
    {
        var issues = SceneValidator.Validate(SampleScenes.CreateSurfaceEcho());
        Assert.Empty(issues);
    }

    [Fact]
    public void MovingReceiverSceneIsValidAndAssignsOnlyTheMicrophonePath()
    {
        var scene = SampleScenes.CreateMovingReceiver();
        var issues = SceneValidator.Validate(scene);

        Assert.Empty(issues);
        Assert.Null(scene.ReceiverRigs[0].MotionPathId);
        Assert.NotNull(scene.ReceiverRigs[1].MotionPathId);
        Assert.Contains(scene.MotionPaths, path => path.Id == scene.ReceiverRigs[1].MotionPathId);
    }

    [Fact]
    public void AtmosphereFieldSceneIsValidAndUsesBoundedFinalIntegration()
    {
        var scene = SampleScenes.CreateAtmosphereField();

        Assert.Empty(SceneValidator.Validate(scene));
        Assert.Equal(2, scene.Atmosphere.Layers.Count);
        Assert.Equal(2, scene.Atmosphere.WindZones.Count);
        Assert.Equal(RenderQuality.Final, scene.RenderProfiles[0].Quality);
        Assert.Equal(48, scene.RenderProfiles[0].AtmosphereIntegrationSegments);
    }

    [Fact]
    public void RealtimeStressSceneMatchesTheHardwareAcceptanceShape()
    {
        var scene = SampleScenes.CreateRealtimeStress();

        Assert.Empty(SceneValidator.Validate(scene));
        Assert.Equal(8, scene.Emitters.Count);
        Assert.Equal(8, scene.MotionPaths.Count);
        Assert.Single(scene.ReceiverRigs, receiver => receiver.Kind == ReceiverRigKind.BinauralHead);
        Assert.Equal(2, scene.ReceiverRigs.Count(receiver => receiver.Kind == ReceiverRigKind.StereoMicrophone));
        Assert.All(scene.Emitters, emitter => Assert.NotNull(emitter.MotionPathId));
        Assert.Equal(48000, scene.RenderProfiles[0].SampleRate);
        Assert.Equal(512, scene.RenderProfiles[0].BufferFrames);
        Assert.Equal(RenderQuality.Interactive, scene.RenderProfiles[0].Quality);
        Assert.Equal(1, scene.RenderProfiles[0].ReflectionOrder);
    }

    [Fact]
    public void ViewportStressSceneUsesAVisualOnlyFiftyByTwentyFiveMetreTerrain()
    {
        var scene = SampleScenes.CreateViewportStress();

        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error);
        var terrain = Assert.Single(scene.Geometry);
        Assert.Equal(GeometryKind.Terrain, terrain.Kind);
        Assert.Equal(new Vector3d(50, 6, 25), terrain.DimensionsMetres);
        Assert.False(terrain.Occludes);
        Assert.False(terrain.Reflects);
    }

    [Fact]
    public void AtmosphereFieldsRejectNonFiniteBoundsSeedsAndOverlaps()
    {
        var scene = SampleScenes.CreateAtmosphereField();
        scene.Atmosphere.WindZones[0] = scene.Atmosphere.WindZones[0] with
        {
            DimensionsMetres = new(double.NaN, 1, 1),
            Transform = new Transform { Scale = new(1, 0, 1) },
            NoiseSeed = 1.5
        };
        scene.Atmosphere.Layers[1] = scene.Atmosphere.Layers[1] with
        {
            BaseAltitudeMetres = 6,
            PressureKilopascals = double.NaN
        };

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path.EndsWith("/dimensionsMetres", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/transform", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/noiseSeed", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "atmosphere/layers/1" && issue.Message.Contains("bounded", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "atmosphere/layers/1" && issue.Message.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedUnitsAreRejected()
    {
        var scene = SampleScenes.CreateFlyby() with { Units = "feet" };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path == "units");
    }

    [Fact]
    public void DanglingMotionPathIsRejected()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.Emitters[0] = scene.Emitters[0] with { MotionPathId = Guid.NewGuid() };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path.EndsWith("motionPathId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectRoundTripsAndProducesStableHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pulsegen-{Guid.NewGuid():N}.pulseproj");
        try
        {
            var scene = SampleScenes.CreateFlyby();
            var firstHash = await ProjectStore.SaveAsync(scene, directory);
            var loaded = await ProjectStore.LoadAsync(directory);
            var secondHash = await ProjectStore.SaveAsync(loaded, directory);

            Assert.Equal(scene.ProjectId, loaded.ProjectId);
            Assert.Equal(scene.ReceiverRigs.Count, loaded.ReceiverRigs.Count);
            Assert.Equal(firstHash, secondHash);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProjectPathsAcceptFolderPickerTrailingSeparators()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pulsegen-picker-{Guid.NewGuid():N}.pulseproj");
        var pickerPath = directory + Path.DirectorySeparatorChar;
        try
        {
            var expected = SampleScenes.CreateFlyby();
            await ProjectStore.SaveAsync(expected, pickerPath);

            var loaded = await ProjectStore.LoadAsync(pickerPath);

            Assert.Equal(expected.ProjectId, loaded.ProjectId);
            Assert.Equal(Path.Combine(directory, "project.json"), ProjectStore.ResolveManifestPath(pickerPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CheckedInSurfaceEchoProjectMatchesItsFactoryScene()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SurfaceEcho.pulseproj");
        var generatedDirectory = Path.Combine(Path.GetTempPath(), $"pulsegen-surface-{Guid.NewGuid():N}.pulseproj");
        try
        {
            var loaded = await ProjectStore.LoadAsync(fixtureDirectory);
            var fixtureHash = await ProjectStore.ComputeSha256Async(ProjectStore.ResolveManifestPath(fixtureDirectory));
            var generatedHash = await ProjectStore.SaveAsync(SampleScenes.CreateSurfaceEcho(), generatedDirectory);

            Assert.Equal("Surface Echo Study", loaded.Name);
            Assert.Equal(fixtureHash, generatedHash);
        }
        finally
        {
            if (Directory.Exists(generatedDirectory))
            {
                Directory.Delete(generatedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PulsePackRoundTripsWithoutOverwritingDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pulsepack-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "Source.pulseproj");
        var archive = Path.Combine(root, "Portable.pulsepack");
        var imported = Path.Combine(root, "Imported.pulseproj");
        try
        {
            await ProjectStore.SaveAsync(SampleScenes.CreateFlyby(), project);
            var archiveHash = await ProjectStore.PackAsync(project, archive);
            var loaded = await ProjectStore.UnpackAsync(archive, imported);

            Assert.Equal(64, archiveHash.Length);
            Assert.Equal(SampleScenes.CreateFlyby().ProjectId, loaded.ProjectId);
            await Assert.ThrowsAsync<IOException>(() => ProjectStore.UnpackAsync(archive, imported));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(20, 343.42)]
    [InlineData(0, 331.3)]
    public void SpeedOfSoundUsesTemperature(double temperature, double expected)
    {
        Assert.Equal(expected, SceneValidator.SpeedOfSound(temperature), precision: 6);
    }

    [Fact]
    public void EffectiveSpeedOfSoundAddsTheBoundedHumidityCorrection()
    {
        Assert.Equal(344.04, SceneValidator.EffectiveSpeedOfSound(20, 50), precision: 6);
    }

    [Fact]
    public void EmbeddedSchemasAreWellFormedJson()
    {
        using var scene = JsonDocument.Parse(SchemaCatalog.ReadSceneSchema());
        using var packs = JsonDocument.Parse(SchemaCatalog.ReadModelPackSchema());
        Assert.Equal("PulseGen SceneDocument v1", scene.RootElement.GetProperty("title").GetString());
        Assert.Equal("PulseGen ModelPackManifest v1", packs.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void NearSonicMotionWarnsButSupersonicMotionFails()
    {
        var scene = SampleScenes.CreateFlyby();
        var path = scene.MotionPaths[0];
        path.Keyframes[1] = path.Keyframes[1] with { TimeSeconds = 0.1, PositionMetres = new(30, 1.7, 4) };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Error && issue.Message.Contains("supersonic", StringComparison.Ordinal));
        Assert.Throws<SceneValidationException>(() => SceneValidator.EnsureValid(scene));

        path.Keyframes[1] = path.Keyframes[1] with { TimeSeconds = 0.2, PositionMetres = new(27, 1.7, 4) };
        issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Warning && issue.Message.Contains("near sonic", StringComparison.Ordinal));
        SceneValidator.EnsureValid(scene);
    }

    [Fact]
    public void ExactSofaModeCannotSilentlyUseNoDataset()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { HrtfMode = HrtfMode.ExactSofa, SofaAssetHash = null };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path.EndsWith("sofaAssetHash", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactSofaModeRequiresADeclaredHrtfAsset()
    {
        var scene = SampleScenes.CreateFlyby();
        var hash = new string('c', 64);
        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { HrtfMode = HrtfMode.ExactSofa, SofaAssetHash = hash };
        scene.Assets.Add(new AssetRef
        {
            Sha256 = hash,
            Kind = AssetKind.Audio,
            FileName = "listener.sofa",
            MediaType = "application/sofa",
            SizeBytes = 1024
        });

        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path.EndsWith("sofaAssetHash", StringComparison.Ordinal) &&
            issue.Message.Contains("HRTF", StringComparison.Ordinal));

        scene.Assets[0] = scene.Assets[0] with { Kind = AssetKind.Hrtf };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void StereoCapsulesMustBePairedAndUseIncreasingCalibrationFrequencies()
    {
        var scene = SampleScenes.CreateFlyby();
        var microphones = scene.ReceiverRigs[1];
        scene.ReceiverRigs[1] = microphones with
        {
            LeftCapsule = new MicrophoneCapsule
            {
                ResponseCentresHz = [1000, 100],
                ResponseDb = [0, 0]
            },
            RightCapsule = null
        };

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Message.Contains("both independent capsules", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("increase strictly", StringComparison.Ordinal));
    }

    [Fact]
    public void ReceiverMotionUsesAValidatedFiniteScenePath()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { MotionPathId = scene.MotionPaths[0].Id };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error);

        scene.MotionPaths[0].Keyframes[0] = scene.MotionPaths[0].Keyframes[0] with
        {
            PositionMetres = new(double.NaN, 0, 0)
        };
        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with { SpacingMetres = double.NaN };
        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path.Contains("/keyframes/0", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/spacingMetres", StringComparison.Ordinal));
    }

    [Fact]
    public void IntegerAndFloatRenderProfilesRequireExplicitCompatibleDither()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { SampleFormat = RenderSampleFormat.Pcm24, Dither = DitherMode.None };
        Assert.Contains(SceneValidator.Validate(scene), issue => issue.Path.EndsWith("/dither", StringComparison.Ordinal));

        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { Dither = DitherMode.Tpdf };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Path.EndsWith("/dither", StringComparison.Ordinal));

        scene.RenderProfiles[0] = scene.RenderProfiles[0] with { SampleFormat = RenderSampleFormat.Float32, Dither = DitherMode.Tpdf };
        Assert.Contains(SceneValidator.Validate(scene), issue => issue.Path.EndsWith("/dither", StringComparison.Ordinal));
    }

    [Fact]
    public void MaterialBandsMustBeOrderedAndConserveEnergy()
    {
        var scene = SampleScenes.CreateFlyby();
        var material = scene.Materials[0];
        scene.Materials[0] = material = material with { Name = "   " };
        material.OctaveBandCentresHz[2] = material.OctaveBandCentresHz[1];
        material.Absorption[0] = 0.9;
        material.Transmission[0] = 0.2;

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path == $"materials/{material.Id}/name");
        Assert.Contains(issues, issue => issue.Path.EndsWith("/octaveBandCentresHz", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == $"materials/{material.Id}" && issue.Message.Contains("incident energy", StringComparison.Ordinal));
    }

    [Fact]
    public void RoomDecayEstimateRespondsToSizeAndMaterialLoss()
    {
        var scene = SampleScenes.CreateSurfaceEcho();
        var stone = scene.Materials.Single(item => item.Name == "Polished stone");
        var curtain = scene.Materials.Single(item => item.Name == "Heavy acoustic curtain");
        var dimensions = new Vector3d(20, 8, 14);

        var stoneEstimate = AcousticRoomAnalysis.Estimate(dimensions, stone);
        var curtainEstimate = AcousticRoomAnalysis.Estimate(dimensions, curtain);

        Assert.True(stoneEstimate.MidFrequencyRt60Seconds > curtainEstimate.MidFrequencyRt60Seconds);
        Assert.True(curtainEstimate.LowFrequencyRt60Seconds > curtainEstimate.HighFrequencyRt60Seconds);
        Assert.InRange(stoneEstimate.MeanFreePathMetres, 5, 20);
        Assert.InRange(stoneEstimate.MeanReflectionIntervalMilliseconds, 10, 60);
    }

    [Fact]
    public void RenderEchoQualitySettingsStayInsideNativeBounds()
    {
        var scene = SampleScenes.CreateFlyby();
        var profile = scene.RenderProfiles[0];
        scene.RenderProfiles[0] = profile with { ReflectionOrder = 2 };
        Assert.DoesNotContain(
            SceneValidator.Validate(scene),
            issue => issue.Path.EndsWith("/reflectionOrder", StringComparison.Ordinal));

        scene.RenderProfiles[0] = profile with
        {
            Name = " ",
            ReflectionOrder = 9,
            RayCount = 1_000_001,
            ImpulseResponseSeconds = 31,
            Seed = -1
        };

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path.EndsWith("/name", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/reflectionOrder", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/rayCount", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/impulseResponseSeconds", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/seed", StringComparison.Ordinal));
    }

    [Fact]
    public void SceneRequiresAnExplicitRenderProfile()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.RenderProfiles.Clear();

        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path == "renderProfiles" && issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void AcousticGeometryRequiresMaterialAndPhysicalDimensions()
    {
        var scene = SampleScenes.CreateFlyby();
        var geometryId = Guid.NewGuid();
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = geometryId,
            Name = "Invalid obstacle",
            Kind = GeometryKind.Box,
            MaterialId = Guid.NewGuid(),
            DimensionsMetres = new(2, -1, 0.2),
            Transform = new Transform { Scale = new(1, 0, 1) }
        });

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path == $"geometry/{geometryId}/materialId");
        Assert.Contains(issues, issue => issue.Path == $"geometry/{geometryId}/dimensionsMetres");
        Assert.Contains(issues, issue => issue.Path == $"geometry/{geometryId}/transform");
    }

    [Fact]
    public void ImportedMeshRequiresDeclaredMeshAssetAndAcceptsReflections()
    {
        var scene = SampleScenes.CreateFlyby();
        var geometryId = Guid.NewGuid();
        var hash = new string('a', 64);
        scene.Geometry.Add(new AcousticGeometry
        {
            Id = geometryId,
            Name = "Imported wall",
            Kind = GeometryKind.ImportedMesh,
            MaterialId = scene.Materials[0].Id,
            MeshAssetHash = hash,
            Reflects = false
        });

        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path == $"geometry/{geometryId}/meshAssetHash" && issue.Severity == ValidationSeverity.Error);

        scene.Assets.Add(new AssetRef
        {
            Sha256 = hash,
            Kind = AssetKind.Audio,
            FileName = "wall.obj",
            MediaType = "model/obj",
            SizeBytes = 100
        });
        Assert.Contains(SceneValidator.Validate(scene), issue =>
            issue.Path == $"geometry/{geometryId}/meshAssetHash" && issue.Severity == ValidationSeverity.Error);

        scene.Assets[0] = scene.Assets[0] with { Kind = AssetKind.Mesh };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error);
        scene.Geometry[^1] = scene.Geometry[^1] with { Reflects = true };
        Assert.DoesNotContain(SceneValidator.Validate(scene), issue =>
            issue.Path == $"geometry/{geometryId}/reflects");
    }

    [Fact]
    public void ImportedMeshMaterialBindingsAreVersionedPersistedAndValidated()
    {
        var scene = SampleScenes.CreateFlyby();
        var alternate = scene.Materials[0] with { Id = Guid.NewGuid(), Name = "Alternate region" };
        scene.Materials.Add(alternate);
        var hash = new string('b', 64);
        scene.Assets.Add(new AssetRef
        {
            Sha256 = hash,
            Kind = AssetKind.Mesh,
            FileName = "regions.obj",
            MediaType = "model/obj",
            SizeBytes = 128
        });
        var geometry = new AcousticGeometry
        {
            Id = Guid.NewGuid(),
            Name = "Bound regions",
            Kind = GeometryKind.ImportedMesh,
            MaterialId = scene.Materials[0].Id,
            MeshAssetHash = hash,
            MaterialBindings =
            [
                new ImportedMeshMaterialBinding
                {
                    SourceMeshIndex = 0,
                    SourceMaterialIndex = 1,
                    SourceMeshName = "StoneWall",
                    SourceMaterialName = "Stone",
                    MaterialId = scene.Materials[0].Id
                },
                new ImportedMeshMaterialBinding
                {
                    SourceMeshIndex = 1,
                    SourceMaterialIndex = 2,
                    SourceMeshName = "CurtainWall",
                    SourceMaterialName = "Curtain",
                    MaterialId = alternate.Id
                }
            ]
        };
        scene.Geometry.Add(geometry);

        Assert.DoesNotContain(SceneValidator.Validate(scene), issue => issue.Severity == ValidationSeverity.Error);
        var restored = ProjectStore.DeserializeManifest(ProjectStore.SerializeManifest(scene));
        Assert.Equal(2, restored.Geometry[^1].MaterialBindings.Count);
        Assert.Equal(alternate.Id, restored.Geometry[^1].MaterialBindings[1].MaterialId);

        scene.Geometry[^1] = geometry with
        {
            MaterialBindings =
            [
                geometry.MaterialBindings[0],
                geometry.MaterialBindings[1] with { SourceMeshIndex = 0, MaterialId = Guid.NewGuid() }
            ]
        };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path.EndsWith("/sourceMeshIndex", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/materialId", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetHashesMustBeValidUniqueAndNonNegative()
    {
        var scene = SampleScenes.CreateFlyby();
        scene.Assets.Add(new AssetRef
        {
            Sha256 = "not-a-sha256",
            Kind = AssetKind.Mesh,
            FileName = "bad.obj",
            MediaType = "model/obj",
            SizeBytes = -1
        });
        var validHash = new string('b', 64);
        scene.Assets.Add(new AssetRef
        {
            Sha256 = validHash,
            Kind = AssetKind.Mesh,
            FileName = "first.obj",
            MediaType = "model/obj",
            SizeBytes = 1
        });
        scene.Assets.Add(scene.Assets[^1] with { FileName = "duplicate.obj" });

        var issues = SceneValidator.Validate(scene);

        Assert.Contains(issues, issue => issue.Path == "assets/0/sha256");
        Assert.Contains(issues, issue => issue.Path == "assets/0/sizeBytes");
        Assert.Contains(issues, issue => issue.Path == "assets/2/sha256" && issue.Message.Contains("unique", StringComparison.Ordinal));
    }

    [Fact]
    public void ReceiverTakeArrangementRequiresVerifiedChannelsAndBoundedEdits()
    {
        var scene = SampleScenes.CreateFlyby();
        var hashes = new[] { new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64) };
        scene.Assets.AddRange(
        [
            new AssetRef { Sha256 = hashes[0], Kind = AssetKind.Render, FileName = "source.wav", MediaType = "audio/wav", SizeBytes = 100, SampleRate = 48000, Channels = 2, DurationSeconds = 1 },
            new AssetRef { Sha256 = hashes[1], Kind = AssetKind.Render, FileName = "left.wav", MediaType = "audio/wav", SizeBytes = 60, SampleRate = 48000, Channels = 1, DurationSeconds = 1 },
            new AssetRef { Sha256 = hashes[2], Kind = AssetKind.Render, FileName = "right.wav", MediaType = "audio/wav", SizeBytes = 60, SampleRate = 48000, Channels = 1, DurationSeconds = 1 },
            new AssetRef { Sha256 = hashes[3], Kind = AssetKind.Render, FileName = "receipt.json", MediaType = "application/vnd.pulsegen.render-receipt+json", SizeBytes = 80 }
        ]);
        scene.CaptureTakes.Add(new CaptureTake
        {
            Id = Guid.NewGuid(),
            ReceiverRigId = scene.ReceiverRigs[0].Id,
            Name = "Listener take",
            SourceAssetHash = hashes[0],
            LeftAssetHash = hashes[1],
            RightAssetHash = hashes[2],
            RenderReceiptHash = hashes[3],
            TrimEndSeconds = 0.75
        });

        Assert.Empty(SceneValidator.Validate(scene));

        scene.CaptureTakes[0] = scene.CaptureTakes[0] with
        {
            ReceiverRigId = Guid.NewGuid(),
            LeftAssetHash = new string('e', 64),
            LeftGainLinear = 5,
            TrimEndSeconds = 1.5
        };
        var issues = SceneValidator.Validate(scene);
        Assert.Contains(issues, issue => issue.Path.EndsWith("/receiverRigId", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/leftAssetHash", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/gain", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.EndsWith("/trim", StringComparison.Ordinal));
    }
}
