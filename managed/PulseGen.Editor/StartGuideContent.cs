using PulseGen.Contracts;

namespace PulseGen.Editor;

internal enum StartGuideAction
{
    Continue,
    ExploreFlyby,
    ExploreSurfaceEcho,
    OpenProject
}

internal sealed record StartGuideResult(StartGuideAction Action, bool ShowOnLaunch);

internal sealed record StartGuideFeature(string Name, string Explanation, string Status);

internal static class StartGuideCatalog
{
    public static IReadOnlyList<StartGuideFeature> ReadyFeatures { get; } =
    [
        new("Build sounds", "Start with tones, pulses, noise, additive layers, or procedural wind.", "READY"),
        new("Bring in a recording", "Import WAV, AIFF, FLAC, MP3, Ogg, or Opus; choose moving sources, a stereo background, or an explicit mono mix.", "READY"),
        new("Move sound through space", "Select a point, choose Move, then edit in Top, Front, Side, or 3D. Focus selection sets the centre for Orbit.", "READY"),
        new("Place listeners and microphones", "Hear a headphone listener and independent stereo microphone pairs at the same time.", "READY"),
        new("Render and finish", "Keep each stereo take separate, edit left and right, then export a final master.", "READY"),
        new("Take a rendered take apart", "Separate rendered stereo into tracked tones, sharp events, and the remaining texture or noise.", "READY")
    ];

    public static IReadOnlyList<StartGuideFeature> GatedFeatures { get; } =
    [
        new("Measured ear profiles", "SOFA files can be stored with a project, but exact measured-head playback awaits the Steam Audio adapter.", "NOT ACTIVE"),
        new("Automatic source isolation", "Demucs and AudioSep model downloads remain off until their weight files and licenses are audited.", "NOT INSTALLED"),
        new("Moving acoustic meshes", "Imported walls and scenery can block and reflect sound, but those surfaces must remain still for now.", "NOT ACTIVE")
    ];

    public static SceneDocument CreateStarterScene(StartGuideAction action, DateTimeOffset? now = null)
    {
        var template = action switch
        {
            StartGuideAction.ExploreFlyby => SampleScenes.CreateFlyby(),
            StartGuideAction.ExploreSurfaceEcho => SampleScenes.CreateSurfaceEcho(),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "This action does not select a starter scene.")
        };
        var created = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return template with
        {
            ProjectId = Guid.NewGuid(),
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
            CaptureTakes = []
        };
    }
}
