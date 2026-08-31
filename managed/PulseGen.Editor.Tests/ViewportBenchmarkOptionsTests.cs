using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class ViewportBenchmarkOptionsTests
{
    [Fact]
    public void NonBenchmarkArgumentsLeaveTheRegularEditorSelected()
    {
        Assert.Null(ViewportBenchmarkOptions.TryParse([]));
    }

    [Fact]
    public void BenchmarkArgumentsUseExplicitThresholdsAndAbsoluteReceiptPath()
    {
        var options = ViewportBenchmarkOptions.TryParse(
        [
            "--viewport-benchmark",
            "--seconds", "4.5",
            "--minimum-fps", "59",
            "--receipt", "out/viewport-test/receipt.json"
        ]);

        Assert.NotNull(options);
        Assert.Equal(4.5, options.DurationSeconds);
        Assert.Equal(59, options.MinimumFramesPerSecond);
        Assert.False(options.ImportedMesh);
        Assert.True(Path.IsPathFullyQualified(options.ReceiptPath));
        Assert.EndsWith(
            Path.Combine("out", "viewport-test", "receipt.json"),
            options.ReceiptPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportedMeshFlagSelectsTheVerifiedAssetBenchmark()
    {
        var options = ViewportBenchmarkOptions.TryParse(
            ["--viewport-benchmark", "--imported-mesh"]);

        Assert.NotNull(options);
        Assert.True(options.ImportedMesh);
    }

    [Theory]
    [InlineData("--seconds", "0.5")]
    [InlineData("--seconds", "31")]
    [InlineData("--minimum-fps", "0")]
    [InlineData("--minimum-fps", "241")]
    public void BenchmarkArgumentsRejectOutOfRangeValues(string option, string value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ViewportBenchmarkOptions.TryParse(["--viewport-benchmark", option, value]));
    }

    [Theory]
    [InlineData("--seconds")]
    [InlineData("--minimum-fps")]
    [InlineData("--receipt")]
    public void BenchmarkArgumentsRejectMissingValues(string option)
    {
        Assert.Throws<ArgumentException>(() =>
            ViewportBenchmarkOptions.TryParse(["--viewport-benchmark", option]));
    }
}
