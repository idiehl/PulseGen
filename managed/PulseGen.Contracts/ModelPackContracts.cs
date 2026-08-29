using System.Text.Json.Serialization;

namespace PulseGen.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelPackFamily
{
    MusicStemSeparation,
    EnvironmentalTextQuery
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelRuntime
{
    PythonPyTorch,
    OnnxCpu,
    WinMl
}

public sealed record ModelArtifact
{
    public required Uri Source { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record ModelPackManifest
{
    public int SchemaVersion { get; init; } = ContractVersions.ModelPack;
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public required ModelPackFamily Family { get; init; }
    public required ModelRuntime Runtime { get; init; }
    public required int NativeSampleRate { get; init; }
    public required string LicenseNotice { get; init; }
    public required Uri ProjectUrl { get; init; }
    public required List<ModelArtifact> Artifacts { get; init; }
    public List<string> Outputs { get; init; } = [];
    public bool ArchivedUpstream { get; init; }
    public bool RequiresExplicitInstall { get; init; } = true;
    public string? KnownLimitation { get; init; }
}

