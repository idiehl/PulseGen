using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulseGen.Contracts;

namespace PulseGen.Engine.Client;

public readonly record struct RenderSummary(
    uint FilesWritten,
    double PeakLeft,
    double PeakRight,
    double RmsLeft,
    double RmsRight,
    ulong FramesRendered,
    uint SampleRate,
    double SpeedOfSoundMetresPerSecond,
    ulong DeterministicSeed,
    string RenderReceiptPath);

public readonly record struct PreviewDeviceStatus(
    bool Running,
    bool Recoverable,
    uint SampleRate,
    uint PeriodFrames,
    uint ReceiverIndex,
    ulong FramesProcessed,
    ulong CallbackCount,
    uint InterruptionCount,
    uint RecoveryCount,
    ulong CallbackDurationNanosecondsTotal,
    ulong CallbackDurationNanosecondsMaximum,
    ulong CallbackDeadlineMissCount,
    uint CallbackLoadPartsPerMillionMaximum,
    uint CallbackFramesMaximum)
{
    public double CallbackBudgetMilliseconds =>
        SampleRate == 0 ? 0 : 1000.0 * PeriodFrames / SampleRate;

    public double CallbackDurationMillisecondsAverage =>
        CallbackCount == 0 ? 0 : CallbackDurationNanosecondsTotal / (CallbackCount * 1_000_000.0);

    public double CallbackDurationMillisecondsMaximum =>
        CallbackDurationNanosecondsMaximum / 1_000_000.0;

    public double CallbackLoadPercentMaximum =>
        CallbackLoadPartsPerMillionMaximum / 10_000.0;
}

public sealed record ImportedMeshTriangleData(
    Guid GeometryId,
    string AssetSha256,
    Vector3d[] TriangleVertices,
    uint SourceMeshCount,
    uint SourceVertexCount,
    Vector3d BoundsMinimum,
    Vector3d BoundsMaximum)
{
    public int TriangleCount => TriangleVertices.Length / 3;
}

