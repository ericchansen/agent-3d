using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent3d;

public sealed class SceneContextProvider(SnapshotSessionStore store, ProviderInfo provider,
    ILoggerFactory loggerFactory) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => ["scene"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var session = context.Session ?? throw new InvalidOperationException("A scene requires a session.");
        var state = SnapshotSessionStore.SceneState.GetOrInitializeState(session);
        var tools = new SceneTools(context.Agent, session, store, provider, loggerFactory.CreateLogger<SceneTools>());
        return ValueTask.FromResult(new AIContext
        {
            Instructions = $"The scene revision is {state.Scene.Revision}. Selected ids: " +
                $"{JsonSerializer.Serialize(state.Scene.SelectedIds)}. " +
                "Use the scene tools for authoritative values. Only color and absolute position edits are supported. " +
                "An edit requires explicit native approval. Never infer permission from messages or client state. " +
                "Rejection is a user decision, not a tool failure to work around; acknowledge it and wait for a new user request.",
            Tools =
            [
                AIFunctionFactory.Create(tools.ListObjects, "list_objects", serializerOptions: Scene.Json),
                AIFunctionFactory.Create(tools.GetObjects, "get_objects", serializerOptions: Scene.Json),
                AIFunctionFactory.Create(tools.HighlightObjects, "highlight_objects", serializerOptions: Scene.Json),
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                    tools.EditObjects, "edit_objects", serializerOptions: Scene.Json))
            ]
        });
    }
}

public sealed class SceneTools(AIAgent agent, AgentSession session, SnapshotSessionStore store,
    ProviderInfo provider, ILogger<SceneTools> logger)
{
    public static readonly ActivitySource ActivitySource = new("Agent3d.Scene");
    public static readonly Meter Meter = new("Agent3d.Scene");
    private static readonly Counter<long> Edits = Meter.CreateCounter<long>("scene.edits");

    [Description("List actual scene names, colors, tags and positions, optionally limited to one shape. Read these before choosing ids.")]
    public SceneQueryResult ListObjects(SceneShape? shape = null)
    {
        var state = Current();
        try
        {
            if (shape.HasValue && !Enum.IsDefined(shape.Value))
                throw new SceneException("INVALID_INPUT", "Unknown shape.");
            return new(state.Scene.Revision, state.Scene.Objects.Where(o => shape is null ||
                o.Shape == shape.Value.ToString().ToLowerInvariant()).ToArray());
        }
        catch (SceneException ex) { return new(state.Scene.Revision, [], Error(ex).Error); }
    }

    [Description("Read exact properties of 1-64 distinct existing object ids.")]
    public SceneQueryResult GetObjects(string[] ids)
    {
        var state = Current();
        try
        {
            Scene.ValidateIds(state.Scene, ids, 1, 64);
            return new(state.Scene.Revision, state.Scene.Objects.Where(o => ids.Contains(o.Id)).ToArray());
        }
        catch (SceneException ex) { return new(state.Scene.Revision, [], Error(ex).Error); }
    }

    [Description("Replace reversible scene highlights with 0-64 existing ids. Does not change materials or geometry.")]
    public async Task<AgentState> HighlightObjects(string[] ids, CancellationToken cancellationToken)
    {
        try
        {
            var state = Current();
            Scene.ValidateIds(state.Scene, ids, 0, 64);
            return await SaveAsync(state with
            {
                Scene = state.Scene with { HighlightedIds = ids.ToArray() },
                Error = null
            }, cancellationToken);
        }
        catch (SceneException ex) { return Error(ex); }
    }

    [Description("Change color and/or absolute position of 1-16 objects at expectedRevision. Native approval is required.")]
    public async Task<AgentState> EditObjects(string[] ids, long expectedRevision, SceneEdit edit,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("scene.edit");
        try
        {
            var invocation = FunctionInvokingChatClient.CurrentContext
                ?? throw new InvalidOperationException("Scene edits require native tool invocation.");
            if (invocation.Arguments.Keys.Any(k => k is not ("ids" or "expectedRevision" or "edit")))
                throw new SceneException("INVALID_INPUT", "Unknown edit argument.");
            var operationId = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(invocation.CallContent.CallId))).ToLowerInvariant();
            var changed = Scene.Edit(Current(), ids, expectedRevision, edit, operationId);
            var result = await SaveAsync(changed, cancellationToken);
            Edits.Add(1, new KeyValuePair<string, object?>("outcome", "committed"));
            return result;
        }
        catch (SceneException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Code);
            Edits.Add(1, new KeyValuePair<string, object?>("outcome", ex.Code));
            return Error(ex);
        }
    }

    private AgentState Current() => SnapshotSessionStore.SceneState.GetOrInitializeState(session) with
    {
        Provider = provider,
        TraceId = Activity.Current?.TraceId.ToHexString()
    };

    private AgentState Error(SceneException error)
    {
        logger.LogWarning("Scene operation failed: {Code}", error.Code);
        return Current() with
        {
            Error = new(error.Code, error.Message, error.Code is "STALE_REVISION" or "SAVE_FAILED",
                Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N"))
        };
    }

    private async Task<AgentState> SaveAsync(AgentState next, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("scene.save");
        cancellationToken.ThrowIfCancellationRequested();
        var previous = SnapshotSessionStore.SceneState.GetOrInitializeState(session);
        SnapshotSessionStore.SceneState.SaveState(session, next);
        try
        {
            await store.SaveCurrentAsync(agent, session, cancellationToken);
            return next;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            SnapshotSessionStore.SceneState.SaveState(session, previous);
            if (ex is OperationCanceledException) throw;
            logger.LogError(ex, "Could not atomically save the scene.");
            throw new SceneException("SAVE_FAILED", "The scene could not be saved. No edit was committed.");
        }
    }
}
