# Foundry Hosted Agent

The application includes an official `Azure.AI.AgentServer.Invocations` entrypoint. [FoundryInvocationHandler](../src/Agent3d/FoundryInvocationHandler.cs) dispatches the existing native AG-UI endpoint delegate. It does not parse SSE, implement an agent loop, or expose arbitrary application routes through the Foundry service.

**Deployment is not qualified by local tests.** The native Invocations adapter has also been exercised against a real hosted deployment: authenticated hydration, model reads, uncommitted previews, one approved edit, replay and rejection. Exact scene and receipt comparisons confirmed no unapproved or repeated mutation. Stopping and resuming that sandbox preserved its scene and receipt, reported `SESSION_RESET`, and rejected its pre-restart pending approval. The model offered a new gated proposal after a rejection; the application instructions now explicitly tell it to stop and wait. Every deployment still needs its own identity, authenticated UI and lifecycle verification.

## Runtime boundary

Set `HOST_MODE=foundry` only inside the authenticated Foundry runtime. This mode listens on port 8088, exposes `/readiness` and `/invocations`, and rejects direct `/agent` requests. The handler requires the official SDK's platform `UserIdKey` and `CallId`; native session storage is partitioned by that caller key. These values are trusted only behind Foundry's authenticated, header-overwriting ingress. Do not run this mode as an anonymous public container or let a browser set platform identity headers.

Model access uses `ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)` to acquire the platform-provided agent identity, not a developer login. Confirm that the deployment supplies this supported identity and grants it the required model access before treating the hosted profile as operational. The UI host must authenticate users and preserve the platform's caller isolation, not forward an arbitrary caller-chosen identity.

Use `https://<resource>.services.ai.azure.com/openai/v1/` for this application's Foundry transport, which requests the `https://ai.azure.com/.default` scope. Match the endpoint and audience in local and hosted configuration; changing the hostname is not necessarily an equivalent configuration.

For direct account-level OpenAI inference, the hosted identity needs [Cognitive Services OpenAI User](https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/ai-machine-learning#cognitive-services-openai-user) on the model account. Default project permissions alone do not necessarily grant account-level chat inference. Identify the principal attached to the deployed agent version, then have an authorized administrator grant only the required scope:

```powershell
az role assignment create --assignee-object-id "<hosted-agent-principal-id>" --assignee-principal-type ServicePrincipal --role "Cognitive Services OpenAI User" --scope "<model-account-resource-id>"
```

Allow normal role propagation before retrying. An inference `PermissionDenied` after successful token acquisition is an authorization problem; do not switch to keys or broaden the credential chain to conceal it.

The [platform persists `$HOME` for each sandbox session](https://learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions). Hosted `DATA_ROOT` defaults to `agent-3d` under the user's home directory. Return the platform's `agent_session_id` on subsequent Invocations calls to reach that sandbox. It is routing state, not authorization. Invocations does not supply conversation persistence; the application uses the native session-store extension and its atomic snapshot.

After process restart, the scene and receipt are recovered but conversation history and pending approvals are intentionally reset. Browser/runtime cached approvals can then fail closed; retrieve fresh state and respect `SESSION_RESET`.

## Programmatic deployment recipe

Use the [official hosted deployment guide](https://learn.microsoft.com/azure/foundry/how-to/develop/framework-hosted-agents) and the [container sample](https://github.com/microsoft/agent-framework/tree/dotnet-1.25.0/dotnet/samples/04-hosting/FoundryHostedAgents/responses/Hosted-ChatClientAgent-Dockerfile) for the Azure Developer CLI lifecycle. This repository supplies [azure.yaml](../azure.yaml), a [Dockerfile](../Dockerfile) and upload exclusions, not an infrastructure automation pipeline.

Before proceeding, approve the subscription, existing Foundry project, region, model deployment, identity permissions and spending. Docker must be running for the configured local image build. Do not run provisioning commands against an unapproved target.

From the repository root:

```powershell
$manifest = (Resolve-Path .\azure.yaml).Path
azd extension install azure.ai.agents
azd auth login
```

Change to a new empty deployment work directory, then initialize from the manifest:

```powershell
azd ai agent init -m $manifest
```

Select the approved existing project. In the generated `agent-3d` project, set the model settings using that target's actual supported endpoint, deployment and token limits:

```powershell
azd env set MODEL_ENDPOINT "<approved-openai-endpoint>"
azd env set MODEL_NAME "<approved-model-deployment>"
azd env set MODEL_CONTEXT_TOKENS "<context-capacity>"
azd env set MODEL_OUTPUT_TOKENS "<output-cap>"
azd provision
azd deploy
```

Provisioning and deployment can incur charges. The manifest advertises Invocations protocol 2.0.0. The request body remains native AG-UI JSON; do not substitute a Responses API body or a plain-text chat payload.

## Acceptance after deployment

Verify authenticated read, exact edit preview, approve, reject and replay against the real managed endpoint. Confirm owner isolation, scene recovery across the supported idle/restart lifecycle, and actual native traces in the chosen telemetry destination. Keep the connected deployment independent of the local/offline profile.

For Foundry/Azure Monitor telemetry, configure a supported OTLP collection destination or the official hosting observability integration for your environment. This example does not silently enable cloud export or assume an Application Insights connection string automatically installs an exporter.
