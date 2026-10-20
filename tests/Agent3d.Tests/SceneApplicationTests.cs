using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent3d;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent3d.Tests;

public sealed class SceneApplicationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "agent3d-test-" + Guid.NewGuid().ToString("N"));

    private WebApplication CreateApp(IChatClient client) => SceneApplication.Build([], client,
        new Settings("local", "scripted", new Uri("http://127.0.0.1:11434/v1"), 16384, 2048, _dataRoot),
        b => { b.WebHost.UseTestServer(); b.Logging.ClearProviders(); });

    [Fact]
    public async Task BundledSkillsUseNativeDiscoveryAndApproval()
    {
        using var model = new ScriptedChatClient(
            (_, options) =>
            {
                Assert.Contains(options!.Tools!, t => t.Name == "load_skill");
                Assert.DoesNotContain(options.Tools!, t => t is HostedWebSearchTool);
                Assert.Contains("inspect-scene", options.Instructions);
                Assert.Contains("edit-scene", options.Instructions);
                return ScriptedChatClient.Call("load_skill", new() { ["skillName"] = "edit-scene" });
            },
            (messages, _) =>
            {
                Assert.Contains("edit_objects", JsonSerializer.Serialize(messages.SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>().Last().Result));
                return ScriptedChatClient.Text("Skill loaded.");
            });
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var first = await Post(http, "/agent", Input("Load the edit-scene skill."));
        var resumed = await Post(http, "/agent", Resume(first, true));
        Assert.DoesNotContain(resumed, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Contains(resumed, e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT" &&
            e.GetProperty("delta").GetString() == "Skill loaded.");
    }

    [Theory]
    [InlineData("/agent")]
    [InlineData("/invocations")]
    public async Task HydrationDoesNotCallModelOrTrustClientScene(string path)
    {
        using var client = new ScriptedChatClient();
        await using var app = CreateApp(client);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var request = Input();
        request["state"] = JsonSerializer.SerializeToNode(new
        {
            scene = new { revision = 100, objects = Array.Empty<object>() },
            selectedIds = new[] { "box-01" }
        });
        var events = await Post(http, path, request);
        var state = Snapshot(events);
        Assert.Equal(0, state.Scene.Revision);
        Assert.Equal(12, state.Scene.Objects.Length);
        Assert.Equal(["box-01"], state.Scene.SelectedIds);
        Assert.Equal(JsonSerializer.Serialize(state.Scene),
            JsonSerializer.Serialize(Snapshot(await Post(http, path, Input())).Scene));
    }

    [Theory]
    [InlineData("/agent", true)]
    [InlineData("/agent", false)]
    [InlineData("/invocations", true)]
    public async Task NativeApprovalCommitsOnceAndRestartKeepsScene(string path, bool approved)
    {
        using var model = new ScriptedChatClient(
            (_, _) => EditCall(0),
            (_, _) => ScriptedChatClient.Text("Edit handled."),
            (_, _) => ScriptedChatClient.Text("The previous decision was already handled."));
        await using (var app = CreateApp(model))
        {
            await app.StartAsync();
            using var http = app.GetTestClient();
            var first = await Post(http, path, Input("Move box-01 up two units."));
            var schema = first.Single(e => e.GetProperty("type").GetString() == "RUN_FINISHED")
                .GetProperty("outcome").GetProperty("interrupts")[0].GetProperty("responseSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.GetProperty("properties").TryGetProperty("toolCall", out _));
            Assert.Contains(schema.GetProperty("required").EnumerateArray(), p => p.GetString() == "toolCall");
            var resume = Resume(first, approved);
            Assert.Equal(0, Snapshot(await Post(http, path, Input())).Scene.Revision);
            var result = await Post(http, path, resume);
            Assert.DoesNotContain(result, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            var current = Snapshot(await Post(http, path, Input()));
            Assert.Equal(approved ? 1 : 0, current.Scene.Revision);
            Assert.Equal(approved ? 3 : 1, current.Scene.Objects.Single(o => o.Id == "box-01").Position.Y);
            if (approved)
            {
                Assert.Equal(1, Snapshot(result).Receipt!.NewRevision);
                Assert.Equal(["box-01"], current.Receipt!.AffectedIds);
            }
            var replay = await Post(http, path, resume);
            Assert.Contains(replay, e => e.GetProperty("type").GetString() == "RUN_ERROR");
            Assert.Equal(2, model.Requests);
            Assert.Equal(current.Scene.Revision, Snapshot(await Post(http, path, Input())).Scene.Revision);
        }
        using var noModel = new ScriptedChatClient();
        await using var restarted = CreateApp(noModel);
        await restarted.StartAsync();
        using var recoveredHttp = restarted.GetTestClient();
        var recovered = Snapshot(await Post(recoveredHttp, path, Input()));
        Assert.Equal(approved ? 1 : 0, recovered.Scene.Revision);
        Assert.Equal("SESSION_RESET", recovered.Error!.Code);
    }

    [Fact]
    public async Task PendingApprovalIsInvalidatedAfterProcessRestart()
    {
        JsonObject decision;
        using var model = new ScriptedChatClient((_, _) => EditCall(0));
        await using (var app = CreateApp(model))
        {
            await app.StartAsync();
            using var http = app.GetTestClient();
            decision = Resume(await Post(http, "/agent", Input("Move the box.")), true);
        }
        using var noModel = new ScriptedChatClient();
        await using var restarted = CreateApp(noModel);
        await restarted.StartAsync();
        using var recoveredHttp = restarted.GetTestClient();
        var recovered = Snapshot(await Post(recoveredHttp, "/agent", Input()));
        Assert.Equal("SESSION_RESET", recovered.Error!.Code);
        var replay = await Post(recoveredHttp, "/agent", decision);
        Assert.Contains(replay, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal(0, noModel.Requests);
        var unchanged = Snapshot(await Post(recoveredHttp, "/agent", Input()));
        Assert.Equal(0, unchanged.Scene.Revision);
        Assert.Equal(1, unchanged.Scene.Objects.Single(o => o.Id == "box-01").Position.Y);
        Assert.Null(unchanged.Receipt);
    }

    [Fact]
    public async Task FiltersAndHighlightsUseExactFixtureIdsWithoutEditing()
    {
        using var model = new ScriptedChatClient(
            (_, _) => ScriptedChatClient.Call("list_objects", new() { ["shape"] = "cylinder" }),
            (messages, _) =>
            {
                var value = Assert.IsType<JsonElement>(messages.SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>().Last().Result);
                Assert.Equal(4, value.GetProperty("matches").GetArrayLength());
                var matches = value.GetProperty("matches").EnumerateArray()
                    .Where(o => o.GetProperty("color").GetString() == "#2563EB")
                    .Select(o => o.GetProperty("id").GetString()!).ToArray();
                Assert.Equal(["cylinder-01", "cylinder-02"], matches);
                return ScriptedChatClient.Call("highlight_objects", new() { ["ids"] = matches });
            },
            (_, _) => ScriptedChatClient.Text("Highlighted."));
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var result = await Post(http, "/agent", Input("Highlight blue cylinders."));
        Assert.DoesNotContain(result, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        var state = Snapshot(await Post(http, "/agent", Input()));
        Assert.Equal(["cylinder-01", "cylinder-02"], state.Scene.HighlightedIds);
        Assert.Equal(0, state.Scene.Revision);
        Assert.All(state.Scene.Objects.Where(o => state.Scene.HighlightedIds.Contains(o.Id)),
            o => Assert.Equal("#2563EB", o.Color));
    }

    [Fact]
    public async Task ForgedApprovalCannotAccessAnotherScene()
    {
        using var model = new ScriptedChatClient(
            (_, _) => EditCall(0),
            (_, _) => ScriptedChatClient.Text("There is no approved operation in this scene."));
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var first = await Post(http, "/agent", Input("Move the box."));
        var incomplete = Resume(first, true);
        incomplete["resume"]![0]!["payload"]!.AsObject().Remove("toolCall");
        using var invalid = await http.PostAsJsonAsync("/agent", incomplete);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var forged = Resume(first, true);
        forged["threadId"] = "other-scene";
        var result = await Post(http, "/agent", forged);
        Assert.Contains(result, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal(1, model.Requests);
        var observed = Snapshot(await Post(http, "/agent", Input()));
        Assert.Equal(1, observed.Scene.Objects.Single(o => o.Id == "box-01").Position.Y);
        Assert.Equal(0, observed.Scene.Revision);
        var replacement = Input();
        replacement["tools"] = JsonNode.Parse("""[{"name":"edit_objects","description":"override","parameters":{}}]""");
        using var rejected = await http.PostAsJsonAsync("/agent", replacement);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task NativeConsentUsesOriginalArgumentsNotClientSubstitutions()
    {
        using var model = new ScriptedChatClient(
            (_, _) => EditCall(0),
            (_, _) => ScriptedChatClient.Text("Original approved edit applied."),
            (_, _) => ScriptedChatClient.Text("The previous decision was already handled."));
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var first = await Post(http, "/agent", Input("Move the box up two units."));
        var tampered = Resume(first, true);
        tampered["resume"]![0]!["payload"]!["toolCall"]!["arguments"]!["edit"]!["position"]!["y"] = 999;
        var result = Snapshot(await Post(http, "/agent", tampered));
        Assert.Equal(3, result.Scene.Objects.Single(o => o.Id == "box-01").Position.Y);
        Assert.Equal(1, result.Scene.Revision);
        Assert.Equal(["box-01"], result.Receipt!.AffectedIds);
        var replay = await Post(http, "/agent", tampered);
        Assert.Contains(replay, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Equal(2, model.Requests);
        var recovered = Snapshot(await Post(http, "/agent", Input()));
        Assert.Equal(1, recovered.Scene.Revision);
        Assert.Equal(result.Receipt.OperationId, recovered.Receipt!.OperationId);
    }

    [Fact]
    public async Task ModelFailureAfterCommitDoesNotLoseOrRepeatTheEdit()
    {
        using var model = new ScriptedChatClient(
            (_, _) => EditCall(0),
            (_, _) => throw new IOException("Synthetic model stream failure."),
            (_, _) => ScriptedChatClient.Text("The saved edit is already complete."));
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var first = await Post(http, "/agent", Input("Move the box."));
        var decision = Resume(first, true);
        var failed = await Post(http, "/agent", decision);
        Assert.Contains(failed, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        var committed = Snapshot(await Post(http, "/agent", Input()));
        Assert.Equal(1, committed.Scene.Revision);
        Assert.Equal(3, committed.Scene.Objects.Single(o => o.Id == "box-01").Position.Y);
        Assert.Equal("committed", committed.Receipt!.Outcome);
        await Post(http, "/agent", decision);
        var recovered = Snapshot(await Post(http, "/agent", Input()));
        Assert.Equal(1, recovered.Scene.Revision);
        Assert.Equal(committed.Receipt.OperationId, recovered.Receipt!.OperationId);
    }

    [Fact]
    public async Task MalformedNativeInputsAreRejectedBeforeRunningTheAgent()
    {
        using var model = new ScriptedChatClient();
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var empty = await http.PostAsync("/agent", JsonContent.Create<object?>(null));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var missingMessages = Input();
        missingMessages["messages"] = null;
        using var invalidMessages = await http.PostAsJsonAsync("/agent", missingMessages);
        Assert.Equal(HttpStatusCode.BadRequest, invalidMessages.StatusCode);
        var invalidResume = Input();
        invalidResume["resume"] = new JsonArray((JsonNode?)null);
        using var invalidDecision = await http.PostAsJsonAsync("/agent", invalidResume);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDecision.StatusCode);
        Assert.Equal(0, model.Requests);
    }

    [Fact]
    public async Task ApprovalDoesNotBypassExpectedRevision()
    {
        using var model = new ScriptedChatClient(
            (_, _) => EditCall(1),
            (_, _) => ScriptedChatClient.Text("The scene revision did not match."));
        await using var app = CreateApp(model);
        await app.StartAsync();
        using var http = app.GetTestClient();
        var first = await Post(http, "/agent", Input("Move the box."));
        var result = Snapshot(await Post(http, "/agent", Resume(first, true)));
        Assert.Equal("STALE_REVISION", result.Error!.Code);
        Assert.Equal(0, result.Scene.Revision);
        Assert.Null(result.Receipt);
        Assert.Equal(0, Snapshot(await Post(http, "/agent", Input())).Scene.Revision);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void EditEnforcesExactBatchBoundary(int count, bool accepted)
    {
        var seed = Scene.ReadSeed();
        var scene = seed with
        {
            Objects = Enumerable.Range(1, 20).Select(i => seed.Objects[0] with { Id = $"object-{i}" }).ToArray()
        };
        Scene.Validate(scene);
        var state = new AgentState(scene);
        var ids = scene.Objects.Take(count).Select(o => o.Id).ToArray();
        if (accepted)
        {
            var changed = Scene.Edit(state, ids, 0, new("#FFFFFF"), "operation");
            Assert.Equal(16, changed.Scene.Objects.Count(o => o.Color == "#FFFFFF"));
            Assert.Equal(1, changed.Scene.Revision);
        }
        else
            Assert.Throws<SceneException>(() => Scene.Edit(state, ids, 0, new("#FFFFFF"), "operation"));
    }

    [Fact]
    public void EditRejectsStaleUnknownDuplicateAndNonFiniteValues()
    {
        var state = new AgentState(Scene.ReadSeed());
        Assert.Equal("STALE_REVISION", Assert.Throws<SceneException>(() =>
            Scene.Edit(state, ["box-01"], 1, new("#FFFFFF"), "operation")).Code);
        Assert.Equal("NOT_FOUND", Assert.Throws<SceneException>(() =>
            Scene.Edit(state, ["missing"], 0, new("#FFFFFF"), "operation")).Code);
        Assert.Throws<SceneException>(() => Scene.Edit(state, ["box-01", "box-01"], 0, new("#FFFFFF"), "operation"));
        Assert.Throws<SceneException>(() => Scene.Edit(state, ["box-01"], 0, new(Position: new(double.NaN, 1, 1)), "operation"));
        Assert.Throws<SceneException>(() => Scene.Edit(state, ["box-01"], 0, new(), "operation"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SceneEdit>("""{"scale":3}""", Scene.Json));
        var unchanged = JsonSerializer.Deserialize<SceneEdit>("""{"color":null,"position":null}""", Scene.Json)!;
        Assert.Throws<SceneException>(() => Scene.Edit(state, ["box-01"], 0, unchanged, "operation"));
        var nullable = JsonSerializer.Deserialize<SceneEdit>(
            """{"color":null,"position":{"x":-4,"y":3,"z":-3}}""", Scene.Json)!;
        Assert.Equal("#F59E0B", Scene.Edit(state, ["box-01"], 0, nullable, "operation").Scene.Objects[0].Color);
        Assert.Equal(0, state.Scene.Revision);
    }

    private static ChatResponse EditCall(long revision) => ScriptedChatClient.Call("edit_objects", new()
    {
        ["ids"] = new[] { "box-01" },
        ["expectedRevision"] = revision,
        ["edit"] = new { position = new { x = -4, y = 3, z = -3 } }
    });

    private static JsonObject Input(string? text = null) => new()
    {
        ["threadId"] = "scene-1",
        ["runId"] = Guid.NewGuid().ToString("N"),
        ["messages"] = text is null ? new JsonArray() : new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["role"] = "user",
            ["content"] = text
        }),
        ["tools"] = new JsonArray(),
        ["context"] = new JsonArray(),
        ["state"] = new JsonObject()
    };

    private static JsonObject Resume(List<JsonElement> events, bool approved)
    {
        var interrupt = events.Single(e => e.GetProperty("type").GetString() == "RUN_FINISHED")
            .GetProperty("outcome").GetProperty("interrupts")[0];
        var args = events.Single(e => e.GetProperty("type").GetString() == "TOOL_CALL_ARGS").GetProperty("delta").GetString()!;
        var name = events.Single(e => e.GetProperty("type").GetString() == "TOOL_CALL_START").GetProperty("toolCallName").GetString();
        var request = Input();
        request["resume"] = new JsonArray(new JsonObject
        {
            ["interruptId"] = interrupt.GetProperty("id").GetString(),
            ["status"] = "resolved",
            ["payload"] = new JsonObject
            {
                ["approved"] = approved,
                ["toolCall"] = new JsonObject
                {
                    ["callId"] = interrupt.GetProperty("toolCallId").GetString(),
                    ["name"] = name,
                    ["arguments"] = JsonNode.Parse(args)
                }
            }
        });
        return request;
    }

    private static async Task<List<JsonElement>> Post(HttpClient http, string path, JsonObject body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        var events = new List<JsonElement>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync())
            events.Add(JsonSerializer.Deserialize<JsonElement>(item.Data));
        return events;
    }

    private static AgentState Snapshot(List<JsonElement> events) =>
        events.Last(e => e.GetProperty("type").GetString() == "STATE_SNAPSHOT")
            .GetProperty("snapshot").Deserialize<AgentState>(Scene.Json)!;

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }
}
