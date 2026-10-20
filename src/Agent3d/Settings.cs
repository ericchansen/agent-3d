using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Agent3d;

public sealed record Settings(string Provider, string Model, Uri Endpoint, int ContextTokens,
    int OutputTokens, string DataRoot, bool Hosted = false)
{
    public static Settings Load(IConfiguration configuration)
    {
        string Required(string name) => configuration[name]
            ?? throw new InvalidOperationException($"{name} must be configured.");
        var hosted = configuration["HOST_MODE"] == "foundry";
        var settings = new Settings(Required("MODEL_PROVIDER"), Required("MODEL_NAME"),
            new Uri(Required("MODEL_ENDPOINT")), int.Parse(Required("MODEL_CONTEXT_TOKENS")),
            int.Parse(Required("MODEL_OUTPUT_TOKENS")),
            Path.GetFullPath(configuration["DATA_ROOT"] ?? (hosted
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agent-3d") : "data")),
            hosted);
        if (settings.Provider is not ("local" or "openai" or "foundry") ||
            settings.ContextTokens <= 0 || settings.OutputTokens <= 0 || settings.OutputTokens >= settings.ContextTokens ||
            settings.Endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(settings.Endpoint.UserInfo))
            throw new InvalidOperationException("Invalid provider, endpoint or model token limits.");
        if (settings.Provider == "local" && !IsLoopback(settings.Endpoint.Host))
            throw new InvalidOperationException("The local model endpoint must use loopback.");
        if (settings.Provider != "local" && settings.Endpoint.Scheme != "https")
            throw new InvalidOperationException("Remote model endpoints require HTTPS.");
        if (settings.Provider == "local")
            foreach (var name in new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
                "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" })
                if (configuration[name] is { Length: > 0 } target &&
                    (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !IsLoopback(uri.Host)))
                    throw new InvalidOperationException("Local mode requires loopback telemetry endpoints.");
        return settings;
    }

    public IChatClient CreateClient(IConfiguration configuration)
    {
        var options = new OpenAIClientOptions { Endpoint = Endpoint };
        var client = Provider switch
        {
            "local" => new OpenAIClient(new ApiKeyCredential(configuration["MODEL_API_KEY"] ?? "local"), options),
            "openai" => new OpenAIClient(new ApiKeyCredential(configuration["MODEL_API_KEY"]
                ?? throw new InvalidOperationException("MODEL_API_KEY must be configured.")), options),
#pragma warning disable OPENAI001 // The official Entra bearer-token transport is preview.
            "foundry" => new OpenAIClient(new BearerTokenPolicy(
                Hosted ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned) :
                    new AzureCliCredential(new AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(60) }),
                "https://ai.azure.com/.default"), options),
#pragma warning restore OPENAI001
            _ => throw new InvalidOperationException("Unknown provider.")
        };
        return client.GetChatClient(Model).AsIChatClient();
    }

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
