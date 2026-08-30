namespace PulseGen.Contracts;

public readonly record struct AcousticRoomEstimate(
    double LowFrequencyRt60Seconds,
    double MidFrequencyRt60Seconds,
    double HighFrequencyRt60Seconds,
    double MeanFreePathMetres,
    double MeanReflectionIntervalMilliseconds);

public static class AcousticRoomAnalysis
{
    public static AcousticRoomEstimate Estimate(
        Vector3d dimensionsMetres,
        AcousticMaterial material,
        double speedOfSoundMetresPerSecond = 343.42)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!FinitePositive(dimensionsMetres.X) || !FinitePositive(dimensionsMetres.Y) || !FinitePositive(dimensionsMetres.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensionsMetres), "Room dimensions must be finite and positive.");
        }
        if (!double.IsFinite(speedOfSoundMetresPerSecond) || speedOfSoundMetresPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedOfSoundMetresPerSecond));
        }
        if (material.OctaveBandCentresHz.Length == 0 ||
            material.Absorption.Length != material.OctaveBandCentresHz.Length ||
            material.Transmission.Length != material.OctaveBandCentresHz.Length)
        {
            throw new ArgumentException("Material coefficient arrays must be non-empty and aligned.", nameof(material));
        }

        var volume = dimensionsMetres.X * dimensionsMetres.Y * dimensionsMetres.Z;
        var surfaceArea = 2 * (
            (dimensionsMetres.X * dimensionsMetres.Y) +
            (dimensionsMetres.X * dimensionsMetres.Z) +
            (dimensionsMetres.Y * dimensionsMetres.Z));
        var meanFreePath = 4 * volume / surfaceArea;

        double DecayAt(double frequencyHz)
        {
            var absorption = SampleBand(material, material.Absorption, frequencyHz);
            var transmission = SampleBand(material, material.Transmission, frequencyHz);
            var energyRemovedPerHit = Math.Clamp(absorption + transmission, 0.0001, 0.9999);
            var equivalentAbsorptionArea = -surfaceArea * Math.Log(1 - energyRemovedPerHit);
            var eyringConstant = 24 * Math.Log(10) / speedOfSoundMetresPerSecond;
            return Math.Clamp(eyringConstant * volume / equivalentAbsorptionArea, 0.05, 30);
        }

        return new(
            DecayAt(250),
            DecayAt(1000),
            DecayAt(8000),
            meanFreePath,
            meanFreePath / speedOfSoundMetresPerSecond * 1000);
    }

    public static double SampleBand(
        AcousticMaterial material,
        IReadOnlyList<double> values,
        double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != material.OctaveBandCentresHz.Length || values.Count == 0)
        {
            throw new ArgumentException("Band values must align with the material frequency centres.", nameof(values));
        }
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }
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
                var amount = (Math.Log2(frequencyHz) - Math.Log2(lower)) /
                    (Math.Log2(upper) - Math.Log2(lower));
                return values[index - 1] + ((values[index] - values[index - 1]) * Math.Clamp(amount, 0, 1));
            }
        }
        return values[last];
    }

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
}
