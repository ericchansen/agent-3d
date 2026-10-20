using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Azure.AI.AgentServer.Invocations;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agent3d;

public static class SceneApplication
{
    public static WebApplication Build(string[] args, IChatClient? chatClient = null,
        Settings? settings = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        settings ??= Settings.Load(builder.Configuration);
        builder.WebHost.UseUrls(settings.Hosted ? "http://0.0.0.0:8088" : "http://127.0.0.1:5057");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
        builder.Services.AddSingleton(settings);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAGUIServer();
        builder.Services.PostConfigure<Microsoft.AspNetCore.Http.Json.JsonOptions>(ApprovalSchema.Configure);
        builder.Services.AddInvocationsServer();
        builder.Services.AddScoped<InvocationHandler, FoundryInvocationHandler>();
        builder.Services.AddSingleton(new SnapshotSessionStore(Path.Combine(settings.DataRoot, "sessions")));
        builder.Services.AddSingleton<AgentIsolationKeyProvider, RequestIsolation>();
#pragma warning disable MAAI001 // Native hosting/session APIs remain preview in the pinned release.
        builder.Services.AddKeyedSingleton<AgentSessionStore>("scene", (services, _) =>
            new IsolationKeyScopedAgentSessionStore(services.GetRequiredService<SnapshotSessionStore>(),
                services.GetRequiredService<AgentIsolationKeyProvider>(), new() { Strict = true }));
#pragma warning restore MAAI001
        builder.Services.AddSingleton(chatClient ?? settings.CreateClient(builder.Configuration));
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("agent-3d"))
                .WithTracing(t => t.AddAspNetCoreInstrumentation()
                    .AddSource("Agent3d.Harness", "Agent3d.Scene", AGUIServerInstrumentation.ActivitySourceName)
                    .AddOtlpExporter())
                .WithMetrics(m => m.AddMeter("Agent3d.Harness", "Microsoft.Extensions.AI", "Agent3d.Scene")
                    .AddOtlpExporter());
            builder.Logging.AddOpenTelemetry(l => l.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("agent-3d"))
                .AddOtlpExporter());
        }
        var app = builder.Build();
        var store = app.Services.GetRequiredService<SnapshotSessionStore>();
        var source = new AgentFileSkillsSource(Path.Combine(AppContext.BaseDirectory, "skills"));
        app.Lifetime.ApplicationStopped.Register(source.Dispose);
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var modelClient = app.Services.GetRequiredService<IChatClient>().AsBuilder().UseLogging(loggerFactory).Build();
        var agent = modelClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "scene",
            DisableWebSearch = true,
            AgentSkillsSource = source,
            OpenTelemetrySourceName = "Agent3d.Harness",
            ChatOptions = new ChatOptions
            {
                ModelId = settings.Model,
                MaxOutputTokens = settings.OutputTokens,
                AllowMultipleToolCalls = false,
                Instructions = "Help inspect and edit the synthetic scene. Use inspect-scene or edit-scene when relevant. " +
                    "Wait for read results before choosing object ids or a revision; do not guess ids."
            },
#pragma warning disable MAAI001 // Native file-memory location configuration is preview.
            FileMemoryStore = new FileSystemAgentFileStore(Path.Combine(settings.DataRoot, "memory")),