public static unsafe class NativeSceneRenderer
{
    private const uint AbiVersion = 4;
    private const string LibraryName = "PulseGen.Engine";
    private const string AcousticModel = "bounded-propagation-v7";
    private const string AtmosphereModel = "layered-bounded-wind-v1";
    private const string RoomResponseModel = "eyring-three-band-v1";
    private const long MaximumMeshAssetBytes = 512L * 1024 * 1024;
    private const uint MaximumImportedMeshTriangles = 250_000;
    private static readonly double[] NativeBandCentresHz = [125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    static NativeSceneRenderer()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeSceneRenderer).Assembly, ResolveLibrary);
    }

    public static string EngineVersion => Marshal.PtrToStringUTF8(Native.pg_get_engine_version()) ?? "unknown";

    public static IReadOnlyList<ImportedMeshTriangleData> DecodeImportedMeshes(
        SceneDocument scene,
        IReadOnlyCollection<Guid> geometryIds,
        string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(geometryIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        SceneValidator.EnsureValid(scene);

        var requestedIds = geometryIds.Distinct().ToArray();
        if (requestedIds.Length == 0)
        {
            return [];
        }
        var geometries = requestedIds.Select(id =>
        {
            var geometry = scene.Geometry.SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException($"Scene does not contain geometry '{id}'.", nameof(geometryIds));
            if (geometry.Kind != GeometryKind.ImportedMesh)
            {
                throw new ArgumentException($"Geometry '{geometry.Name}' is not an imported mesh.", nameof(geometryIds));
            }
            if (geometry.IsDynamic)
            {
                throw new NotSupportedException($"Dynamic acoustic geometry '{geometry.Name}' requires the motion adapter that is not active yet.");
            }
            return geometry;
        }).ToArray();

        var config = new NativeEngineConfig
        {
            Size = (uint)sizeof(NativeEngineConfig),
            Version = AbiVersion,
            MaxEmitters = 1,
            MaxReceivers = 1,
            MaxKeyframesPerPath = 2
        };
        Check(Native.pg_engine_create(&config, out var engine), engine, "create mesh decode engine");
        try
        {
            var materialIndexes = ConfigureMaterials(scene, engine);
            var results = new List<ImportedMeshTriangleData>(geometries.Length);
            foreach (var geometry in geometries)
            {
                if (geometry.MaterialId is not { } materialId || !materialIndexes.TryGetValue(materialId, out var materialIndex))
                {
                    throw new InvalidOperationException($"Imported mesh '{geometry.Name}' references a missing material.");
                }
                var receipt = ConfigureImportedMesh(scene, engine, geometry, materialIndex, projectDirectory, out var meshIndex);
                Check(
                    Native.pg_engine_copy_mesh_triangles(engine, meshIndex, null, 0, out var vertexCount),
                    engine,
                    "measure imported mesh triangle output");
                if (vertexCount != checked(receipt.TriangleCount * 3U))
                {
                    throw new InvalidDataException($"Imported mesh '{geometry.Name}' returned an inconsistent triangle-copy count.");
                }
                var nativeVertices = new NativeVector[checked((int)vertexCount)];
                fixed (NativeVector* nativeVertexPointer = nativeVertices)
                {
                    Check(
                        Native.pg_engine_copy_mesh_triangles(engine, meshIndex, nativeVertexPointer, vertexCount, out var copiedVertexCount),
                        engine,
                        "copy imported mesh triangles");
                    if (copiedVertexCount != vertexCount)
                    {
                        throw new InvalidDataException($"Imported mesh '{geometry.Name}' changed while copying triangle data.");
                    }
                }
                var vertices = new Vector3d[nativeVertices.Length];
                for (var index = 0; index < nativeVertices.Length; index++)
                {
                    vertices[index] = new(nativeVertices[index].X, nativeVertices[index].Y, nativeVertices[index].Z);
                }
                results.Add(new(
                    geometry.Id,
                    geometry.MeshAssetHash!,
                    vertices,
                    receipt.SourceMeshCount,
                    receipt.SourceVertexCount,
                    new(receipt.BoundsMinMetres.X, receipt.BoundsMinMetres.Y, receipt.BoundsMinMetres.Z),
                    new(receipt.BoundsMaxMetres.X, receipt.BoundsMaxMetres.Y, receipt.BoundsMaxMetres.Z)));
            }
            return results;
        }
        finally
        {
            Native.pg_engine_destroy(engine);
        }
    }

    public static RenderSummary Render(
        SceneDocument scene,
        string outputDirectory,
        RenderProfile? profile = null,
        string? projectDirectory = null)
    {
        SceneValidator.EnsureValid(scene);
        EnsureReceiverCapabilities(scene);
        profile ??= scene.RenderProfiles.FirstOrDefault()
            ?? throw new InvalidOperationException("The scene has no render profile.");
        SceneValidator.EnsureValid(scene with { RenderProfiles = [profile] });
        Directory.CreateDirectory(outputDirectory);

        var appliedAtmosphereSegments = profile.Quality == RenderQuality.Final
            ? profile.AtmosphereIntegrationSegments
            : 1;
        var engine = CreateConfiguredEngine(scene, projectDirectory, appliedAtmosphereSegments);
        try
        {
            var outputPointer = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(outputDirectory));
            var prefixPointer = Marshal.StringToCoTaskMemUTF8(SafePrefix(scene.Name));
            try
            {
                var request = new NativeRenderRequest
                {
                    Size = (uint)sizeof(NativeRenderRequest),
                    Version = AbiVersion,
                    OutputDirectoryUtf8 = outputPointer,
                    FilePrefixUtf8 = prefixPointer,
                    DurationSeconds = scene.Timeline.DurationSeconds,
                    SampleRate = (uint)profile.SampleRate,
                    SampleFormat = profile.SampleFormat switch
                    {
                        RenderSampleFormat.Pcm16 => 0,
                        RenderSampleFormat.Pcm24 => 1,
                        _ => 2
                    },
                    Quality = profile.Quality switch { RenderQuality.Draft => 0, RenderQuality.Final => 2, _ => 1 },
                    ReflectionOrder = (uint)profile.ReflectionOrder,
                    Seed = (uint)profile.Seed,
                    RayCount = (uint)profile.RayCount,
                    ImpulseResponseSeconds = profile.ImpulseResponseSeconds
                };
                var receipt = new NativeRenderReceipt { Size = (uint)sizeof(NativeRenderReceipt), Version = AbiVersion };
                Check(Native.pg_engine_render_offline(engine, &request, &receipt), engine, "offline render");
                var summary = new RenderSummary(
                    receipt.FilesWritten,
                    receipt.PeakLeft,
                    receipt.PeakRight,
                    receipt.RmsLeft,
                    receipt.RmsRight,
                    receipt.FramesRendered,
                    receipt.SampleRate,
                    receipt.SpeedOfSoundMetresPerSecond,
                    receipt.DeterministicSeed,
                    string.Empty);
                var receiptPath = WriteRenderReceipt(scene, profile, appliedAtmosphereSegments, outputDirectory, summary);
                return summary with { RenderReceiptPath = receiptPath };
            }
            finally
            {
                Marshal.FreeCoTaskMem(outputPointer);
                Marshal.FreeCoTaskMem(prefixPointer);
            }
        }
        finally
        {
            Native.pg_engine_destroy(engine);
        }
    }

    private static string WriteRenderReceipt(
        SceneDocument scene,
        RenderProfile profile,
        int appliedAtmosphereSegments,
        string outputDirectory,
        RenderSummary summary)
    {
        var output = Path.GetFullPath(outputDirectory);
        var prefix = SafePrefix(scene.Name);
        var takeFiles = scene.ReceiverRigs.Where(receiver => receiver.Enabled)
            .Select(receiver => new
            {
                Receiver = receiver,
                Path = Path.Combine(output, $"{prefix}_{NativeReceiverName(scene, receiver)}.wav")
            })
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .Select(item =>
            {
                if (!File.Exists(item.Path))
                {
                    throw new FileNotFoundException("The native engine did not produce an expected isolated receiver take.", item.Path);
                }
                using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                return new RenderedTakeIdentity(
                    item.Receiver.Id,
                    item.Receiver.Name,
                    Path.GetFileName(item.Path),
                    stream.Length,
                    Convert.ToHexStringLower(SHA256.HashData(stream)));
            })
            .ToArray();
        if (takeFiles.Length != summary.FilesWritten)
        {
            throw new InvalidDataException("Rendered take enumeration did not match the native engine receipt.");
        }
        var document = new RenderReceiptDocument(
            8,
            EngineVersion,
            AbiVersion,
            AcousticModel,
            AtmosphereModel,
            RoomResponseModel,
            scene.ProjectId,
            scene.Name,
            ProjectStore.ComputeSceneRevisionSha256(scene),
            scene.UpdatedAtUtc,
            profile.Id,
            profile.Name,
            profile.Quality,
            profile.SampleRate,
            profile.SampleFormat,
            profile.Dither,
            profile.ReflectionOrder,
            profile.RayCount,
            profile.ImpulseResponseSeconds,
            scene.Atmosphere.Layers.Count,
            scene.Atmosphere.WindZones.Count,
            appliedAtmosphereSegments,
            summary.DeterministicSeed,
            summary.FramesRendered,
            summary.FilesWritten,
            summary.PeakLeft,
            summary.PeakRight,
            summary.RmsLeft,
            summary.RmsRight,
            summary.SpeedOfSoundMetresPerSecond,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            [
                new("PulseGen.Engine", EngineVersion, "native sample renderer"),
                new("miniaudio", "0.11.25", "WASAPI device adapter"),
                new("Assimp", "6.0.5", "bounded memory-only OBJ/glTF/GLB mesh importer")
            ],
            [],
            takeFiles);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var destination = Path.Combine(output, $"{prefix}-render-receipt.json");
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, jsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record RenderDependency(string Name, string Version, string Role);
    private sealed record RenderedTakeIdentity(Guid ReceiverRigId, string ReceiverName, string FileName, long SizeBytes, string Sha256);
    private sealed record RenderReceiptDocument(
        int ReceiptVersion,
        string EngineVersion,
        uint NativeAbiVersion,
        string AcousticModel,
        string AtmosphereModel,
        string RoomResponseModel,
        Guid ProjectId,
        string SceneName,
        string SceneRevisionSha256,
        DateTimeOffset SceneUpdatedAtUtc,
        Guid RenderProfileId,
        string RenderProfileName,
        RenderQuality Quality,
        int SampleRate,
        RenderSampleFormat SampleFormat,
        DitherMode Dither,
        int ReflectionOrder,
        int RayCount,
        double ImpulseResponseSeconds,
        int AtmosphereLayerCount,
        int WindZoneCount,
        int AtmosphereIntegrationSegments,
        ulong Seed,
        ulong FramesRendered,
        uint FilesWritten,
        double PeakLeft,
        double PeakRight,
        double RmsLeft,
        double RmsRight,
        double SpeedOfSoundMetresPerSecond,
        string OperatingSystem,
        string ProcessArchitecture,
        string ManagedRuntime,
        IReadOnlyList<RenderDependency> Dependencies,
        IReadOnlyList<string> ModelPacks,
        IReadOnlyList<RenderedTakeIdentity> Takes);

    public static PreviewSession StartPreview(
        SceneDocument scene,
        uint enabledReceiverIndex = 0,
        uint sampleRate = 48000,
        uint periodFrames = 512,
        uint reflectionOrder = 1,
        uint rayCount = 1024,
        double impulseResponseSeconds = 2,
        uint seed = 1,
        bool exclusiveMode = false,
        double startTimeSeconds = 0,
        string? projectDirectory = null)
    {
        SceneValidator.EnsureValid(scene);
        EnsureReceiverCapabilities(scene);
        if (!double.IsFinite(startTimeSeconds) || startTimeSeconds < 0 || startTimeSeconds > scene.Timeline.DurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(startTimeSeconds));
        }
        var engine = CreateConfiguredEngine(scene, projectDirectory, atmosphereIntegrationSegments: 1);
        try
        {
            var config = new NativeDeviceConfig
            {
                Size = (uint)sizeof(NativeDeviceConfig),
                Version = AbiVersion,
                ReceiverIndex = enabledReceiverIndex,
                SampleRate = sampleRate,
                PeriodFrames = periodFrames,
                ReflectionOrder = reflectionOrder,
                ExclusiveMode = exclusiveMode ? 1U : 0U,
                StartFrame = checked((ulong)Math.Round(startTimeSeconds * sampleRate)),
                RayCount = rayCount,
                Seed = seed,
                ImpulseResponseSeconds = impulseResponseSeconds
            };
            Check(Native.pg_engine_start_device(engine, &config), engine, "start WASAPI preview");
            return new PreviewSession(engine);
        }
        catch
        {
            Native.pg_engine_destroy(engine);
            throw;
        }
    }

    public static PreviewSession StartBufferPreview(
        float[] interleavedStereo,
        uint sampleRate,
        uint periodFrames = 512,
        bool loop = false,
        bool exclusiveMode = false)
    {
        ArgumentNullException.ThrowIfNull(interleavedStereo);
        if (interleavedStereo.Length == 0 || (interleavedStereo.Length & 1) != 0)
        {
            throw new ArgumentException("A stereo preview requires a non-empty even number of interleaved samples.", nameof(interleavedStereo));
        }
        if (interleavedStereo.Any(sample => !float.IsFinite(sample)))
        {
            throw new ArgumentException("Stereo preview samples must all be finite.", nameof(interleavedStereo));
        }

        var engineConfig = new NativeEngineConfig
        {
            Size = (uint)sizeof(NativeEngineConfig),
            Version = AbiVersion,
            MaxEmitters = 1,
            MaxReceivers = 1,
            MaxKeyframesPerPath = 2
        };
        Check(Native.pg_engine_create(&engineConfig, out var engine), engine, "create buffer preview engine");
        try
        {
            fixed (float* samples = interleavedStereo)
            {
                var config = new NativeBufferDeviceConfig
                {
                    Size = (uint)sizeof(NativeBufferDeviceConfig),
                    Version = AbiVersion,
                    InterleavedStereo = samples,
                    FrameCount = checked((ulong)(interleavedStereo.Length / 2)),
                    SampleRate = sampleRate,
                    PeriodFrames = periodFrames,
                    ExclusiveMode = exclusiveMode ? 1U : 0U,
                    Loop = loop ? 1U : 0U
                };
                Check(Native.pg_engine_start_buffer_device(engine, &config), engine, "start stereo buffer preview");
            }
            return new PreviewSession(engine);
        }
        catch
        {
            Native.pg_engine_destroy(engine);
            throw;
        }
    }

    private static nint CreateConfiguredEngine(
        SceneDocument scene,
        string? projectDirectory,
        int atmosphereIntegrationSegments)
    {
        var config = new NativeEngineConfig
        {
            Size = (uint)sizeof(NativeEngineConfig),
            Version = AbiVersion,
            MaxEmitters = 64,
            MaxReceivers = 16,
            MaxKeyframesPerPath = 4096
        };

        Check(Native.pg_engine_create(&config, out var engine), engine, "create engine");
        try
        {
            ConfigureAtmosphere(scene, engine, atmosphereIntegrationSegments);
            var materialIndexes = ConfigureMaterials(scene, engine);
            ConfigureRoom(scene, engine);
            ConfigureGeometry(scene, engine, materialIndexes, projectDirectory);
            foreach (var emitter in scene.Emitters.Where(item => item.Enabled))
            {
                ConfigureEmitter(scene, engine, emitter);
            }
            foreach (var receiver in scene.ReceiverRigs.Where(item => item.Enabled))
            {
                ConfigureReceiver(scene, engine, receiver);
            }
            return engine;
        }
        catch
        {
            Native.pg_engine_destroy(engine);
            throw;
        }
    }

    public sealed unsafe class PreviewSession : IDisposable
    {
        private readonly object gate = new();
        private nint engine;

        internal PreviewSession(nint engineHandle)
        {
            engine = engineHandle;
        }

        public PreviewDeviceStatus Status
        {
            get
            {
                lock (gate)
                {
                    return ReadStatus();
                }
            }
        }

        public PreviewDeviceStatus RecoverAt(double timelineSeconds)
        {
            if (!double.IsFinite(timelineSeconds) || timelineSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timelineSeconds));
            }

            lock (gate)
            {
                var current = engine;
                if (current == 0)
                {
                    throw new ObjectDisposedException(nameof(PreviewSession));
                }
                var before = ReadStatus();
                if (!before.Recoverable || before.SampleRate == 0)
                {
                    throw new InvalidOperationException("The preview no longer has retained audio-device state to recover.");
                }
                var resumeFrame = checked((ulong)Math.Round(timelineSeconds * before.SampleRate));
                Check(Native.pg_engine_recover_device(current, resumeFrame), current, "recover the WASAPI preview");
                return ReadStatus();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                var current = engine;
                if (current == 0)
                {
                    return;
                }
                engine = 0;
                _ = Native.pg_engine_stop_device(current);
                Native.pg_engine_destroy(current);
            }
        }

        private PreviewDeviceStatus ReadStatus()
        {
            var current = engine;
            if (current == 0)
            {
                return default;
            }
            var status = new NativeDeviceStatus { Size = (uint)sizeof(NativeDeviceStatus), Version = AbiVersion };
            Check(Native.pg_engine_get_device_status(current, &status), current, "read preview device status");
            return new(
                status.Running != 0,
                status.Recoverable != 0,
                status.SampleRate,
                status.PeriodFrames,
                status.ReceiverIndex,
                status.FramesProcessed,
                status.CallbackCount,
                status.InterruptionCount,
                status.RecoveryCount,
                status.CallbackDurationNanosecondsTotal,
                status.CallbackDurationNanosecondsMaximum,
                status.CallbackDeadlineMissCount,
                status.CallbackLoadPartsPerMillionMaximum,
                status.CallbackFramesMaximum);
        }
    }

    private static void ConfigureAtmosphere(SceneDocument scene, nint engine, int integrationSegments)
    {
        if (scene.Atmosphere.Layers.Count > 64 || scene.Atmosphere.WindZones.Count > 64)
        {
            throw new InvalidOperationException("The native atmosphere field supports at most 64 layers and 64 wind zones.");
        }

        var layers = scene.Atmosphere.Layers
            .OrderBy(layer => layer.BaseAltitudeMetres)
            .Select(layer => new NativeAtmosphereLayer
            {
                Size = (uint)sizeof(NativeAtmosphereLayer),
                Version = AbiVersion,
                BaseAltitudeMetres = layer.BaseAltitudeMetres,
                TopAltitudeMetres = layer.TopAltitudeMetres,
                TemperatureCelsius = layer.TemperatureCelsius,
                RelativeHumidityPercent = layer.RelativeHumidityPercent,
                PressureKilopascals = layer.PressureKilopascals
            })
            .ToArray();
        var windZones = scene.Atmosphere.WindZones
            .Select(zone => new NativeWindZone
            {
                Size = (uint)sizeof(NativeWindZone),
                Version = AbiVersion,
                CentreMetres = ToNative(zone.Transform.PositionMetres),
                DimensionsMetres = ToNative(new Vector3d(
                    zone.DimensionsMetres.X * zone.Transform.Scale.X,
                    zone.DimensionsMetres.Y * zone.Transform.Scale.Y,
                    zone.DimensionsMetres.Z * zone.Transform.Scale.Z)),
                RotationDegrees = ToNative(zone.Transform.RotationDegrees),
                VelocityMetresPerSecond = ToNative(zone.VelocityMetresPerSecond),
                Turbulence = zone.Turbulence,
                NoiseSeed = checked((ulong)zone.NoiseSeed)
            })
            .ToArray();

        fixed (NativeAtmosphereLayer* layerPointer = layers)
        fixed (NativeWindZone* windZonePointer = windZones)
        {
            var descriptor = new NativeAtmosphereField
            {
                Size = (uint)sizeof(NativeAtmosphereField),
                Version = AbiVersion,
                TemperatureCelsius = scene.Atmosphere.TemperatureCelsius,
                RelativeHumidityPercent = scene.Atmosphere.RelativeHumidityPercent,
                PressureKilopascals = scene.Atmosphere.PressureKilopascals,
                Layers = layerPointer,
                LayerCount = (uint)layers.Length,
                WindZones = windZonePointer,
                WindZoneCount = (uint)windZones.Length,
                IntegrationSegments = checked((uint)integrationSegments)
            };
            Check(Native.pg_engine_set_atmosphere_field(engine, &descriptor), engine, "set layered atmosphere field");
        }
    }

    private static Dictionary<Guid, uint> ConfigureMaterials(SceneDocument scene, nint engine)
    {
        var indexes = new Dictionary<Guid, uint>(scene.Materials.Count);
        foreach (var material in scene.Materials)
        {
            var namePointer = Marshal.StringToCoTaskMemUTF8(material.Name);
            try
            {
                var descriptor = new NativeAcousticMaterial
                {
                    Size = (uint)sizeof(NativeAcousticMaterial),
                    Version = AbiVersion,
                    NameUtf8 = namePointer,
                    Roughness = material.Roughness
                };
                for (var index = 0; index < NativeBandCentresHz.Length; index++)
                {
                    var centre = NativeBandCentresHz[index];
                    descriptor.OctaveBandCentresHz[index] = centre;
                    descriptor.Absorption[index] = BandValue(material, material.Absorption, centre);
                    descriptor.Scattering[index] = BandValue(material, material.Scattering, centre);
                    descriptor.Transmission[index] = BandValue(material, material.Transmission, centre);
                }
                Check(Native.pg_engine_add_acoustic_material(engine, &descriptor, out var nativeIndex), engine, "add acoustic material");
                indexes.Add(material.Id, nativeIndex);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePointer);
            }
        }
        return indexes;
    }

    private static void ConfigureGeometry(
        SceneDocument scene,
        nint engine,
        IReadOnlyDictionary<Guid, uint> materialIndexes,
        string? projectDirectory)
    {
        foreach (var geometry in scene.Geometry.Where(item => item.Kind != GeometryKind.ParametricRoom && (item.Occludes || item.Reflects)))
        {
            if (geometry.IsDynamic)
            {
                throw new NotSupportedException($"Dynamic acoustic geometry '{geometry.Name}' requires the motion adapter that is not active yet.");
            }
            if (geometry.MaterialId is not { } materialId || !materialIndexes.TryGetValue(materialId, out var materialIndex))
            {
                throw new InvalidOperationException($"Acoustic geometry '{geometry.Name}' references a missing material.");
            }

            if (geometry.Kind == GeometryKind.ImportedMesh)
            {
                _ = ConfigureImportedMesh(scene, engine, geometry, materialIndex, projectDirectory, out _);
                continue;
            }

            var nativeKind = geometry.Kind switch
            {
                GeometryKind.Plane => 0,
                GeometryKind.Box => 1,
                _ => throw new NotSupportedException($"Acoustic geometry '{geometry.Name}' uses {geometry.Kind}, which requires a primitive adapter that is not active yet.")
            };

            var dimensions = new Vector3d(
                geometry.DimensionsMetres.X * geometry.Transform.Scale.X,
                geometry.DimensionsMetres.Y * geometry.Transform.Scale.Y,
                geometry.DimensionsMetres.Z * geometry.Transform.Scale.Z);
            var namePointer = Marshal.StringToCoTaskMemUTF8(geometry.Name);
            try
            {
                var descriptor = new NativeGeometry
                {
                    Size = (uint)sizeof(NativeGeometry),
                    Version = AbiVersion,
                    NameUtf8 = namePointer,
                    Kind = nativeKind,
                    CentreMetres = ToNative(geometry.Transform.PositionMetres),
                    DimensionsMetres = ToNative(dimensions),
                    RotationDegrees = ToNative(geometry.Transform.RotationDegrees),
                    MaterialIndex = materialIndex,
                    Occludes = geometry.Occludes ? 1U : 0U,
                    Reflects = geometry.Reflects ? 1U : 0U,
                    Enabled = 1U
                };
                Check(Native.pg_engine_add_geometry(engine, &descriptor, out _), engine, "add acoustic geometry");
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePointer);
            }
        }
    }

    private static NativeMeshImportReceipt ConfigureImportedMesh(
        SceneDocument scene,
        nint engine,
        AcousticGeometry geometry,
        uint materialIndex,
        string? projectDirectory,
        out uint meshIndex)
    {
        meshIndex = uint.MaxValue;
        if (projectDirectory is null)
        {
            throw new InvalidOperationException($"Imported mesh '{geometry.Name}' requires its owning .pulseproj directory for content-addressed asset verification.");
        }
        var asset = scene.Assets.SingleOrDefault(item =>
            item.Kind == AssetKind.Mesh && string.Equals(item.Sha256, geometry.MeshAssetHash, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Imported mesh '{geometry.Name}' references a missing mesh asset.");
        var format = Path.GetExtension(asset.FileName).TrimStart('.').ToLowerInvariant();
        if (format is not ("obj" or "gltf" or "glb"))
        {
            throw new NotSupportedException($"Imported mesh asset '{asset.FileName}' must be OBJ, glTF, or GLB.");
        }

        var path = ProjectStore.ResolveAssetPath(projectDirectory, asset.Sha256);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Content-addressed mesh asset for '{geometry.Name}' is missing.", path);
        }
        if (file.Length != asset.SizeBytes || file.Length is <= 0 or > MaximumMeshAssetBytes)
        {
            throw new InvalidDataException($"Mesh asset '{asset.FileName}' size does not match its manifest or exceeds the 512 MiB safety cap.");
        }
        byte[] encoded;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        {
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Mesh asset '{asset.FileName}' failed SHA-256 verification.");
            }
            stream.Position = 0;
            encoded = new byte[checked((int)file.Length)];
            stream.ReadExactly(encoded);
        }

        var namePointer = Marshal.StringToCoTaskMemUTF8(geometry.Name);
        var formatPointer = Marshal.StringToCoTaskMemUTF8(format);
        var receipt = new NativeMeshImportReceipt
        {
            Size = (uint)sizeof(NativeMeshImportReceipt),
            Version = AbiVersion
        };
        try
        {
            fixed (byte* encodedPointer = encoded)
            {
                var descriptor = new NativeMeshGeometry
                {
                    Size = (uint)sizeof(NativeMeshGeometry),
                    Version = AbiVersion,
                    NameUtf8 = namePointer,
                    EncodedData = encodedPointer,
                    EncodedSizeBytes = (ulong)encoded.Length,
                    FormatHintUtf8 = formatPointer,
                    CentreMetres = ToNative(geometry.Transform.PositionMetres),
                    Scale = ToNative(geometry.Transform.Scale),
                    RotationDegrees = ToNative(geometry.Transform.RotationDegrees),
                    MaterialIndex = materialIndex,
                    Occludes = geometry.Occludes ? 1U : 0U,
                    Reflects = geometry.Reflects ? 1U : 0U,
                    Enabled = 1U,
                    MaxTriangles = MaximumImportedMeshTriangles
                };
                Check(Native.pg_engine_add_mesh_geometry(engine, &descriptor, out meshIndex, &receipt), engine, "import acoustic mesh");
                if (receipt.TriangleCount == 0 || receipt.TriangleCount > MaximumImportedMeshTriangles || receipt.BvhNodeCount == 0)
                {
                    throw new InvalidDataException($"Imported mesh '{geometry.Name}' returned an invalid native import receipt.");
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
            Marshal.FreeCoTaskMem(formatPointer);
        }
        return receipt;
    }

    private static double BandValue(AcousticMaterial material, IReadOnlyList<double> values, double frequencyHz) =>
        AcousticRoomAnalysis.SampleBand(material, values, frequencyHz);

    private static void ConfigureRoom(SceneDocument scene, nint engine)
    {
        var room = scene.Geometry.FirstOrDefault(item => item.Kind == GeometryKind.ParametricRoom);
        if (room is null)
        {
            return;
        }
        if (room.IsDynamic || room.Transform.RotationDegrees.Length > 1e-9)
        {
            throw new NotSupportedException($"Parametric room '{room.Name}' must remain static and axis-aligned in the current modal-room solver.");
        }

        var material = scene.Materials.FirstOrDefault(item => item.Id == room.MaterialId)
            ?? throw new InvalidOperationException("The room references a missing acoustic material.");
        var dimensions = new Vector3d(
            room.DimensionsMetres.X * room.Transform.Scale.X,
            room.DimensionsMetres.Y * room.Transform.Scale.Y,
            room.DimensionsMetres.Z * room.Transform.Scale.Z);
        static double ReflectionGain(AcousticMaterial value, double frequency) => Math.Sqrt(Math.Max(
            0,
            1 - BandValue(value, value.Absorption, frequency) - BandValue(value, value.Transmission, frequency)));
        var roomEstimate = AcousticRoomAnalysis.Estimate(dimensions, material);
        var descriptor = new NativeRoom
        {
            Size = (uint)sizeof(NativeRoom),
            Version = AbiVersion,
            CentreMetres = ToNative(room.Transform.PositionMetres),
            DimensionsMetres = ToNative(dimensions),
            WallReflectionLow = ReflectionGain(material, 250),
            WallReflectionMid = ReflectionGain(material, 1000),
            WallReflectionHigh = ReflectionGain(material, 8000),
            Scattering = material.Scattering.Average(),
            Enabled = 1,
            DecayTimeSeconds = roomEstimate.MidFrequencyRt60Seconds,
            DecayTimeLowSeconds = roomEstimate.LowFrequencyRt60Seconds,
            DecayTimeHighSeconds = roomEstimate.HighFrequencyRt60Seconds
        };
        Check(Native.pg_engine_set_room(engine, &descriptor), engine, "set room");
    }

    private static void ConfigureEmitter(SceneDocument scene, nint engine, Emitter emitter)
    {
        var oscillator = emitter.SignalGraph.Nodes.FirstOrDefault(item => item.Kind is SignalNodeKind.Oscillator or SignalNodeKind.Noise or SignalNodeKind.Turbulence)
            ?? throw new NotSupportedException($"Emitter '{emitter.Name}' does not contain a primitive source supported by the alpha engine.");
        var path = scene.MotionPaths.FirstOrDefault(item => item.Id == emitter.MotionPathId)
            ?? throw new InvalidOperationException($"Emitter '{emitter.Name}' requires a motion path for native rendering.");
        var frames = path.Keyframes.Select(item => new NativeMotionKeyframe
        {
            TimeSeconds = item.TimeSeconds,
            PositionMetres = ToNative(item.PositionMetres)
        }).ToArray();
        var waveform = MapWaveform(oscillator.Waveform ?? WaveformKind.Sine);
        var partials = ReadAdditivePartials(oscillator);
        var namePointer = Marshal.StringToCoTaskMemUTF8(emitter.Name);
        try
        {
            fixed (NativeMotionKeyframe* framePointer = frames)
            fixed (NativeAdditivePartial* partialPointer = partials)
            {
                var descriptor = new NativeSignalEmitter
                {
                    Size = (uint)sizeof(NativeSignalEmitter),
                    Version = AbiVersion,
                    NameUtf8 = namePointer,
                    Waveform = waveform,
                    FrequencyHz = oscillator.NumericParameters.GetValueOrDefault("frequencyHz", 440),
                    Amplitude = oscillator.NumericParameters.GetValueOrDefault("amplitude", 1),
                    PhaseRadians = oscillator.NumericParameters.GetValueOrDefault("phaseRadians", 0),
                    PulseWidth = oscillator.NumericParameters.GetValueOrDefault("pulseWidth", 0.5),
                    GainLinear = emitter.GainLinear,
                    MinDistanceMetres = emitter.MinDistanceMetres,
                    AttackSeconds = oscillator.NumericParameters.GetValueOrDefault("attackSeconds", 0),
                    ReleaseSeconds = oscillator.NumericParameters.GetValueOrDefault("releaseSeconds", 0),
                    NonlinearDrive = oscillator.NumericParameters.GetValueOrDefault("drive", 0),
                    AdditivePartials = partialPointer,
                    AdditivePartialCount = (uint)partials.Length,
                    MotionPathKind = MapPath(path.Kind),
                    Keyframes = framePointer,
                    KeyframeCount = (uint)frames.Length,
                    NoiseSeed = (uint)Math.Clamp(oscillator.NumericParameters.GetValueOrDefault("seed", 1), 1, uint.MaxValue),
                    Enabled = 1
                };
                Check(Native.pg_engine_add_signal_emitter(engine, &descriptor, out _), engine, "add primitive signal emitter");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private static int MapWaveform(WaveformKind waveform) => waveform switch
    {
        WaveformKind.Sine => 0,
        WaveformKind.Triangle => 1,
        WaveformKind.Saw => 2,
        WaveformKind.Square => 3,
        WaveformKind.Pulse => 4,
        WaveformKind.WhiteNoise => 5,
        WaveformKind.PinkNoise => 6,
        WaveformKind.BrownNoise => 7,
        WaveformKind.Impulse => 8,
        WaveformKind.Additive => 9,
        WaveformKind.Turbulence => 10,
        _ => throw new NotSupportedException($"Waveform '{waveform}' requires an adapter that is not active in the alpha engine.")
    };

    private static int MapPath(MotionPathKind path) => path switch
    {
        MotionPathKind.Linear => 0,
        MotionPathKind.CatmullRom => 1,
        MotionPathKind.CubicBezier => 2,
        MotionPathKind.Arc or MotionPathKind.Orbit => 3,
        MotionPathKind.RecordedGesture => 4,
        _ => 0
    };

    private static NativeAdditivePartial[] ReadAdditivePartials(SignalNode node)
    {
        if (node.Waveform != WaveformKind.Additive)
        {
            return [];
        }

        var partials = new List<NativeAdditivePartial>();
        for (var index = 0; index < 1024; index++)
        {
            if (!node.NumericParameters.TryGetValue($"partial.{index}.amplitude", out var amplitude))
            {
                break;
            }
            partials.Add(new NativeAdditivePartial
            {
                FrequencyRatio = node.NumericParameters.GetValueOrDefault($"partial.{index}.ratio", index + 1),
                Amplitude = amplitude,
                PhaseRadians = node.NumericParameters.GetValueOrDefault($"partial.{index}.phaseRadians", 0)
            });
        }
        if (partials.Count == 0)
        {
            throw new InvalidOperationException("An additive source requires partial.N.amplitude parameters beginning at index zero.");
        }
        return [.. partials];
    }

    private static void ConfigureReceiver(SceneDocument scene, nint engine, ReceiverRig receiver)
    {
        var namePointer = Marshal.StringToCoTaskMemUTF8(NativeReceiverName(scene, receiver));
        try
        {
            var descriptor = new NativeReceiver
            {
                Size = (uint)sizeof(NativeReceiver),
                Version = AbiVersion,
                NameUtf8 = namePointer,
                Kind = receiver.Kind == ReceiverRigKind.BinauralHead ? 0 : 1,
                CentreMetres = ToNative(receiver.Transform.PositionMetres),
                YawDegrees = receiver.Transform.RotationDegrees.Y,
                SpacingMetres = receiver.SpacingMetres,
                HeadRadiusMetres = receiver.HeadRadiusMetres,
                MicrophoneAngleDegrees = receiver.AngleDegrees,
                PolarPattern = (int)receiver.PolarPattern,
                SensitivityDb = receiver.SensitivityDb,
                SelfNoiseDbSpl = receiver.SelfNoiseDbSpl,
                Enabled = 1
            };
            Check(Native.pg_engine_add_receiver(engine, &descriptor, out var receiverIndex), engine, "add receiver");
            if (receiver.Kind == ReceiverRigKind.StereoMicrophone)
            {
                var (left, right) = ReceiverRigGeometry.EffectiveMicrophoneCapsules(receiver);
                var leftDescriptor = ToNative(left);
                var rightDescriptor = ToNative(right);
                Check(
                    Native.pg_engine_set_receiver_capsules(engine, receiverIndex, &leftDescriptor, &rightDescriptor),
                    engine,
                    "set independent microphone capsules");
            }
            if (receiver.MotionPathId is not { } motionPathId)
            {
                return;
            }
            var path = scene.MotionPaths.First(item => item.Id == motionPathId);
            var frames = path.Keyframes.Select(item => new NativeMotionKeyframe
            {
                TimeSeconds = item.TimeSeconds,
                PositionMetres = ToNative(item.PositionMetres)
            }).ToArray();
            fixed (NativeMotionKeyframe* framePointer = frames)
            {
                var motion = new NativeReceiverMotion
                {
                    Size = (uint)sizeof(NativeReceiverMotion),
                    Version = AbiVersion,
                    ReceiverIndex = receiverIndex,
                    MotionPathKind = MapPath(path.Kind),
                    Keyframes = framePointer,
                    KeyframeCount = (uint)frames.Length
                };
                Check(Native.pg_engine_set_receiver_motion(engine, &motion), engine, "set receiver motion");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private static NativeReceiverCapsule ToNative(MicrophoneCapsule capsule) => new()
    {
        Size = (uint)sizeof(NativeReceiverCapsule),
        Version = AbiVersion,
        RelativePositionMetres = ToNative(capsule.RelativeTransform.PositionMetres),
        RelativeRotationDegrees = ToNative(capsule.RelativeTransform.RotationDegrees),
        PolarPattern = (int)capsule.PolarPattern,
        SensitivityDb = capsule.SensitivityDb,
        SelfNoiseDbSpl = capsule.SelfNoiseDbSpl
    };

    private static void EnsureReceiverCapabilities(SceneDocument scene)
    {
        var unsupported = scene.ReceiverRigs.FirstOrDefault(receiver =>
            receiver.Enabled && receiver.Kind == ReceiverRigKind.BinauralHead && receiver.HrtfMode != HrtfMode.Parametric);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"Receiver '{unsupported.Name}' uses {unsupported.HrtfMode}. The current renderer supports the analytic parametric head model; measured SOFA and bounded hybrid rendering remain gated until the Steam Audio adapter is active.");
        }
    }

    private static string SafePrefix(string value)
    {
        var safe = new string(value.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
        safe = string.IsNullOrWhiteSpace(safe) ? "PulseGen" : safe;
        return safe.Length > 63 ? safe[..63] : safe;
    }

    private static string SafeNativeName(string value, string fallback)
    {
        var safe = new string((string.IsNullOrEmpty(value) ? fallback : value).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        return safe.Length > 63 ? safe[..63] : safe;
    }

    private static string NativeReceiverName(SceneDocument scene, ReceiverRig receiver)
    {
        var safe = SafeNativeName(receiver.Name, "Receiver");
        var collides = scene.ReceiverRigs
            .Where(candidate => candidate.Enabled)
            .Count(candidate => string.Equals(SafeNativeName(candidate.Name, "Receiver"), safe, StringComparison.Ordinal)) > 1;
        if (!collides)
        {
            return safe;
        }

        const int guidCharacters = 32;
        const int separatorCharacters = 1;
        var prefixLength = 63 - guidCharacters - separatorCharacters;
        return $"{safe[..Math.Min(safe.Length, prefixLength)]}_{receiver.Id:N}";
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        var explicitPath = Environment.GetEnvironmentVariable("PULSEGEN_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (TryLoadCompatibleEngine(explicitPath, out var explicitHandle))
            {
                return explicitHandle;
            }
            throw new DllNotFoundException($"PULSEGEN_ENGINE_PATH does not identify a compatible PulseGen native engine: {explicitPath}");
        }

        var incompatibleCandidateFound = false;
        var localPath = Path.Combine(AppContext.BaseDirectory, "PulseGen.Engine.dll");
        if (TryLoadCompatibleEngine(localPath, out var localHandle))
        {
            return localHandle;
        }
        incompatibleCandidateFound |= File.Exists(localPath);

        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
#if DEBUG
            string[] configurations = ["debug", "release"];
#else
            string[] configurations = ["release", "debug"];
#endif
            foreach (var configuration in configurations)
            {
                var candidate = Path.Combine(directory.FullName, "out", "native", configuration, "native", "PulseGen.Engine.dll");
                if (TryLoadCompatibleEngine(candidate, out var candidateHandle))
                {
                    return candidateHandle;
                }
                incompatibleCandidateFound |= File.Exists(candidate);
            }
            directory = directory.Parent;
        }

        if (incompatibleCandidateFound)
        {
            throw new DllNotFoundException("PulseGen native engine candidates were found, but none expose the required ABI and bounded-propagation operations.");
        }

        return NativeLibrary.Load(libraryName, assembly, searchPath);
    }

    private static bool TryLoadCompatibleEngine(string path, out nint handle)
    {
        handle = 0;
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var candidate))
        {
            return false;
        }
        try
        {
            if (!NativeLibrary.TryGetExport(candidate, "pg_get_abi_version", out var abiExport) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_add_acoustic_material", out _) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_add_geometry", out _) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_add_mesh_geometry", out _) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_copy_mesh_triangles", out _) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_set_atmosphere_field", out _) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_set_receiver_motion", out _) ||
                !NativeLibrary.TryGetExport(candidate, "pg_engine_recover_device", out _))
            {
                return false;
            }
            var getAbiVersion = Marshal.GetDelegateForFunctionPointer<GetAbiVersionDelegate>(abiExport);
            if (getAbiVersion() != AbiVersion)
            {
                return false;
            }
            handle = candidate;
            candidate = 0;
            return true;
        }
        finally
        {
            if (candidate != 0)
            {
                NativeLibrary.Free(candidate);
            }
        }
    }

    private static NativeVector ToNative(Vector3d vector) => new() { X = vector.X, Y = vector.Y, Z = vector.Z };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAbiVersionDelegate();

    private static void Check(int status, nint engine, string operation)
    {
        if (status == 0)
        {
            return;
        }
        var detail = engine == 0 ? null : Marshal.PtrToStringUTF8(Native.pg_get_last_error(engine));
        throw new InvalidOperationException($"Could not {operation} (status {status}): {detail ?? "no detail"}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeVector { public double X, Y, Z; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeEngineConfig { public uint Size, Version, MaxEmitters, MaxReceivers, MaxKeyframesPerPath, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAtmosphereLayer { public uint Size, Version; public double BaseAltitudeMetres, TopAltitudeMetres, TemperatureCelsius, RelativeHumidityPercent, PressureKilopascals; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeWindZone { public uint Size, Version; public NativeVector CentreMetres, DimensionsMetres, RotationDegrees, VelocityMetresPerSecond; public double Turbulence; public ulong NoiseSeed; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAtmosphereField { public uint Size, Version; public double TemperatureCelsius, RelativeHumidityPercent, PressureKilopascals; public NativeAtmosphereLayer* Layers; public uint LayerCount; public NativeWindZone* WindZones; public uint WindZoneCount, IntegrationSegments, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRoom { public uint Size, Version; public NativeVector CentreMetres, DimensionsMetres; public double WallReflectionLow, WallReflectionMid, WallReflectionHigh, Scattering; public uint Enabled, Reserved; public double DecayTimeSeconds, DecayTimeLowSeconds, DecayTimeHighSeconds; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAcousticMaterial { public uint Size, Version; public nint NameUtf8; public fixed double OctaveBandCentresHz[8]; public fixed double Absorption[8]; public fixed double Scattering[8]; public fixed double Transmission[8]; public double Roughness; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeGeometry { public uint Size, Version; public nint NameUtf8; public int Kind; public NativeVector CentreMetres, DimensionsMetres, RotationDegrees; public uint MaterialIndex, Occludes, Reflects, Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshGeometry { public uint Size, Version; public nint NameUtf8; public void* EncodedData; public ulong EncodedSizeBytes; public nint FormatHintUtf8; public NativeVector CentreMetres, Scale, RotationDegrees; public uint MaterialIndex, Occludes, Reflects, Enabled, MaxTriangles, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshImportReceipt { public uint Size, Version, SourceMeshCount, SourceVertexCount, TriangleCount, BvhNodeCount; public NativeVector BoundsMinMetres, BoundsMaxMetres; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMotionKeyframe { public double TimeSeconds; public NativeVector PositionMetres; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAdditivePartial { public double FrequencyRatio, Amplitude, PhaseRadians; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSignalEmitter { public uint Size, Version; public nint NameUtf8; public int Waveform; public double FrequencyHz, Amplitude, PhaseRadians, PulseWidth, GainLinear, MinDistanceMetres, AttackSeconds, ReleaseSeconds, NonlinearDrive; public NativeAdditivePartial* AdditivePartials; public uint AdditivePartialCount; public int MotionPathKind; public NativeMotionKeyframe* Keyframes; public uint KeyframeCount, NoiseSeed, Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeReceiver { public uint Size, Version; public nint NameUtf8; public int Kind; public NativeVector CentreMetres; public double YawDegrees, SpacingMetres, HeadRadiusMetres, MicrophoneAngleDegrees; public int PolarPattern; public double SensitivityDb, SelfNoiseDbSpl; public uint Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeReceiverCapsule { public uint Size, Version; public NativeVector RelativePositionMetres, RelativeRotationDegrees; public int PolarPattern; public double SensitivityDb, SelfNoiseDbSpl; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeReceiverMotion { public uint Size, Version, ReceiverIndex; public int MotionPathKind; public NativeMotionKeyframe* Keyframes; public uint KeyframeCount, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRenderRequest { public uint Size, Version; public nint OutputDirectoryUtf8, FilePrefixUtf8; public double DurationSeconds; public uint SampleRate; public int SampleFormat, Quality; public uint ReflectionOrder, Seed; public nint CancellationFlag; public uint RayCount, Reserved; public double ImpulseResponseSeconds; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRenderReceipt { public uint Size, Version; public ulong FramesRendered; public uint FilesWritten, SampleRate; public double PeakLeft, PeakRight, RmsLeft, RmsRight, SpeedOfSoundMetresPerSecond; public ulong DeterministicSeed; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeDeviceConfig { public uint Size, Version, ReceiverIndex, SampleRate, PeriodFrames, ReflectionOrder, ExclusiveMode, Reserved; public ulong StartFrame; public uint RayCount, Seed; public double ImpulseResponseSeconds; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBufferDeviceConfig { public uint Size, Version; public float* InterleavedStereo; public ulong FrameCount; public uint SampleRate, PeriodFrames, ExclusiveMode, Loop; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeDeviceStatus { public uint Size, Version, Running, SampleRate, PeriodFrames, ReceiverIndex; public ulong FramesProcessed, CallbackCount; public uint Recoverable, InterruptionCount, RecoveryCount, Reserved; public ulong CallbackDurationNanosecondsTotal, CallbackDurationNanosecondsMaximum, CallbackDeadlineMissCount; public uint CallbackLoadPartsPerMillionMaximum, CallbackFramesMaximum; }

    private static class Native
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pg_get_engine_version();
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_create(NativeEngineConfig* config, out nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern void pg_engine_destroy(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pg_get_last_error(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_atmosphere_field(nint engine, NativeAtmosphereField* atmosphere);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_room(nint engine, NativeRoom* room);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_acoustic_material(nint engine, NativeAcousticMaterial* material, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_geometry(nint engine, NativeGeometry* geometry, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_mesh_geometry(nint engine, NativeMeshGeometry* geometry, out uint index, NativeMeshImportReceipt* receipt);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_copy_mesh_triangles(nint engine, uint meshIndex, NativeVector* triangleVertices, uint vertexCapacity, out uint vertexCount);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_signal_emitter(nint engine, NativeSignalEmitter* emitter, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_receiver(nint engine, NativeReceiver* receiver, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_receiver_capsules(nint engine, uint receiverIndex, NativeReceiverCapsule* leftCapsule, NativeReceiverCapsule* rightCapsule);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_receiver_motion(nint engine, NativeReceiverMotion* motion);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_render_offline(nint engine, NativeRenderRequest* request, NativeRenderReceipt* receipt);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_start_device(nint engine, NativeDeviceConfig* config);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_start_buffer_device(nint engine, NativeBufferDeviceConfig* config);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_recover_device(nint engine, ulong resumeFrame);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_stop_device(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_get_device_status(nint engine, NativeDeviceStatus* status);
    }
}
