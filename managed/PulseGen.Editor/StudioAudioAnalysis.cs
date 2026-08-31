namespace PulseGen.Editor;

internal sealed record StudioAudioMetrics(
    double SamplePeakLinear,
    double? SamplePeakDbFs,
    double TruePeakLinear,
    double? TruePeakDbTp,
    double? IntegratedLoudnessLufs,
    double? LoudnessRangeLu,
    double? MaximumMomentaryLoudnessLufs,
    double? MaximumShortTermLoudnessLufs);

/// <summary>
/// Measures a stereo master using the K-weighting and gated programme-loudness
/// model defined by ITU-R BS.1770 / EBU R 128. The true-peak interpolator uses
/// the standard 4x / 2x sampling guidance for 48 / 96 kHz programme audio.
/// </summary>
internal static class StudioAudioAnalyzer
{
    private const double LoudnessOffset = -0.691;
    private const double AbsoluteGateLufs = -70;
    private const double RelativeIntegratedGateLu = -10;
    private const double RelativeLraGateLu = -20;

    public static StudioAudioMetrics Analyze(FloatWaveBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Channels != 2 || buffer.SampleRate is < 8000 or > 192000 ||
            buffer.InterleavedSamples.Length == 0 || buffer.InterleavedSamples.Length % 2 != 0 ||
            buffer.InterleavedSamples.Any(sample => !float.IsFinite(sample)))
        {
            throw new ArgumentException("Studio measurement requires finite, non-empty stereo audio at a supported sample rate.", nameof(buffer));
        }

        var samplePeak = buffer.InterleavedSamples.Max(sample => Math.Abs((double)sample));
        var truePeak = MeasureTruePeak(buffer, samplePeak);
        var power = BuildKWeightedPower(buffer);
        var prefix = BuildPrefix(power);
        var momentaryEnergies = WindowEnergies(prefix, buffer.SampleRate, 0.4, 0.1);
        var shortTermEnergies = WindowEnergies(prefix, buffer.SampleRate, 3, 1);
        var integrated = MeasureIntegratedLoudness(momentaryEnergies);
        var loudnessRange = MeasureLoudnessRange(shortTermEnergies, integrated);

