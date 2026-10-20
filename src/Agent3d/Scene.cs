using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace Agent3d;

public sealed record Vector3(double X, double Y, double Z);
public enum SceneShape { Box, Sphere, Cylinder }
public sealed record SceneObject(string Id, string Name, string Shape, Vector3 Position,
    Vector3 Dimensions, string Color, string[] Tags);
public sealed record SceneSnapshot(int SchemaVersion, long Revision, SceneObject[] Objects,
    string[] SelectedIds, string[] HighlightedIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SceneEdit(string? Color = null, Vector3? Position = null);
public sealed record OperationReceipt(string OperationId, string[] AffectedIds,
    long OldRevision, long NewRevision, string Outcome);
public sealed record ApplicationError(string Code, string Message, bool Retryable, string CorrelationId);
public sealed record ProviderInfo(string Name, string Model);
public sealed record SceneQueryResult(long Revision, SceneObject[] Matches, ApplicationError? Error = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record AgentState(SceneSnapshot Scene, OperationReceipt? Receipt = null,
    ApplicationError? Error = null, ProviderInfo? Provider = null, string? TraceId = null);

public sealed class SceneException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static partial class Scene
{
    public const long MaxRevision = 9007199254740991;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<SceneShape>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static SceneSnapshot ReadSeed()
    {
        var scene = JsonSerializer.Deserialize<SceneSnapshot>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "seed.scene.json")), Json)
            ?? throw new InvalidDataException("The seed scene is empty.");
        Validate(scene);
        return scene;
    }

    public static void Validate(SceneSnapshot scene)
    {
        Require(scene.SchemaVersion == 1 && scene.Revision is >= 0 and <= MaxRevision,
            "Invalid scene version or revision.");
        Require(scene.Objects is { Length: >= 1 and <= 64 }, "A scene must contain 1-64 objects.");
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scene.Objects)
        {
            Require(item is not null && ValidId(item.Id) && unique.Add(item.Id), "Invalid or duplicate object id.");
            Require(item.Name is { Length: >= 1 and <= 80 }, "Invalid object name.");
            Require(item.Shape is "box" or "sphere" or "cylinder", "Invalid shape.");
            ValidateVector(item.Position, -1000, 1000);
            ValidateVector(item.Dimensions, 0.1, 100);
            Require(item.Shape != "sphere" || item.Dimensions.X == item.Dimensions.Y &&
                item.Dimensions.X == item.Dimensions.Z, "Sphere dimensions must be equal.");
            Require(item.Shape != "cylinder" || item.Dimensions.X == item.Dimensions.Z,
                "Cylinder X and Z dimensions must be equal.");
            ValidateColor(item.Color);
            Require(item.Tags is { Length: <= 8 } && item.Tags.All(t => t is { Length: >= 1 and <= 32 }) &&
                item.Tags.Distinct(StringComparer.Ordinal).Count() == item.Tags.Length, "Invalid tags.");
        }
        ValidateIds(scene, scene.SelectedIds, 0, 64);
        ValidateIds(scene, scene.HighlightedIds, 0, 64);
    }

    public static AgentState Edit(AgentState state, string[] ids, long expectedRevision,
        SceneEdit edit, string operationId)
    {
        ValidateIds(state.Scene, ids, 1, 16);
        Require(expectedRevision is >= 0 and <= MaxRevision, "Invalid revision.");
        if (state.Scene.Revision != expectedRevision)
            throw new SceneException("STALE_REVISION", "The scene changed. Read it and propose a new edit.");
        Require(state.Scene.Revision < MaxRevision, "The scene revision limit has been reached.");
        Require(edit is not null && (edit.Color is not null || edit.Position is not null),
            "An edit needs a color or absolute position.");
        if (edit.Color is not null)
            ValidateColor(edit.Color);
        if (edit.Position is not null)
            ValidateVector(edit.Position, -1000, 1000);
        Require(ValidId(operationId), "Invalid invocation identity.");
        var affected = ids.ToHashSet(StringComparer.Ordinal);
        var next = state.Scene with
        {
            Revision = expectedRevision + 1,
            Objects = state.Scene.Objects.Select(o => affected.Contains(o.Id)
                ? o with { Color = edit.Color ?? o.Color, Position = edit.Position ?? o.Position }
                : o).ToArray()
        };
        return state with
        {
            Scene = next,
            Receipt = new(operationId, ids.ToArray(), expectedRevision, next.Revision, "committed"),
            Error = null
        };
    }

    public static void ValidateIds(SceneSnapshot scene, string[]? ids, int min, int max)
    {
        Require(ids is not null && ids.Length >= min && ids.Length <= max &&
            ids.All(ValidId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
            $"Supply {min}-{max} distinct valid object ids.");
        if (ids!.Any(id => !scene.Objects.Any(o => o.Id == id)))
            throw new SceneException("NOT_FOUND", "One or more object ids do not exist.");
    }

    public static bool ValidId(string? id) => id is { Length: >= 1 and <= 64 } && IdPattern().IsMatch(id);

    public static void ValidateColor(string? color) =>
        Require(color is not null && ColorPattern().IsMatch(color), "Color must be #RRGGBB.");

    private static void ValidateVector(Vector3? value, double min, double max) =>
        Require(value is not null && new[] { value.X, value.Y, value.Z }
            .All(n => double.IsFinite(n) && n >= min && n <= max), $"Coordinates must be finite and within [{min}, {max}].");

    private static void Require([DoesNotReturnIf(false)] bool valid, string message)
    {
        if (!valid) throw new SceneException("INVALID_INPUT", message);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();
}
