using PulseGen.Contracts;

namespace PulseGen.Editor;

internal sealed record MaterialAuditionBandChoice(double FrequencyHz, string Name)
{
    public override string ToString() => Name;
}

internal sealed record AcousticMaterialPreset(
    string Name,
    string Description,
    double[] Absorption,
    double[] Scattering,
    double[] Transmission,
    double Roughness)
{
    public AcousticMaterial Create(Guid id, string? name = null) => new()
    {
        Id = id,
        Name = name ?? Name,
        OctaveBandCentresHz = AcousticMaterialPresets.StandardBandCentresHz.ToArray(),
        Absorption = Absorption.ToArray(),
        Scattering = Scattering.ToArray(),
        Transmission = Transmission.ToArray(),
        Roughness = Roughness
    };

    public AcousticMaterial ApplyTo(AcousticMaterial material) => material with
    {
        OctaveBandCentresHz = AcousticMaterialPresets.StandardBandCentresHz.ToArray(),
        Absorption = Absorption.ToArray(),
        Scattering = Scattering.ToArray(),
        Transmission = Transmission.ToArray(),
        Roughness = Roughness
    };

    public bool Matches(AcousticMaterial material) =>
        material.OctaveBandCentresHz.SequenceEqual(AcousticMaterialPresets.StandardBandCentresHz) &&
        material.Absorption.SequenceEqual(Absorption) &&
        material.Scattering.SequenceEqual(Scattering) &&
        material.Transmission.SequenceEqual(Transmission) &&
        Math.Abs(material.Roughness - Roughness) <= 1e-12;

    public override string ToString() => Name;
}

internal static class AcousticMaterialPresets
{
    public static readonly double[] StandardBandCentresHz = [125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static IReadOnlyList<AcousticMaterialPreset> All { get; } =
    [
        new(
            "Balanced wall",
            "A moderate, neutral starting point for an untreated wall. It returns a clear echo without behaving like polished stone.",
            [0.08, 0.08, 0.1, 0.12, 0.16, 0.2, 0.26, 0.32],
            [0.08, 0.09, 0.1, 0.12, 0.16, 0.2, 0.26, 0.32],
            [0.03, 0.025, 0.02, 0.015, 0.01, 0.005, 0.002, 0],
            0.35),
        new(
            "Polished stone",
            "A hard, smooth surface that returns most sound as a bright, distinct echo.",
            [0.01, 0.015, 0.02, 0.025, 0.035, 0.05, 0.08, 0.12],
            [0.03, 0.03, 0.04, 0.05, 0.08, 0.12, 0.18, 0.24],
            [0.01, 0.008, 0.005, 0.003, 0.002, 0.001, 0, 0],
            0.18),
        new(
            "Dense timber",
            "A solid wooden barrier with a warmer, less mirror-like echo and some low-frequency sound passing through.",
            [0.12, 0.1, 0.09, 0.08, 0.08, 0.1, 0.14, 0.2],
            [0.18, 0.18, 0.2, 0.24, 0.3, 0.36, 0.42, 0.48],
            [0.18, 0.14, 0.1, 0.08, 0.05, 0.03, 0.015, 0.008],
            0.52),
        new(
            "Heavy curtain",
            "A soft treatment that soaks up voices and high notes, leaving much less clear echo.",
            [0.08, 0.18, 0.42, 0.68, 0.82, 0.9, 0.94, 0.96],
            [0.12, 0.14, 0.18, 0.22, 0.28, 0.34, 0.4, 0.45],
            [0.12, 0.08, 0.04, 0.02, 0.01, 0.005, 0.002, 0.001],
            0.85),
        new(
            "Carpet and underlay",
            "A floor treatment that leaves bass comparatively lively while strongly damping footsteps and brighter sound.",
            [0.08, 0.12, 0.25, 0.45, 0.62, 0.72, 0.78, 0.8],
            [0.08, 0.1, 0.14, 0.2, 0.28, 0.36, 0.42, 0.46],
            [0, 0, 0, 0, 0, 0, 0, 0],
            0.92),
        new(
            "Smooth glass",
            "A hard, smooth boundary with a bright reflection and limited sound passing through this simplified acoustic layer.",
            [0.08, 0.06, 0.05, 0.04, 0.03, 0.03, 0.04, 0.06],
            [0.02, 0.02, 0.025, 0.03, 0.04, 0.05, 0.07, 0.1],
            [0.08, 0.06, 0.04, 0.025, 0.015, 0.008, 0.004, 0.002],
            0.08)
    ];

    public static AcousticMaterialPreset Default => All[0];
}

internal static class MaterialAuditionBands
{
    public static IReadOnlyList<MaterialAuditionBandChoice> All { get; } =
    [
        new(125, "125 Hz · deep bass"),
        new(250, "250 Hz · bass"),
        new(500, "500 Hz · low voice"),
        new(1000, "1 kHz · voice centre"),
        new(2000, "2 kHz · presence"),
        new(4000, "4 kHz · detail"),
        new(8000, "8 kHz · brightness"),
        new(16000, "16 kHz · air")
    ];
}

internal static class MaterialAuditionScene
{
    public static SceneDocument Create(AcousticMaterial sourceMaterial, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);
        if (!double.IsFinite(frequencyHz) || frequencyHz is < 20 or > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }

