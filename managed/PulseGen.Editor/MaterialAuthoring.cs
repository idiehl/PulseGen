using PulseGen.Contracts;

namespace PulseGen.Editor;

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