        return new StudioAudioMetrics(
            samplePeak,
            ToDecibels(samplePeak),
            truePeak,
            ToDecibels(truePeak),
            integrated,
            loudnessRange,
            MaximumLoudness(momentaryEnergies),
            MaximumLoudness(shortTermEnergies));
    }

    private static double[] BuildKWeightedPower(FloatWaveBuffer buffer)
    {
        var leftShelf = Biquad.CreateHighShelf(buffer.SampleRate);
        var leftHighPass = Biquad.CreateHighPass(buffer.SampleRate);
        var rightShelf = Biquad.CreateHighShelf(buffer.SampleRate);
        var rightHighPass = Biquad.CreateHighPass(buffer.SampleRate);
        var power = new double[buffer.Frames];
        for (var frame = 0; frame < buffer.Frames; frame++)
        {
            var left = leftHighPass.Process(leftShelf.Process(buffer.InterleavedSamples[frame * 2]));
            var right = rightHighPass.Process(rightShelf.Process(buffer.InterleavedSamples[(frame * 2) + 1]));
            power[frame] = (left * left) + (right * right);
        }
        return power;
    }

    private static double[] BuildPrefix(IReadOnlyList<double> values)
    {
        var prefix = new double[values.Count + 1];
        for (var index = 0; index < values.Count; index++)
        {
            prefix[index + 1] = prefix[index] + values[index];
        }
        return prefix;
    }

    private static double[] WindowEnergies(double[] prefix, int sampleRate, double windowSeconds, double stepSeconds)
    {
        var window = checked((int)Math.Round(windowSeconds * sampleRate, MidpointRounding.AwayFromZero));
        var step = checked((int)Math.Round(stepSeconds * sampleRate, MidpointRounding.AwayFromZero));
        var frames = prefix.Length - 1;
        if (frames < window)
        {
            return [];
        }

        var energies = new double[1 + ((frames - window) / step)];
        for (var block = 0; block < energies.Length; block++)
        {
            var start = block * step;
            energies[block] = (prefix[start + window] - prefix[start]) / window;
        }
        return energies;
    }

    private static double? MeasureIntegratedLoudness(IReadOnlyList<double> blockEnergies)
    {
        var absoluteGated = blockEnergies
            .Where(energy => EnergyToLoudness(energy) >= AbsoluteGateLufs)
            .ToArray();
        if (absoluteGated.Length == 0)
        {
            return null;
        }

        var preliminary = EnergyToLoudness(absoluteGated.Average());
        var relativeGate = preliminary + RelativeIntegratedGateLu;
        var final = absoluteGated
            .Where(energy => EnergyToLoudness(energy) >= relativeGate)
            .ToArray();
        return final.Length == 0 ? null : EnergyToLoudness(final.Average());
    }

    private static double? MeasureLoudnessRange(IReadOnlyList<double> shortTermEnergies, double? integratedLoudness)
    {
        if (integratedLoudness is null)
        {
            return null;
        }
        var gate = Math.Max(AbsoluteGateLufs, integratedLoudness.Value + RelativeLraGateLu);
        var values = shortTermEnergies
            .Select(EnergyToLoudness)
            .Where(value => value >= gate)
            .Order()
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }
        return Percentile(values, 0.95) - Percentile(values, 0.10);
    }

    private static double? MaximumLoudness(IReadOnlyList<double> energies)
    {
        if (energies.Count == 0)
        {
            return null;
        }
        var maximum = energies.Max();
        return maximum > 0 ? EnergyToLoudness(maximum) : null;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double proportion)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }
        var position = proportion * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static double EnergyToLoudness(double energy) =>
        energy > 0 ? LoudnessOffset + (10 * Math.Log10(energy)) : double.NegativeInfinity;

    private static double? ToDecibels(double linear) =>
        linear > 0 ? 20 * Math.Log10(linear) : null;

    private static double MeasureTruePeak(FloatWaveBuffer buffer, double samplePeak)
    {
        var factor = buffer.SampleRate < 96000 ? 4 : buffer.SampleRate < 192000 ? 2 : 1;
        if (factor == 1)
        {
            return samplePeak;
        }

        const int halfTaps = 24;
        var maximum = samplePeak;
        for (var channel = 0; channel < 2; channel++)
        {
            for (var frame = 0; frame < buffer.Frames; frame++)
            {
                for (var phase = 1; phase < factor; phase++)
                {
                    var outputIndex = checked((frame * factor) + phase);
                    var firstInput = DivideCeiling(outputIndex - halfTaps, factor);
                    var lastInput = (outputIndex + halfTaps) / factor;
                    var value = 0d;
                    for (var input = firstInput; input <= lastInput; input++)
                    {
                        if ((uint)input >= (uint)buffer.Frames)
                        {
                            continue;
                        }
                        var delta = outputIndex - (input * factor);
                        var normalized = (double)delta / factor;
                        var sinc = delta == 0 ? 1 : Math.Sin(Math.PI * normalized) / (Math.PI * normalized);
                        var window = 0.5 * (1 + Math.Cos(Math.PI * delta / halfTaps));
                        value += buffer.InterleavedSamples[(input * 2) + channel] * sinc * window;
                    }
                    maximum = Math.Max(maximum, Math.Abs(value));
                }
            }
        }
        return maximum;
    }

    private static int DivideCeiling(int dividend, int divisor) =>
        dividend >= 0 ? (dividend + divisor - 1) / divisor : dividend / divisor;

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double z1;
        private double z2;

        public double Process(double input)
        {
            var output = (b0 * input) + z1;
            z1 = (b1 * input) - (a1 * output) + z2;
            z2 = (b2 * input) - (a2 * output);
            return output;
        }

        public static Biquad CreateHighShelf(int sampleRate)
        {
            const double frequency = 1681.974450955533;
            const double gainDb = 3.999843853973347;
            const double q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * frequency / sampleRate);
            var vh = Math.Pow(10, gainDb / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var denominator = 1 + (k / q) + (k * k);
            return new(
                (vh + (vb * k / q) + (k * k)) / denominator,
                2 * ((k * k) - vh) / denominator,
                (vh - (vb * k / q) + (k * k)) / denominator,
                2 * ((k * k) - 1) / denominator,
                (1 - (k / q) + (k * k)) / denominator);
        }

        public static Biquad CreateHighPass(int sampleRate)
        {
            const double frequency = 38.13547087602444;
            const double q = 0.5003270373238773;
            var k = Math.Tan(Math.PI * frequency / sampleRate);
            var denominator = 1 + (k / q) + (k * k);
            return new(
                1 / denominator,
                -2 / denominator,
                1 / denominator,
                2 * ((k * k) - 1) / denominator,
                (1 - (k / q) + (k * k)) / denominator);
        }
    }
}