        var pathId = Guid.Parse("f1000000-0000-4000-8000-000000000001");
        var emitterId = Guid.Parse("f1000000-0000-4000-8000-000000000002");
        var oscillatorId = Guid.Parse("f1000000-0000-4000-8000-000000000003");
        var outputId = Guid.Parse("f1000000-0000-4000-8000-000000000004");
        var materialId = Guid.Parse("f1000000-0000-4000-8000-000000000005");
        var planeId = Guid.Parse("f1000000-0000-4000-8000-000000000006");
        var receiverId = Guid.Parse("f1000000-0000-4000-8000-000000000007");
        var profileId = Guid.Parse("f1000000-0000-4000-8000-000000000008");
        var material = sourceMaterial with
        {
            Id = materialId,
            OctaveBandCentresHz = sourceMaterial.OctaveBandCentresHz.ToArray(),
            Absorption = sourceMaterial.Absorption.ToArray(),
            Scattering = sourceMaterial.Scattering.ToArray(),
            Transmission = sourceMaterial.Transmission.ToArray()
        };
        var timestamp = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

        return new SceneDocument
        {
            ProjectId = Guid.Parse("f1000000-0000-4000-8000-000000000000"),
            Name = $"{material.Name} {frequencyHz:0} Hz audition",
            Atmosphere = new AtmosphereField
            {
                TemperatureCelsius = 20,
                RelativeHumidityPercent = 50,
                PressureKilopascals = 101.325
            },
            MotionPaths =
            [
                new MotionPath
                {
                    Id = pathId,
                    Name = "Stationary audition tone",
                    Kind = MotionPathKind.Linear,
                    Keyframes =
                    [
                        new MotionKeyframe { TimeSeconds = 0, PositionMetres = new(-2, 1.5, 0) },
                        new MotionKeyframe { TimeSeconds = 1.5, PositionMetres = new(-2, 1.5, 0) }
                    ]
                }
            ],
            Emitters =
            [
                new Emitter
                {
                    Id = emitterId,
                    Name = $"{frequencyHz:0} Hz comparison tone",
                    MotionPathId = pathId,
                    GainLinear = 0.7,
                    MinDistanceMetres = 0.5,
                    SignalGraph = new SignalGraph
                    {
                        Nodes =
                        [
                            new SignalNode
                            {
                                Id = oscillatorId,
                                Name = "Audition sine",
                                Kind = SignalNodeKind.Oscillator,
                                Waveform = WaveformKind.Sine,
                                NumericParameters = new Dictionary<string, double>
                                {
                                    ["frequencyHz"] = frequencyHz,
                                    ["amplitude"] = 0.45
                                }
                            },
                            new SignalNode { Id = outputId, Name = "Audition output", Kind = SignalNodeKind.Output }
                        ],
                        Connections =
                        [
                            new SignalConnection { FromNodeId = oscillatorId, FromPort = "out", ToNodeId = outputId, ToPort = "in" }
                        ]
                    }
                }
            ],
            Materials = [material],
            Geometry =
            [
                new AcousticGeometry
                {
                    Id = planeId,
                    Name = "Audition reflector",
                    Kind = GeometryKind.Plane,
                    MaterialId = materialId,
                    Transform = new Transform { PositionMetres = new(0, 1.5, 3) },
                    DimensionsMetres = new(12, 6, 0),
                    Occludes = false,
                    Reflects = true
                }
            ],
            ReceiverRigs =
            [
                new ReceiverRig
                {
                    Id = receiverId,
                    Name = "Audition microphones",
                    Kind = ReceiverRigKind.StereoMicrophone,
                    Transform = new Transform { PositionMetres = new(2, 1.5, 0) },
                    SpacingMetres = 0.12,
                    PolarPattern = PolarPattern.Omni
                }
            ],
            Timeline = new EventTimeline { DurationSeconds = 1.5 },
            RenderProfiles =
            [
                new RenderProfile
                {
                    Id = profileId,
                    Name = "Material audition",
                    Quality = RenderQuality.Interactive,
                    SampleRate = 48000,
                    BufferFrames = 512,
                    ReflectionOrder = 1,
                    RayCount = 4096,
                    ImpulseResponseSeconds = 1.25,
                    Seed = 20260830
                }
            ],
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp
        };
    }
}

internal readonly record struct MaterialEnergySummary(
    double Absorbed,
    double ClearReflection,
    double ScatteredAway,
    double Transmitted)
{
    public double Total => Absorbed + ClearReflection + ScatteredAway + Transmitted;
}

internal static class AcousticMaterialPhysics
{
    public static MaterialEnergySummary Average(AcousticMaterial material) =>
        Average(material.Absorption, material.Scattering, material.Transmission);

    public static MaterialEnergySummary Average(
        IReadOnlyList<double> absorption,
        IReadOnlyList<double> scattering,
        IReadOnlyList<double> transmission)
    {
        ArgumentNullException.ThrowIfNull(absorption);
        ArgumentNullException.ThrowIfNull(scattering);
        ArgumentNullException.ThrowIfNull(transmission);
        if (absorption.Count == 0 || scattering.Count != absorption.Count || transmission.Count != absorption.Count)
        {
            throw new ArgumentException("Material coefficient arrays must be non-empty and have matching lengths.");
        }

        double absorbed = 0;
        double clearReflection = 0;
        double scatteredAway = 0;
        double transmitted = 0;
        for (var index = 0; index < absorption.Count; index++)
        {
            var remaining = Math.Max(0, 1 - absorption[index] - transmission[index]);
            absorbed += absorption[index];
            clearReflection += remaining * (1 - scattering[index]);
            scatteredAway += remaining * scattering[index];
            transmitted += transmission[index];
        }

        var scale = 1.0 / absorption.Count;
        return new(
            absorbed * scale,
            clearReflection * scale,
            scatteredAway * scale,
            transmitted * scale);
    }
}
