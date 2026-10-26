using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PulseGen.Engine.Client;

internal sealed record SndFileRuntimeLibrary(nint Handle, string Version, string BinarySha256, bool IsUserReplacement);

/// <summary>Loads the pinned default or an explicitly selected compatible replacement once per process.</summary>
internal static class SndFileRuntimeLoader
{
    private static readonly Lazy<SndFileRuntimeLibrary> Runtime = new(() =>
    {
        var selected = Environment.GetEnvironmentVariable("PULSEGEN_SNDFILE_PATH");
        return Load(
            string.IsNullOrWhiteSpace(selected) ? Path.Combine(AppContext.BaseDirectory, "sndfile.dll") : selected,
            allowUserReplacement: !string.IsNullOrWhiteSpace(selected));
    });

    internal static SndFileRuntimeLibrary Current => Runtime.Value;

    internal static SndFileRuntimeLibrary Load(string path, bool allowUserReplacement)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new DllNotFoundException("The selected sndfile.dll is missing. Install the bundled codec or correct PULSEGEN_SNDFILE_PATH.");

        // Keep the verified file open through native loading so it cannot be replaced between these steps.
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!allowUserReplacement && !string.Equals(hash, SampleAudioReader.ExpectedBinarySha256, StringComparison.Ordinal))
            throw new InvalidDataException("Refusing to load a bundled codec that does not match its pinned SHA-256. An intentional compatible replacement must be selected through PULSEGEN_SNDFILE_PATH.");

        var library = NativeLibrary.Load(fullPath);
        try
        {
            // Check the complete API used by both the importer and exporter before either can use this handle.
            string[] required = ["sf_version_string", "sf_wchar_open", "sf_format_check", "sf_readf_float",
                "sf_writef_float", "sf_writef_short", "sf_writef_int", "sf_command", "sf_set_string",
                "sf_get_string", "sf_strerror", "sf_write_sync", "sf_close", "sf_error"];
            foreach (var symbol in required)
                if (!NativeLibrary.TryGetExport(library, symbol, out _))
                    throw new NotSupportedException($"The selected codec is incompatible: required libsndfile API {symbol} is missing.");

            var versionFunction = Marshal.GetDelegateForFunctionPointer<VersionFunction>(NativeLibrary.GetExport(library, "sf_version_string"));
            var version = Marshal.PtrToStringUTF8(versionFunction()) ?? "unknown";
            if (!version.StartsWith("libsndfile-1.", StringComparison.Ordinal))
                throw new NotSupportedException($"The selected codec reports an unsupported libsndfile API version: {version}.");
            return new(library, version, hash, allowUserReplacement && hash != SampleAudioReader.ExpectedBinarySha256);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint VersionFunction();
}
