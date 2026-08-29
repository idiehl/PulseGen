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
    uint SampleRate,
    uint PeriodFrames,
    uint ReceiverIndex,
    ulong FramesProcessed,
    ulong CallbackCount);

public static unsafe class NativeSceneRenderer
{
    private const uint AbiVersion = 1;
    private const string LibraryName = "PulseGen.Engine";
    private const string AcousticModel = "bounded-geometry-v2";
    private const long MaximumMeshAssetBytes = 512L * 1024 * 1024;
    private const uint MaximumImportedMeshTriangles = 250_000;
    private static readonly double[] NativeBandCentresHz = [125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    static NativeSceneRenderer()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeSceneRenderer).Assembly, ResolveLibrary);
    }

    public static string EngineVersion => Marshal.PtrToStringUTF8(Native.pg_get_engine_version()) ?? "unknown";

    public static RenderSummary Render(
        SceneDocument scene,
        string outputDirectory,
        RenderProfile? profile = null,
        string? projectDirectory = null)
    {
        SceneValidator.EnsureValid(scene);
        Directory.CreateDirectory(outputDirectory);
        profile ??= scene.RenderProfiles.FirstOrDefault()
            ?? throw new InvalidOperationException("The scene has no render profile.");

        var engine = CreateConfiguredEngine(scene, projectDirectory);
        try
        {
            var outputPointer = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(outputDirectory));
            var prefixPointer = Marshal.StringToCoTaskMemUTF8(SafePrefix(scene.Name));
            try
            {
                var request = new NativeRenderRequest
                {
                    Size = (uint)sizeof(NativeRenderRequest), Version = AbiVersion,
                    OutputDirectoryUtf8 = outputPointer, FilePrefixUtf8 = prefixPointer,
                    DurationSeconds = scene.Timeline.DurationSeconds,
                    SampleRate = (uint)profile.SampleRate,
                    SampleFormat = profile.SampleFormat switch
                    {
                        RenderSampleFormat.Pcm16 => 0,
                        RenderSampleFormat.Pcm24 => 1,
                        _ => 2
                    },
                    Quality = profile.Quality switch { RenderQuality.Draft => 0, RenderQuality.Final => 2, _ => 1 },
                    ReflectionOrder = (uint)profile.ReflectionOrder, Seed = (uint)profile.Seed
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
                var receiptPath = WriteRenderReceipt(scene, profile, outputDirectory, summary);
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
        string outputDirectory,
        RenderSummary summary)
    {
        var output = Path.GetFullPath(outputDirectory);
        var prefix = SafePrefix(scene.Name);
        var takeFiles = scene.ReceiverRigs.Where(receiver => receiver.Enabled)
            .Select(receiver => Path.Combine(output, $"{prefix}_{SafeNativeName(receiver.Name, "Receiver")}.wav"))
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("The native engine did not produce an expected isolated receiver take.", path);
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                return new RenderedTakeIdentity(
                    Path.GetFileName(path),
                    stream.Length,
                    Convert.ToHexStringLower(SHA256.HashData(stream)));
            })
            .ToArray();
        if (takeFiles.Length != summary.FilesWritten)
        {
            throw new InvalidDataException("Rendered take enumeration did not match the native engine receipt.");
        }
        var document = new RenderReceiptDocument(
            3,
            EngineVersion,
            AbiVersion,
            AcousticModel,
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
            profile.AtmosphereIntegrationSegments,
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
    private sealed record RenderedTakeIdentity(string FileName, long SizeBytes, string Sha256);
    private sealed record RenderReceiptDocument(
        int ReceiptVersion,
        string EngineVersion,
        uint NativeAbiVersion,
        string AcousticModel,
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
        bool exclusiveMode = false,
        double startTimeSeconds = 0,
        string? projectDirectory = null)
    {
        SceneValidator.EnsureValid(scene);
        if (!double.IsFinite(startTimeSeconds) || startTimeSeconds < 0 || startTimeSeconds > scene.Timeline.DurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(startTimeSeconds));
        }
        var engine = CreateConfiguredEngine(scene, projectDirectory);
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
                StartFrame = checked((ulong)Math.Round(startTimeSeconds * sampleRate))
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

    private static nint CreateConfiguredEngine(SceneDocument scene, string? projectDirectory)
    {
        var config = new NativeEngineConfig
        {
            Size = (uint)sizeof(NativeEngineConfig), Version = AbiVersion,
            MaxEmitters = 64, MaxReceivers = 16, MaxKeyframesPerPath = 4096
        };

        Check(Native.pg_engine_create(&config, out var engine), engine, "create engine");
        try
        {
            ConfigureAtmosphere(scene, engine);
            var materialIndexes = ConfigureMaterials(scene, engine);
            ConfigureRoom(scene, engine);
            ConfigureGeometry(scene, engine, materialIndexes, projectDirectory);
            foreach (var emitter in scene.Emitters.Where(item => item.Enabled))
            {
                ConfigureEmitter(scene, engine, emitter);
            }
            foreach (var receiver in scene.ReceiverRigs.Where(item => item.Enabled))
            {
                ConfigureReceiver(engine, receiver);
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
        private nint engine;

        internal PreviewSession(nint engineHandle)
        {
            engine = engineHandle;
        }

        public PreviewDeviceStatus Status
        {
            get
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
                    status.SampleRate,
                    status.PeriodFrames,
                    status.ReceiverIndex,
                    status.FramesProcessed,
                    status.CallbackCount);
            }
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref engine, 0);
            if (current == 0)
            {
                return;
            }
            _ = Native.pg_engine_stop_device(current);
            Native.pg_engine_destroy(current);
        }
    }

    private static void ConfigureAtmosphere(SceneDocument scene, nint engine)
    {
        var wind = scene.Atmosphere.WindZones.FirstOrDefault();
        var descriptor = new NativeAtmosphere
        {
            Size = (uint)sizeof(NativeAtmosphere), Version = AbiVersion,
            TemperatureCelsius = scene.Atmosphere.TemperatureCelsius,
            RelativeHumidityPercent = scene.Atmosphere.RelativeHumidityPercent,
            PressureKilopascals = scene.Atmosphere.PressureKilopascals,
            UniformWindMetresPerSecond = ToNative(wind?.VelocityMetresPerSecond ?? Vector3d.Zero),
            Turbulence = wind?.Turbulence ?? 0,
            NoiseSeed = (ulong)(wind?.NoiseSeed ?? 1)
        };
        Check(Native.pg_engine_set_atmosphere(engine, &descriptor), engine, "set atmosphere");
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
                ConfigureImportedMesh(scene, engine, geometry, materialIndex, projectDirectory);
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

    private static void ConfigureImportedMesh(
        SceneDocument scene,
        nint engine,
        AcousticGeometry geometry,
        uint materialIndex,
        string? projectDirectory)
    {
        if (geometry.Reflects)
        {
            throw new NotSupportedException(
                $"Imported-mesh reflections for '{geometry.Name}' require the geometric reflection backend; disable Reflects until that backend is active.");
        }
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
                    Reflects = 0U,
                    Enabled = 1U,
                    MaxTriangles = MaximumImportedMeshTriangles
                };
                var receipt = new NativeMeshImportReceipt
                {
                    Size = (uint)sizeof(NativeMeshImportReceipt),
                    Version = AbiVersion
                };
                Check(Native.pg_engine_add_mesh_geometry(engine, &descriptor, out _, &receipt), engine, "import acoustic mesh");
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
    }

    private static double BandValue(AcousticMaterial material, IReadOnlyList<double> values, double frequencyHz)
    {
        if (frequencyHz <= material.OctaveBandCentresHz[0])
        {
            return values[0];
        }
        var last = material.OctaveBandCentresHz.Length - 1;
        if (frequencyHz >= material.OctaveBandCentresHz[last])
        {
            return values[last];
        }
        for (var index = 1; index <= last; index++)
        {
            var upper = material.OctaveBandCentresHz[index];
            if (frequencyHz <= upper)
            {
                var lower = material.OctaveBandCentresHz[index - 1];
                var amount = (Math.Log2(frequencyHz) - Math.Log2(lower)) / (Math.Log2(upper) - Math.Log2(lower));
                return values[index - 1] + ((values[index] - values[index - 1]) * Math.Clamp(amount, 0, 1));
            }
        }
        return values[last];
    }

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
        var descriptor = new NativeRoom
        {
            Size = (uint)sizeof(NativeRoom), Version = AbiVersion,
            CentreMetres = ToNative(room.Transform.PositionMetres), DimensionsMetres = ToNative(dimensions),
            WallReflectionLow = ReflectionGain(material, 250),
            WallReflectionMid = ReflectionGain(material, 1000),
            WallReflectionHigh = ReflectionGain(material, 8000),
            Scattering = material.Scattering.Average(), Enabled = 1
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
            TimeSeconds = item.TimeSeconds, PositionMetres = ToNative(item.PositionMetres)
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
                    Size = (uint)sizeof(NativeSignalEmitter), Version = AbiVersion, NameUtf8 = namePointer,
                    Waveform = waveform,
                    FrequencyHz = oscillator.NumericParameters.GetValueOrDefault("frequencyHz", 440),
                    Amplitude = oscillator.NumericParameters.GetValueOrDefault("amplitude", 1),
                    PhaseRadians = oscillator.NumericParameters.GetValueOrDefault("phaseRadians", 0),
                    PulseWidth = oscillator.NumericParameters.GetValueOrDefault("pulseWidth", 0.5),
                    GainLinear = emitter.GainLinear, MinDistanceMetres = emitter.MinDistanceMetres,
                    AttackSeconds = oscillator.NumericParameters.GetValueOrDefault("attackSeconds", 0),
                    ReleaseSeconds = oscillator.NumericParameters.GetValueOrDefault("releaseSeconds", 0),
                    NonlinearDrive = oscillator.NumericParameters.GetValueOrDefault("drive", 0),
                    AdditivePartials = partialPointer, AdditivePartialCount = (uint)partials.Length,
                    MotionPathKind = MapPath(path.Kind),
                    Keyframes = framePointer, KeyframeCount = (uint)frames.Length,
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

    private static void ConfigureReceiver(nint engine, ReceiverRig receiver)
    {
        var namePointer = Marshal.StringToCoTaskMemUTF8(receiver.Name);
        try
        {
            var descriptor = new NativeReceiver
            {
                Size = (uint)sizeof(NativeReceiver), Version = AbiVersion, NameUtf8 = namePointer,
                Kind = receiver.Kind == ReceiverRigKind.BinauralHead ? 0 : 1,
                CentreMetres = ToNative(receiver.Transform.PositionMetres),
                YawDegrees = receiver.Transform.RotationDegrees.Y,
                SpacingMetres = receiver.SpacingMetres, HeadRadiusMetres = receiver.HeadRadiusMetres,
                MicrophoneAngleDegrees = receiver.AngleDegrees, PolarPattern = (int)receiver.PolarPattern,
                SensitivityDb = receiver.SensitivityDb, SelfNoiseDbSpl = receiver.SelfNoiseDbSpl, Enabled = 1
            };
            Check(Native.pg_engine_add_receiver(engine, &descriptor, out _), engine, "add receiver");
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
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
            throw new DllNotFoundException("PulseGen native engine candidates were found, but none expose the required ABI and primitive-acoustics operations.");
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
                !NativeLibrary.TryGetExport(candidate, "pg_engine_add_mesh_geometry", out _))
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeAtmosphere { public uint Size, Version; public double TemperatureCelsius, RelativeHumidityPercent, PressureKilopascals; public NativeVector UniformWindMetresPerSecond; public double Turbulence; public ulong NoiseSeed; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRoom { public uint Size, Version; public NativeVector CentreMetres, DimensionsMetres; public double WallReflectionLow, WallReflectionMid, WallReflectionHigh, Scattering; public uint Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAcousticMaterial { public uint Size, Version; public nint NameUtf8; public fixed double OctaveBandCentresHz[8]; public fixed double Absorption[8]; public fixed double Scattering[8]; public fixed double Transmission[8]; public double Roughness; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeGeometry { public uint Size, Version; public nint NameUtf8; public int Kind; public NativeVector CentreMetres, DimensionsMetres, RotationDegrees; public uint MaterialIndex, Occludes, Reflects, Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshGeometry { public uint Size, Version; public nint NameUtf8; public void* EncodedData; public ulong EncodedSizeBytes; public nint FormatHintUtf8; public NativeVector CentreMetres, Scale, RotationDegrees; public uint MaterialIndex, Occludes, Reflects, Enabled, MaxTriangles, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMeshImportReceipt { public uint Size, Version, SourceMeshCount, SourceVertexCount, TriangleCount, BvhNodeCount; public NativeVector BoundsMinMetres, BoundsMaxMetres; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMotionKeyframe { public double TimeSeconds; public NativeVector PositionMetres; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeAdditivePartial { public double FrequencyRatio, Amplitude, PhaseRadians; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSignalEmitter { public uint Size, Version; public nint NameUtf8; public int Waveform; public double FrequencyHz, Amplitude, PhaseRadians, PulseWidth, GainLinear, MinDistanceMetres, AttackSeconds, ReleaseSeconds, NonlinearDrive; public NativeAdditivePartial* AdditivePartials; public uint AdditivePartialCount; public int MotionPathKind; public NativeMotionKeyframe* Keyframes; public uint KeyframeCount, NoiseSeed, Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeReceiver { public uint Size, Version; public nint NameUtf8; public int Kind; public NativeVector CentreMetres; public double YawDegrees, SpacingMetres, HeadRadiusMetres, MicrophoneAngleDegrees; public int PolarPattern; public double SensitivityDb, SelfNoiseDbSpl; public uint Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRenderRequest { public uint Size, Version; public nint OutputDirectoryUtf8, FilePrefixUtf8; public double DurationSeconds; public uint SampleRate; public int SampleFormat, Quality; public uint ReflectionOrder, Seed; public nint CancellationFlag; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRenderReceipt { public uint Size, Version; public ulong FramesRendered; public uint FilesWritten, SampleRate; public double PeakLeft, PeakRight, RmsLeft, RmsRight, SpeedOfSoundMetresPerSecond; public ulong DeterministicSeed; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeDeviceConfig { public uint Size, Version, ReceiverIndex, SampleRate, PeriodFrames, ReflectionOrder, ExclusiveMode, Reserved; public ulong StartFrame; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeDeviceStatus { public uint Size, Version, Running, SampleRate, PeriodFrames, ReceiverIndex; public ulong FramesProcessed, CallbackCount; }

    private static class Native
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pg_get_engine_version();
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_create(NativeEngineConfig* config, out nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern void pg_engine_destroy(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pg_get_last_error(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_atmosphere(nint engine, NativeAtmosphere* atmosphere);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_room(nint engine, NativeRoom* room);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_acoustic_material(nint engine, NativeAcousticMaterial* material, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_geometry(nint engine, NativeGeometry* geometry, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_mesh_geometry(nint engine, NativeMeshGeometry* geometry, out uint index, NativeMeshImportReceipt* receipt);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_signal_emitter(nint engine, NativeSignalEmitter* emitter, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_receiver(nint engine, NativeReceiver* receiver, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_render_offline(nint engine, NativeRenderRequest* request, NativeRenderReceipt* receipt);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_start_device(nint engine, NativeDeviceConfig* config);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_stop_device(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_get_device_status(nint engine, NativeDeviceStatus* status);
    }
}
