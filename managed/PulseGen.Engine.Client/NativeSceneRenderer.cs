using System.Reflection;
using System.Runtime.InteropServices;
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
    ulong DeterministicSeed);

public static unsafe class NativeSceneRenderer
{
    private const uint AbiVersion = 1;
    private const string LibraryName = "PulseGen.Engine";

    static NativeSceneRenderer()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeSceneRenderer).Assembly, ResolveLibrary);
    }

    public static string EngineVersion => Marshal.PtrToStringUTF8(Native.pg_get_engine_version()) ?? "unknown";

    public static RenderSummary Render(SceneDocument scene, string outputDirectory, RenderProfile? profile = null)
    {
        SceneValidator.EnsureValid(scene);
        Directory.CreateDirectory(outputDirectory);
        profile ??= scene.RenderProfiles.FirstOrDefault()
            ?? throw new InvalidOperationException("The scene has no render profile.");

        var config = new NativeEngineConfig
        {
            Size = (uint)sizeof(NativeEngineConfig), Version = AbiVersion,
            MaxEmitters = 64, MaxReceivers = 16, MaxKeyframesPerPath = 4096
        };

        Check(Native.pg_engine_create(&config, out var engine), engine, "create engine");
        try
        {
            ConfigureAtmosphere(scene, engine);
            ConfigureRoom(scene, engine);
            foreach (var emitter in scene.Emitters.Where(item => item.Enabled))
            {
                ConfigureEmitter(scene, engine, emitter);
            }
            foreach (var receiver in scene.ReceiverRigs.Where(item => item.Enabled))
            {
                ConfigureReceiver(engine, receiver);
            }

            var outputPointer = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(outputDirectory));
            var prefixPointer = Marshal.StringToCoTaskMemUTF8(SafePrefix(scene.Name));
            try
            {
                var request = new NativeRenderRequest
                {
                    Size = (uint)sizeof(NativeRenderRequest), Version = AbiVersion,
                    OutputDirectoryUtf8 = outputPointer, FilePrefixUtf8 = prefixPointer,
                    DurationSeconds = scene.Timeline.DurationSeconds,
                    SampleRate = (uint)profile.SampleRate, SampleFormat = 2,
                    Quality = profile.Quality switch { RenderQuality.Draft => 0, RenderQuality.Final => 2, _ => 1 },
                    ReflectionOrder = (uint)profile.ReflectionOrder, Seed = (uint)profile.Seed
                };
                var receipt = new NativeRenderReceipt { Size = (uint)sizeof(NativeRenderReceipt), Version = AbiVersion };
                Check(Native.pg_engine_render_offline(engine, &request, &receipt), engine, "offline render");
                return new(
                    receipt.FilesWritten,
                    receipt.PeakLeft,
                    receipt.PeakRight,
                    receipt.RmsLeft,
                    receipt.RmsRight,
                    receipt.FramesRendered,
                    receipt.SampleRate,
                    receipt.SpeedOfSoundMetresPerSecond,
                    receipt.DeterministicSeed);
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

    private static void ConfigureRoom(SceneDocument scene, nint engine)
    {
        var room = scene.Geometry.FirstOrDefault(item => item.Kind == GeometryKind.ParametricRoom);
        if (room is null)
        {
            return;
        }

        var material = scene.Materials.FirstOrDefault(item => item.Id == room.MaterialId)
            ?? throw new InvalidOperationException("The room references a missing acoustic material.");
        var descriptor = new NativeRoom
        {
            Size = (uint)sizeof(NativeRoom), Version = AbiVersion,
            CentreMetres = ToNative(room.Transform.PositionMetres), DimensionsMetres = ToNative(room.DimensionsMetres),
            WallReflectionLow = Math.Sqrt(1 - material.Absorption[1]),
            WallReflectionMid = Math.Sqrt(1 - material.Absorption[3]),
            WallReflectionHigh = Math.Sqrt(1 - material.Absorption[6]),
            Scattering = material.Scattering.Average(), Enabled = 1
        };
        Check(Native.pg_engine_set_room(engine, &descriptor), engine, "set room");
    }

    private static void ConfigureEmitter(SceneDocument scene, nint engine, Emitter emitter)
    {
        var oscillator = emitter.SignalGraph.Nodes.FirstOrDefault(item => item.Kind == SignalNodeKind.Oscillator)
            ?? throw new NotSupportedException($"Emitter '{emitter.Name}' does not contain an oscillator supported by the alpha engine.");
        var path = scene.MotionPaths.FirstOrDefault(item => item.Id == emitter.MotionPathId)
            ?? throw new InvalidOperationException($"Emitter '{emitter.Name}' requires a motion path for native rendering.");
        var frames = path.Keyframes.Select(item => new NativeMotionKeyframe
        {
            TimeSeconds = item.TimeSeconds, PositionMetres = ToNative(item.PositionMetres)
        }).ToArray();
        var namePointer = Marshal.StringToCoTaskMemUTF8(emitter.Name);
        try
        {
            fixed (NativeMotionKeyframe* framePointer = frames)
            {
                var descriptor = new NativeSineEmitter
                {
                    Size = (uint)sizeof(NativeSineEmitter), Version = AbiVersion, NameUtf8 = namePointer,
                    FrequencyHz = oscillator.NumericParameters["frequencyHz"],
                    Amplitude = oscillator.NumericParameters.GetValueOrDefault("amplitude", 1),
                    GainLinear = emitter.GainLinear, MinDistanceMetres = emitter.MinDistanceMetres,
                    Keyframes = framePointer, KeyframeCount = (uint)frames.Length, Enabled = 1
                };
                Check(Native.pg_engine_add_sine_emitter(engine, &descriptor, out _), engine, "add emitter");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
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
        return string.IsNullOrWhiteSpace(safe) ? "PulseGen" : safe;
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        var explicitPath = Environment.GetEnvironmentVariable("PULSEGEN_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && NativeLibrary.TryLoad(explicitPath, out var explicitHandle))
        {
            return explicitHandle;
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, "PulseGen.Engine.dll");
        if (NativeLibrary.TryLoad(localPath, out var localHandle))
        {
            return localHandle;
        }

        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            foreach (var configuration in new[] { "debug", "release" })
            {
                var candidate = Path.Combine(directory.FullName, "out", "native", configuration, "native", "PulseGen.Engine.dll");
                if (NativeLibrary.TryLoad(candidate, out var candidateHandle))
                {
                    return candidateHandle;
                }
            }
            directory = directory.Parent;
        }

        return NativeLibrary.Load(libraryName, assembly, searchPath);
    }

    private static NativeVector ToNative(Vector3d vector) => new() { X = vector.X, Y = vector.Y, Z = vector.Z };

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
    [StructLayout(LayoutKind.Sequential)] private struct NativeMotionKeyframe { public double TimeSeconds; public NativeVector PositionMetres; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSineEmitter { public uint Size, Version; public nint NameUtf8; public double FrequencyHz, Amplitude, GainLinear, MinDistanceMetres; public NativeMotionKeyframe* Keyframes; public uint KeyframeCount, Enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeReceiver { public uint Size, Version; public nint NameUtf8; public int Kind; public NativeVector CentreMetres; public double YawDegrees, SpacingMetres, HeadRadiusMetres, MicrophoneAngleDegrees; public int PolarPattern; public double SensitivityDb, SelfNoiseDbSpl; public uint Enabled, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRenderRequest { public uint Size, Version; public nint OutputDirectoryUtf8, FilePrefixUtf8; public double DurationSeconds; public uint SampleRate; public int SampleFormat, Quality; public uint ReflectionOrder, Seed; public nint CancellationFlag; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRenderReceipt { public uint Size, Version; public ulong FramesRendered; public uint FilesWritten, SampleRate; public double PeakLeft, PeakRight, RmsLeft, RmsRight, SpeedOfSoundMetresPerSecond; public ulong DeterministicSeed; }

    private static class Native
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pg_get_engine_version();
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_create(NativeEngineConfig* config, out nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern void pg_engine_destroy(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pg_get_last_error(nint engine);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_atmosphere(nint engine, NativeAtmosphere* atmosphere);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_set_room(nint engine, NativeRoom* room);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_sine_emitter(nint engine, NativeSineEmitter* emitter, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_add_receiver(nint engine, NativeReceiver* receiver, out uint index);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] internal static extern int pg_engine_render_offline(nint engine, NativeRenderRequest* request, NativeRenderReceipt* receipt);
    }
}
