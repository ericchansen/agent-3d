using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Agent3d;

#pragma warning disable MAAI001 // Implements the native preview session-storage boundary.
public sealed class SnapshotSessionStore(string root) : AgentSessionStore
{
    public static readonly ProviderSessionState<AgentState> SceneState =
        new(_ => new(Scene.ReadSeed()), "scene", Scene.Json);
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly ConditionalWeakTable<AgentSession, Binding> _bindings = new();

    public override async ValueTask<AgentSession> GetOrCreateSessionAsync(AIAgent agent,
        AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(agent, key, cancellationToken);
        if (session is not null) return session;
        session = await agent.CreateSessionAsync(cancellationToken);
        _bindings.Add(session, new(key));
        SceneState.GetOrInitializeState(session);
        return session;
    }

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent,
        AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        await using var file = File.OpenRead(path);
        var saved = await JsonSerializer.DeserializeAsync<SavedSession>(file, Scene.Json, cancellationToken)
            ?? throw new InvalidDataException("The saved session is empty.");
        var session = await agent.DeserializeSessionAsync(saved.Session, cancellationToken: cancellationToken);
        var state = SceneState.GetOrInitializeState(session);
        Scene.Validate(state.Scene);
        if (saved.Epoch != _epoch)
        {
            // A new process never restores permission to execute a pending tool call.
            session = await agent.CreateSessionAsync(cancellationToken);
            SceneState.SaveState(session, state with
            {
                Error = new("SESSION_RESET", "The scene was recovered. Conversation and pending approvals were reset.",
                    false, Guid.NewGuid().ToString("N"))
            });
        }
        _bindings.Add(session, new(key));
        return session;
    }

    public ValueTask SaveCurrentAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken) =>
        SaveSessionAsync(agent, _bindings.GetValue(session,
            _ => throw new InvalidOperationException("The session is not bound to server-owned storage.")).Key,
            session, cancellationToken);

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        AgentSession session, CancellationToken cancellationToken = default)
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        var path = GetPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, new SavedSession(_epoch, serialized), Scene.Json, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string GetPath(AgentSessionStoreKey key)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            key.SessionId,
            Partitions = key.Partitions?.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
        });
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(identity)) + ".json");
    }

    private sealed record Binding(AgentSessionStoreKey Key);
    private sealed record SavedSession(Guid Epoch, JsonElement Session);
}
#pragma warning restore MAAI001
