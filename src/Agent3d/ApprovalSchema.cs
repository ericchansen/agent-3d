using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http.Json;

namespace Agent3d;

internal static class ApprovalSchema
{
    private static readonly JsonElement Schema = CreateSchema();

    public static void Configure(JsonOptions options)
    {
        // AGUI.Server 1.0.1 requires toolCall but omits it from the advertised approval schema.
        options.SerializerOptions.TypeInfoResolver = options.SerializerOptions.TypeInfoResolver!.WithAddedModifier(info =>
        {
            if (info.Type != typeof(AGUIInterrupt)) return;
            var property = info.Properties.Single(p => p.Name == "responseSchema");
            var original = property.Get!;
            property.Get = value => value is AGUIInterrupt { Reason: "tool_call" } ? Schema : original(value);
        });
    }

    private static JsonElement CreateSchema()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        var schema = options.GetJsonSchemaAsNode(typeof(AGUIToolApprovalResumePayload));
        schema["type"] = "object";
        schema["required"] = new JsonArray("approved", "toolCall");
        schema["properties"]!["toolCall"]!["type"] = "object";
        return JsonSerializer.SerializeToElement(schema);
    }
}
