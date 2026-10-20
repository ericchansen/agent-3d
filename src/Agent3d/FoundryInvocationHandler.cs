using Azure.AI.AgentServer.Invocations;

namespace Agent3d;

public sealed class FoundryInvocationHandler(EndpointDataSource endpoints, Settings settings) : InvocationHandler
{
    public override async Task HandleAsync(HttpRequest request, HttpResponse response,
        InvocationContext context, CancellationToken cancellationToken)
    {
        if (settings.Hosted)
        {
            if (string.IsNullOrWhiteSpace(context.PlatformContext.UserIdKey) ||
                string.IsNullOrWhiteSpace(context.PlatformContext.CallId))
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            // Only use this mode behind Foundry's authenticated, header-overwriting ingress.
            request.HttpContext.Items["scene-owner"] = context.PlatformContext.UserIdKey;
        }
        var endpoint = endpoints.Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/agent");
        var previous = request.HttpContext.GetEndpoint();
        request.HttpContext.SetEndpoint(endpoint);
        try { await endpoint.RequestDelegate!(request.HttpContext); }
        finally { request.HttpContext.SetEndpoint(previous); }
    }
}
