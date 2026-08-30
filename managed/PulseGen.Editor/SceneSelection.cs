using PulseGen.Contracts;

namespace PulseGen.Editor;

internal enum SceneSelectionKind
{
    None,
    Emitter,
    MotionPath,
    Geometry,
    BaseAtmosphere,
    AtmosphereLayer,
    WindZone,
    Receiver
}

internal readonly record struct SceneSelection(
    SceneSelectionKind Kind,
    Guid Id = default,
    int Index = -1)
{
    public static SceneSelection Emitter(Guid id) => new(SceneSelectionKind.Emitter, id);

    public static SceneSelection MotionPath(Guid id) => new(SceneSelectionKind.MotionPath, id);

    public static SceneSelection Geometry(Guid id) => new(SceneSelectionKind.Geometry, id);

    public static SceneSelection BaseAtmosphere() => new(SceneSelectionKind.BaseAtmosphere);

    public static SceneSelection AtmosphereLayer(int index) => new(SceneSelectionKind.AtmosphereLayer, Index: index);

    public static SceneSelection WindZone(Guid id) => new(SceneSelectionKind.WindZone, id);

    public static SceneSelection Receiver(Guid id) => new(SceneSelectionKind.Receiver, id);

    public bool IsSpatial => Kind is SceneSelectionKind.Geometry or SceneSelectionKind.WindZone;
}

internal sealed record MaterialChoice(Guid Id, string Name)
{
    public override string ToString() => Name;
}

internal static class SceneSelectionResolver
{
    public static bool Exists(SceneDocument scene, SceneSelection selection) => selection.Kind switch
    {
        SceneSelectionKind.Emitter => scene.Emitters.Any(item => item.Id == selection.Id),
        SceneSelectionKind.MotionPath => scene.MotionPaths.Any(item => item.Id == selection.Id),
        SceneSelectionKind.Geometry => scene.Geometry.Any(item => item.Id == selection.Id),
        SceneSelectionKind.BaseAtmosphere => true,
        SceneSelectionKind.AtmosphereLayer => selection.Index >= 0 && selection.Index < scene.Atmosphere.Layers.Count,
        SceneSelectionKind.WindZone => scene.Atmosphere.WindZones.Any(item => item.Id == selection.Id),
        SceneSelectionKind.Receiver => scene.ReceiverRigs.Any(item => item.Id == selection.Id),
        _ => false
    };

    public static Emitter? Emitter(SceneDocument scene, SceneSelection selection) =>
        selection.Kind == SceneSelectionKind.Emitter
            ? scene.Emitters.FirstOrDefault(item => item.Id == selection.Id)
            : null;

    public static MotionPath? MotionPath(SceneDocument scene, SceneSelection selection)
    {
        Guid? pathId = selection.Kind switch
        {
            SceneSelectionKind.MotionPath => selection.Id,
            SceneSelectionKind.Emitter => Emitter(scene, selection)?.MotionPathId,
            SceneSelectionKind.Receiver => scene.ReceiverRigs
                .FirstOrDefault(item => item.Id == selection.Id)?.MotionPathId,
            _ => null
        };

        if (pathId is not null)
        {
            return scene.MotionPaths.FirstOrDefault(item => item.Id == pathId.Value);
        }

        return null;
    }

    public static string UniqueName(string stem, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(stem))
        {
            return stem;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} {suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

internal static class SceneAuthoringOperations
{
    public static bool TryAddGeometry(
        SceneDocument scene,
        AcousticGeometry geometry,
        out ValidationIssue? error) =>
        TryAdd(scene.Geometry, geometry, scene, out error);

    public static bool TryAddWindZone(
        SceneDocument scene,
        WindZone zone,
        out ValidationIssue? error) =>
        TryAdd(scene.Atmosphere.WindZones, zone, scene, out error);

    public static bool TryAddAtmosphereLayer(
        SceneDocument scene,
        AtmosphereLayer layer,
        out ValidationIssue? error) =>
        TryAdd(scene.Atmosphere.Layers, layer, scene, out error);

    public static bool TryReplaceEmitter(
        SceneDocument scene,
        Guid id,
        Emitter replacement,
        out ValidationIssue? error) =>
        TryReplace(scene.Emitters, item => item.Id == id, replacement, scene, out error);

    public static bool TryReplaceMotionPath(
        SceneDocument scene,
        Guid id,
        MotionPath replacement,
        out ValidationIssue? error) =>
        TryReplace(scene.MotionPaths, item => item.Id == id, replacement, scene, out error);

    public static bool TryReplaceGeometry(
        SceneDocument scene,
        Guid id,
        AcousticGeometry replacement,
        out ValidationIssue? error) =>
        TryReplace(scene.Geometry, item => item.Id == id, replacement, scene, out error);

    public static bool TryReplaceWindZone(
        SceneDocument scene,
        Guid id,
        WindZone replacement,
        out ValidationIssue? error) =>
        TryReplace(scene.Atmosphere.WindZones, item => item.Id == id, replacement, scene, out error);

    public static bool TryReplaceAtmosphereLayer(
        SceneDocument scene,
        int index,
        AtmosphereLayer replacement,
        out ValidationIssue? error)
    {
        if (index < 0 || index >= scene.Atmosphere.Layers.Count)
        {
            error = new ValidationIssue("atmosphere/layers", "The selected atmosphere layer no longer exists.");
            return false;
        }

        var original = scene.Atmosphere.Layers[index];
        scene.Atmosphere.Layers[index] = replacement;
        error = FirstError(scene);
        if (error is not null)
        {
            scene.Atmosphere.Layers[index] = original;
            return false;
        }

        return true;
    }

    public static bool TryReplaceBaseAtmosphere(
        SceneDocument scene,
        AtmosphereField replacement,
        out SceneDocument updatedScene,
        out ValidationIssue? error)
    {
        updatedScene = scene with { Atmosphere = replacement };
        error = FirstError(updatedScene);
        if (error is not null)
        {
            updatedScene = scene;
            return false;
        }

        return true;
    }

    private static bool TryAdd<T>(
        List<T> items,
        T item,
        SceneDocument scene,
        out ValidationIssue? error)
    {
        items.Add(item);
        error = FirstError(scene);
        if (error is not null)
        {
            items.RemoveAt(items.Count - 1);
            return false;
        }

        return true;
    }

    private static bool TryReplace<T>(
        List<T> items,
        Predicate<T> predicate,
        T replacement,
        SceneDocument scene,
        out ValidationIssue? error)
    {
        var index = items.FindIndex(predicate);
        if (index < 0)
        {
            error = new ValidationIssue("scene/selection", "The selected scene object no longer exists.");
            return false;
        }

        var original = items[index];
        items[index] = replacement;
        error = FirstError(scene);
        if (error is not null)
        {
            items[index] = original;
            return false;
        }

        return true;
    }

    private static ValidationIssue? FirstError(SceneDocument scene) =>
        SceneValidator.Validate(scene).FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error);
}
