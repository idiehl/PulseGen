using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class PackageSelfTestOptionsTests
{
    [Fact]
    public void IgnoresOrdinaryEditorLaunch()
    {
        Assert.Null(PackageSelfTestOptions.TryParse([]));
    }

    [Fact]
    public void ParsesExplicitReceiptAndOutput()
    {
        var options = PackageSelfTestOptions.TryParse(
        [
            "--package-self-test",
            "--receipt", "results/self-test.json",
            "--output", "results/render"
        ]);

        Assert.NotNull(options);
        Assert.Equal(Path.GetFullPath("results/self-test.json"), options.ReceiptPath);
        Assert.Equal(Path.GetFullPath("results/render"), options.OutputDirectory);
    }

    [Fact]
    public void RequiresJsonReceipt()
    {
        Assert.Throws<ArgumentException>(() => PackageSelfTestOptions.TryParse(
        [
            "--package-self-test",
            "--receipt", "results/self-test.txt"
        ]));
    }
}
