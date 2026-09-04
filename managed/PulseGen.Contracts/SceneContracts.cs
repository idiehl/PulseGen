using System.Text.Json.Serialization;

namespace PulseGen.Contracts;

public static class ContractVersions
{
    public const int SceneDocument = 1;
    public const int NativeAbi = 5;
    public const int ModelPack = 1;
}

public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d Zero => new(0, 0, 0);

    [JsonIgnore]
    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    public static Vector3d operator +(Vector3d left, Vector3d right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3d operator -(Vector3d left, Vector3d right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3d operator *(Vector3d value, double scalar) =>
        new(value.X * scalar, value.Y * scalar, value.Z * scalar);
}

public sealed record Transform
{
    public Vector3d PositionMetres { get; init; } = Vector3d.Zero;
    public Vector3d RotationDegrees { get; init; } = Vector3d.Zero;
    public Vector3d Scale { get; init; } = new(1, 1, 1);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntityKind
{
    Emitter,
    Geometry,
    ReceiverRig,
    AtmosphereZone
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaveformKind
{
    Sine,
    Triangle,
    Saw,
    Square,
    Pulse,
    WhiteNoise,
    PinkNoise,
    BrownNoise,
    Impulse,
    Wavetable,
    Additive,
    ModalResonator,
    Waveguide,
    Turbulence,
    Sample
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalNodeKind
{
    Oscillator,
    Noise,
    Envelope,
    Lfo,
    Filter,
    Nonlinearity,
    AdditiveBank,
    ModalResonator,
    Delay,
    Waveguide,
    Turbulence,
    Sample,
    Output
}

public sealed record SignalNode
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required SignalNodeKind Kind { get; init; }
    public WaveformKind? Waveform { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplePlayback? SamplePlayback { get; init; }
    public Dictionary<string, double> NumericParameters { get; init; } = [];
    public Dictionary<string, string> TextParameters { get; init; } = [];
}

public sealed record SamplePlayback
{
    public required string AssetSha256 { get; init; }
    public int ChannelIndex { get; init; }
    public double StartTimeSeconds { get; init; }
    public double OffsetSeconds { get; init; }
    public bool Loop { get; init; }
}

public sealed record SignalConnection
{
    public required Guid FromNodeId { get; init; }
    public required string FromPort { get; init; }
    public required Guid ToNodeId { get; init; }
    public required string ToPort { get; init; }
}

public sealed record SignalGraph
{
    public int SchemaVersion { get; init; } = 1;
    public List<SignalNode> Nodes { get; init; } = [];
    public List<SignalConnection> Connections { get; init; } = [];
}

public sealed record Emitter
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Transform Transform { get; init; } = new();
    public required SignalGraph SignalGraph { get; init; }
    public Guid? MotionPathId { get; init; }
    public double GainLinear { get; init; } = 1;
    public double MinDistanceMetres { get; init; } = 1;
    public double Directivity { get; init; }
    public SourceChannelMapping ChannelMapping { get; init; } = SourceChannelMapping.MonoPoint;
    public Guid? LinkedRightEmitterId { get; init; }
    public bool Enabled { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceChannelMapping
{
    MonoPoint,
    LinkedDualEmitters,
    NonSpatialStereoBed,
    ExplicitMonoFoldDown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MotionPathKind
{
    Linear,
    CubicBezier,
    CatmullRom,
    Arc,
    Orbit,
    RecordedGesture
}

public sealed record MotionKeyframe
{
    public required double TimeSeconds { get; init; }
    public required Vector3d PositionMetres { get; init; }
    public Vector3d? InTangentMetres { get; init; }
    public Vector3d? OutTangentMetres { get; init; }
}

public sealed record MotionPath
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public MotionPathKind Kind { get; init; } = MotionPathKind.Linear;
    public List<MotionKeyframe> Keyframes { get; init; } = [];
    public List<MotionKeyframe> RawGesture { get; init; } = [];
    public List<VelocityControlPoint> VelocityProfile { get; init; } = [];
    public bool Loop { get; init; }
}

public sealed record VelocityControlPoint
{
    public required double TimeSeconds { get; init; }
    public required double SpeedMetresPerSecond { get; init; }
}

public sealed record AcousticMaterial
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public double[] OctaveBandCentresHz { get; init; } = [125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public double[] Absorption { get; init; } = [0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1];
    public double[] Scattering { get; init; } = [0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05];
    public double[] Transmission { get; init; } = [0, 0, 0, 0, 0, 0, 0, 0];
    public double Roughness { get; init; } = 0.25;
    public string? VisualTextureAssetHash { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeometryKind
{
    Plane,
    Box,
    Sphere,
    Cylinder,
    Arch,
    Tunnel,
    ParametricRoom,
    Terrain,
    ImportedMesh
}

public sealed record ImportedMeshMaterialBinding
{
    public required uint SourceMeshIndex { get; init; }
    public required uint SourceMaterialIndex { get; init; }
    public required string SourceMeshName { get; init; }
    public required string SourceMaterialName { get; init; }
    public required Guid MaterialId { get; init; }
}

public sealed record AcousticGeometry
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required GeometryKind Kind { get; init; }
    public Transform Transform { get; init; } = new();
    public Guid? MaterialId { get; init; }
    public Vector3d DimensionsMetres { get; init; } = new(10, 4, 8);
    public string? MeshAssetHash { get; init; }
    public List<ImportedMeshMaterialBinding> MaterialBindings { get; init; } = [];
    public bool IsDynamic { get; init; }
    public bool Occludes { get; init; } = true;
    public bool Reflects { get; init; } = true;
}

public sealed record WindZone
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Transform Transform { get; init; } = new();
    public Vector3d DimensionsMetres { get; init; } = new(20, 20, 20);
    public Vector3d VelocityMetresPerSecond { get; init; } = Vector3d.Zero;
    public double Turbulence { get; init; }
    public double NoiseSeed { get; init; } = 1;
}

public sealed record AtmosphereLayer
{
    public required double BaseAltitudeMetres { get; init; }
    public required double TopAltitudeMetres { get; init; }
    public double TemperatureCelsius { get; init; } = 20;
    public double RelativeHumidityPercent { get; init; } = 50;
    public double PressureKilopascals { get; init; } = 101.325;
}

public sealed record AtmosphereField
{
    public double TemperatureCelsius { get; init; } = 20;
    public double RelativeHumidityPercent { get; init; } = 50;
    public double PressureKilopascals { get; init; } = 101.325;
    public List<AtmosphereLayer> Layers { get; init; } = [];
    public List<WindZone> WindZones { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReceiverRigKind
{
    BinauralHead,
    StereoMicrophone
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HrtfMode
{
    Parametric,
    ExactSofa,
    BoundedHybrid
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolarPattern
{
    Omni,
    Cardioid,
    Supercardioid,
    FigureEight
}

public sealed record ReceiverRig
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required ReceiverRigKind Kind { get; init; }
    public Transform Transform { get; init; } = new();
    public double SpacingMetres { get; init; } = 0.18;
    public double AngleDegrees { get; init; }
    public double HeadRadiusMetres { get; init; } = 0.09;
    public Vector3d HeadDimensionsMetres { get; init; } = new(0.18, 0.22, 0.24);
    public HrtfMode HrtfMode { get; init; } = HrtfMode.Parametric;
    public string? SofaAssetHash { get; init; }
    public PolarPattern PolarPattern { get; init; } = PolarPattern.Omni;
    public double SensitivityDb { get; init; }
    public double SelfNoiseDbSpl { get; init; }
    public MicrophoneCapsule? LeftCapsule { get; init; }
    public MicrophoneCapsule? RightCapsule { get; init; }
    public Guid? MotionPathId { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record MicrophoneCapsule
{
    public Transform RelativeTransform { get; init; } = new();
    public PolarPattern PolarPattern { get; init; } = PolarPattern.Omni;
    public double SensitivityDb { get; init; }
    public double SelfNoiseDbSpl { get; init; }
    public double[] ResponseCentresHz { get; init; } = [20, 100, 1000, 10000, 20000];
    public double[] ResponseDb { get; init; } = [0, 0, 0, 0, 0];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineEventKind
{
    StartEmitter,
    StopEmitter,
    SetParameter,
    BeginCapture,
    EndCapture,
    Marker
}

public sealed record TimelineEvent
{
    public required Guid Id { get; init; }
    public required TimelineEventKind Kind { get; init; }
    public required double TimeSeconds { get; init; }
    public Guid? TargetId { get; init; }
    public Dictionary<string, double> NumericParameters { get; init; } = [];
    public string? Label { get; init; }
}

public sealed record EventTimeline
{
    public double DurationSeconds { get; init; } = 6;
    public List<TimelineEvent> Events { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenderQuality
{
    Draft,
    Interactive,
    Final
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenderSampleFormat
{
    Pcm16,
    Pcm24,
    Float32
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DitherMode
{
    None,
    Tpdf
}

public sealed record RenderProfile
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public RenderQuality Quality { get; init; } = RenderQuality.Interactive;
    public int SampleRate { get; init; } = 48000;
    public RenderSampleFormat SampleFormat { get; init; } = RenderSampleFormat.Float32;
    public DitherMode Dither { get; init; } = DitherMode.None;
    public int BufferFrames { get; init; } = 512;
    public int ReflectionOrder { get; init; } = 1;
    public int RayCount { get; init; } = 1024;
    public double ImpulseResponseSeconds { get; init; } = 2;
    public int AtmosphereIntegrationSegments { get; init; } = 1;
    public int Seed { get; init; } = 1;
}

public sealed record CaptureTake
{
    public required Guid Id { get; init; }
    public required Guid ReceiverRigId { get; init; }
    public required string Name { get; init; }
    public string? SourceAssetHash { get; init; }
    public string? LeftAssetHash { get; init; }
    public string? RightAssetHash { get; init; }
    public bool ChannelsLinked { get; init; } = true;
    public bool IncludedInMaster { get; init; } = true;
    public bool LeftMuted { get; init; }
    public bool RightMuted { get; init; }
    public double LeftGainLinear { get; init; } = 1;
    public double RightGainLinear { get; init; } = 1;
    public double TimelineOffsetSeconds { get; init; }
    public double TrimStartSeconds { get; init; }
    public double? TrimEndSeconds { get; init; }
    public DateTimeOffset? RenderedAtUtc { get; init; }
    public string? RenderReceiptHash { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetKind
{
    Audio,
    Mesh,
    Material,
    Hrtf,
    ImpulseResponse,
    ModelOutput,
    Render,
    ProjectManifest
}

public sealed record AssetRef
{
    public required string Sha256 { get; init; }
    public required AssetKind Kind { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long SizeBytes { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Provenance { get; init; }
    public string? License { get; init; }
}

public sealed record SceneDocument
{
    public int SchemaVersion { get; init; } = ContractVersions.SceneDocument;
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public string Units { get; init; } = "SI";
    public int DefaultSampleRate { get; init; } = 48000;
    public required AtmosphereField Atmosphere { get; init; }
    public List<Emitter> Emitters { get; init; } = [];
    public List<MotionPath> MotionPaths { get; init; } = [];
    public List<AcousticMaterial> Materials { get; init; } = [];
    public List<AcousticGeometry> Geometry { get; init; } = [];
    public List<ReceiverRig> ReceiverRigs { get; init; } = [];
    public EventTimeline Timeline { get; init; } = new();
    public List<RenderProfile> RenderProfiles { get; init; } = [];
    public List<CaptureTake> CaptureTakes { get; init; } = [];
    public List<AssetRef> Assets { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
