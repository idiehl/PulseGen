using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace PulseGen.Engine.Client.Tests;

public sealed class SndFileRuntimeLoaderTests
{
    [Fact]
    public void DefaultLoadsOnlyThePinnedBinaryAndReportsItsActualIdentity()
    {
        var runtime = SndFileRuntimeLoader.Load(Path.Combine(AppContext.BaseDirectory, "sndfile.dll"), allowUserReplacement: false);
        try
        {
            Assert.Equal(SampleAudioReader.ExpectedBinarySha256, runtime.BinarySha256);
            Assert.Contains("1.2.2", runtime.Version, StringComparison.Ordinal);
            Assert.False(runtime.IsUserReplacement);
        }
        finally { NativeLibrary.Free(runtime.Handle); }
    }

    [Fact]
    public void DifferentCompatibleBinaryRequiresExplicitSelectionAndRetainsItsOwnHash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsegen-user-codec-{Guid.NewGuid():N}.dll");
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "sndfile.dll"), path);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
                stream.Write("PulseGen replacement-policy fixture"u8);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.NotEqual(SampleAudioReader.ExpectedBinarySha256, hash);
            Assert.Throws<InvalidDataException>(() => SndFileRuntimeLoader.Load(path, allowUserReplacement: false));

            var runtime = SndFileRuntimeLoader.Load(path, allowUserReplacement: true);
            try
            {
                Assert.Equal(hash, runtime.BinarySha256);
                Assert.True(runtime.IsUserReplacement);
                Assert.Contains("1.2.2", runtime.Version, StringComparison.Ordinal);
                Assert.True(NativeLibrary.TryGetExport(runtime.Handle, "sf_wchar_open", out _));
            }
            finally { NativeLibrary.Free(runtime.Handle); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingOrIncompatibleExplicitSelectionFailsInsteadOfFallingBack()
    {
        Assert.Throws<DllNotFoundException>(() => SndFileRuntimeLoader.Load(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"), allowUserReplacement: true));
        var exception = Assert.Throws<NotSupportedException>(() => SndFileRuntimeLoader.Load(
            Path.Combine(Environment.SystemDirectory, "kernel32.dll"), allowUserReplacement: true));
        Assert.Contains("required libsndfile API", exception.Message, StringComparison.Ordinal);
    }
}