#pragma warning restore MAAI001
            AIContextProviders = [new SceneContextProvider(store, new(settings.Provider, settings.Model),
                app.Services.GetRequiredService<ILoggerFactory>())]
        }, loggerFactory: app.Services.GetRequiredService<ILoggerFactory>());

        app.Use(async (context, next) =>
        {
            if (settings.Hosted)
            {
                if (context.Request.Path != "/invocations" && context.Request.Path != "/readiness")
                { context.Response.StatusCode = 404; return; }
            }
            else
            {
                if (!Settings.IsLoopback(context.Request.Host.Host) ||
                    context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote) ||
                    context.Request.Headers.Origin is { Count: > 0 } origins &&
                    (!Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) || !Settings.IsLoopback(origin.Host)))
                { context.Response.StatusCode = 403; return; }
                context.Items["scene-owner"] = "loopback";
            }
            await next(context);
        });
        app.MapGet("/readiness", () => Results.Ok());
        app.MapInvocationsServer();
        var streamOptions = new AGUIStreamOptions();
        foreach (var tool in new[] { "list_objects", "get_objects", "highlight_objects", "edit_objects" })
            streamOptions.MapResult(tool, result =>
                result.Result is JsonElement { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("scene", out _)
                    ? [new StateSnapshotEvent { Snapshot = value }] : []);
        var gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
        app.Lifetime.ApplicationStopped.Register(() => { foreach (var gate in gates) gate.Dispose(); });
        app.MapAGUIServer("/agent", agent).WithMetadata(streamOptions)
            .AddEndpointFilter(async (context, next) =>
            {
                var input = context.GetArgument<RunAgentInput?>(0);
                var cancellation = context.HttpContext.RequestAborted;
                if (input is null || input.Messages is null ||
                    !Scene.ValidId(input.ThreadId) || !Scene.ValidId(input.RunId) ||
                    input.Tools is { Count: > 0 } || input.Context is { Count: > 0 })
                    return Results.BadRequest(new { error = "Invalid identifiers, client tools or client context." });
                if (input.Resume is { Count: > 0 } && input.Resume.Any(r =>
                    r is null || r.Payload is not { ValueKind: JsonValueKind.Object } payload ||
                    !payload.TryGetProperty("toolCall", out var call) || call.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("approved", out var consent) ||
                    consent.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                    return Results.BadRequest(new { error = "Native approval resumes require approved and the original toolCall." });
                var owner = await app.Services.GetRequiredService<AgentIsolationKeyProvider>().GetIsolationKeyAsync(cancellation);
                if (owner is null) return Results.Unauthorized();
                // Fixed stripes bound lock memory even when clients send many distinct thread ids.
                var gate = gates[SHA256.HashData(Encoding.UTF8.GetBytes(owner + "\n" + input.ThreadId))[0] % gates.Length];
                if (!await gate.WaitAsync(0, cancellation)) return Results.Conflict(new { error = "BUSY" });
                try
                {
#pragma warning disable MAAI001
                    var scopedStore = app.Services.GetRequiredKeyedService<AgentSessionStore>("scene");
                    var key = new AgentSessionStoreKey(input.ThreadId);
                    var session = await scopedStore.GetOrCreateSessionAsync(agent, key, cancellation);
#pragma warning restore MAAI001
                    var state = SnapshotSessionStore.SceneState.GetOrInitializeState(session);
                    if (input.State is { ValueKind: JsonValueKind.Object } clientState &&
                        clientState.TryGetProperty("selectedIds", out var selection))
                    {
                        var ids = selection.Deserialize<string[]>(Scene.Json);
                        Scene.ValidateIds(state.Scene, ids, 0, 64);
                        state = state with { Scene = state.Scene with { SelectedIds = ids! } };
                        SnapshotSessionStore.SceneState.SaveState(session, state);
                    }
#pragma warning disable MAAI001
                    await scopedStore.SaveSessionAsync(agent, key, session, cancellation);
#pragma warning restore MAAI001
                    input.State = null;
                    input.ForwardedProperties = null;
                    if (input.Messages.Count == 0 && input.Resume is not { Count: > 0 })
                    {
                        state = state with { Provider = new(settings.Provider, settings.Model) };
                        return new LockedResult(TypedResults.ServerSentEvents(Hydrate(input, state)), gate);
                    }
                    if (input.Resume is { Count: > 0 })
                        input.Messages = [];
                    else if (input.Messages.LastOrDefault() is AGUIUserMessage user)
                        input.Messages = [user];
                    else
                    {
                        gate.Release();
                        return Results.BadRequest(new { error = "Send a user message or native approval response." });
                    }
                    var result = await next(context);
                    return new LockedResult((IResult)result!, gate);
                }
                catch (SceneException ex)
                {
                    gate.Release();
                    return Results.BadRequest(new { error = ex.Code, message = ex.Message });
                }
                catch (JsonException)
                {
                    gate.Release();
                    return Results.BadRequest(new { error = "INVALID_INPUT", message = "Invalid selection hints." });
                }
                catch { gate.Release(); throw; }
            });
        return app;
    }

    private static async IAsyncEnumerable<BaseEvent> Hydrate(RunAgentInput input, AgentState state)
    {
        yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
        yield return new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(state, Scene.Json) };
        yield return new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId, Outcome = new RunFinishedSuccessOutcome() };
        await Task.CompletedTask;
    }

    private sealed class LockedResult(IResult result, SemaphoreSlim gate) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try { await result.ExecuteAsync(context); }
            finally { gate.Release(); }
        }
    }

    private sealed class RequestIsolation(IHttpContextAccessor accessor) : AgentIsolationKeyProvider
    {
        public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(accessor.HttpContext?.Items["scene-owner"] as string);
    }
}
