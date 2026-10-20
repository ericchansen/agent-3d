# Agent 3D

A small C# example for inspecting synthetic boxes, spheres and cylinders, then approving bounded color or position changes. The application uses the packaged [Microsoft Agent Framework Harness](https://github.com/microsoft/agent-framework/blob/dotnet-1.25.0/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs), native skills, typed tools and AG-UI. It does not implement a model loop, planner or approval registry.

The server owns the scene. Browser state cannot replace it or authorize edits. Original application code is MIT licensed; see [third-party notices](THIRD_PARTY_NOTICES.md).

## Run locally

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Start an OpenAI-compatible, tool-capable model server separately. This repository does not install a model server or download weights.

From the repository root, in PowerShell:

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:MODEL_PROVIDER = "local"
$env:MODEL_ENDPOINT = "http://127.0.0.1:11434/v1/"
$env:MODEL_NAME = "<your-server-model-name>"
$env:MODEL_CONTEXT_TOKENS = "16384"
$env:MODEL_OUTPUT_TOKENS = "1024"
dotnet restore --locked-mode
dotnet run --project .\src\Agent3d --no-restore
```

Use the actual model name and supported token limits. The local server listens on `http://127.0.0.1:5057`. Connect the separately runnable [Agent 3D UI](https://github.com/ericchansen/agent-3d-ui) to `http://127.0.0.1:5057/agent`.

An empty native AG-UI request reads the scene without invoking the model:

```powershell
$body = @{
    threadId = "demo"
    runId = "initial-state"
    messages = @()
    tools = @()
    context = @()
    state = @{}
} | ConvertTo-Json
Invoke-WebRequest http://127.0.0.1:5057/agent -Method Post -ContentType application/json -Body $body
```

The response contains native `RUN_STARTED`, `STATE_SNAPSHOT` and `RUN_FINISHED` events. The initial scene has revision 0 and 12 objects. Its source is the [pinned fixture](contracts/fixtures/seed.scene.json).

Try these prompts in the UI:

- "Find the blue cylinders and highlight them." Expected highlights: `cylinder-01`, `cylinder-02`; colors and revision remain unchanged.
- "Move box-01 up two units." Approve the exact proposed coordinates. Expected result: position `(-4, 3, -3)` and revision 1.
- Reject the next edit. The scene must remain unchanged.

The Harness retains its packaged modes, todos, memory, skill approvals and telemetry. Web search is explicitly disabled. File access, shell execution and background agents are not opted in.

## Tools and contract

| Tool | Input | Effect |
|---|---|---|
| `list_objects` | Optional shape enum | Read actual names, colors, tags and coordinates; do not guess color codes |
| `get_objects` | Existing ids | Read exact properties |
| `highlight_objects` | Existing ids, or an empty list | Replace reversible highlights |
| `edit_objects` | `ids`, `expectedRevision`, `edit` | Native approval, validation, atomic save, one revision increment |

An edit contains `color`, absolute `position`, or both. An absent or null field means unchanged; at least one non-null field is required. Dimensions, geometry, ids and tags are not editable. See [contract invariants](contracts/README.md) and [schema](contracts/application.schema.json).

Native consent is bound to the original server-recorded invocation. If a client substitutes arguments in an otherwise valid approval, the SDK uses the original arguments, not the substitution. A later model failure does not roll back an already saved edit; its receipt and revision still prevent a second mutation.

The schema snapshot comes from `ericchansen/agent-3d-ui`, SHA-256 `5CF50D89D10BD2EE1E5EA05AE0BAB552FB5F8C569548641BE1E96ACEB3185D38`. It is copied locally for offline use; there is no runtime GitHub dependency.

Send selection hints in `state.selectedIds`. Other client state is ignored. Client tools and extra client context are rejected. Native session history is server-owned: a normal request submits the newest user message; an approval request submits the native resume decision. Transcript history must not be used to grant approval.

## Configuration

| Variable | Meaning |
|---|---|
| `MODEL_PROVIDER` | Required: `local`, `openai` or `foundry`; no fallback |
| `MODEL_ENDPOINT` | Required OpenAI-compatible base URL, including `/v1/` where required |
| `MODEL_NAME` | Required model/deployment name |
| `MODEL_CONTEXT_TOKENS` | Required declared model capacity; see compaction limitation below |
| `MODEL_OUTPUT_TOKENS` | Required output cap, smaller than context capacity |
| `MODEL_API_KEY` | Required for `openai`; optional for a local server |
| `DATA_ROOT` | Server-owned snapshot/memory directory; local default is `data` under the process working directory |
| `HOST_MODE` | Leave unset locally; `foundry` enables the protected Invocations hosting boundary |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional exporter destination; unset means no application exporter |

`local` requires loopback model and telemetry endpoints and never constructs an Azure credential. `openai` uses the configured API key and requires HTTPS. `foundry` uses explicit `AzureCliCredential` locally and system-assigned `ManagedIdentityCredential` when hosted; it does not probe a credential chain. The credential and model client are reused for the application's lifetime. Local CLI authentication has a 60-second process timeout.

For connected development, sign in with `az login`, set `MODEL_PROVIDER=foundry`, and set `MODEL_ENDPOINT` to `https://<resource>.services.ai.azure.com/openai/v1/` with your deployed model name. This transport requests the `https://ai.azure.com/.default` scope; do not substitute an endpoint that expects a different token audience. Keep real endpoints, keys and account identifiers out of source control. Changing provider/model requires a new process and resets the conversation on recovery.

## Persistence, cancellation and limits

[SnapshotSessionStore](src/Agent3d/SnapshotSessionStore.cs) implements the native `AgentSessionStore` boundary. Scene, last receipt and serialized native session share one atomic snapshot. The edit is saved before a successful result is returned. A stale revision or replay cannot apply a second edit. Server-side owner partitions and a run lock isolate concurrent access; overlapping requests can receive `409 BUSY`.

A process restart recovers the saved scene and receipt but starts a fresh conversation, invalidates pending approvals and reports `SESSION_RESET`. It does not replay a mutation. Ordinary empty-message hydration preserves a pending approval without running the agent. The UI runtime owns browser transcript/pending-event reconnection; the backend hydration response itself contains state, not a replay of the conversation.

The default `data` directory and local OpenTelemetry diagnostic configuration are excluded from build/publish output. Put custom `DATA_ROOT` locations outside the project tree so runtime conversations cannot accidentally become deployment assets.

Cancel a pending approval by rejecting its original native call. Aborting an HTTP request cancels work before the commit boundary. Cancellation after the atomic save does not undo it; hydrate to discover the committed result.

The scene instructions tell the model to acknowledge rejection and wait for a new request. A model can still offer another proposal; it remains separately approval-gated and must not be executed automatically.

This is a single-process example, not a distributed database or production tenancy service. Local mode is a loopback-only synthetic demonstration. Do not expose it on a public interface or remove the hosted identity checks.

### Pinned SDK compatibility

The [application manifest](src/Agent3d/Agent3d.csproj) and lock file pin Harness **1.25.0**, MAF AG-UI hosting **1.25.0-preview.261014.1**, AG-UI server **1.0.1**, and official Invocations **1.0.0-beta.7**. Preview APIs are opted into at their use sites.

Two integration limits are explicit:

- Harness 1.25.0's optional budget-enabled compaction dropped an approved skill result from the subsequent model input in a scripted reproduction. This example retains the stock no-compaction default, sets the model output cap, and records the configured context capacity; it does not enforce that capacity with a custom reducer. Long conversations can exceed model limits. Requalify native compaction before enabling it.
- AG-UI server 1.0.1 requires the original `toolCall` in approval resumes but advertises only `approved`. [ApprovalSchema](src/Agent3d/ApprovalSchema.cs) projects a schema from the native `AGUIToolApprovalResumePayload` using supported JSON serialization metadata. It changes no interrupt identity or execution authority. Remove this projection when the native producer advertises its actual required shape. Incomplete resumes are rejected before reaching the native store.

The [tests](tests/Agent3d.Tests) distinguish scripted model output from real inference. Validation scopes are separate:

| Scope | Observed result |
|---|---|
| Connected development | Foundry reads and local-browser preview, approval, rejection and pending-approval reload passed |
| Hosted core, deployment version 3 | Authenticated hydration/read/preview/approve-once/replay/reject passed; exact scene and receipt comparisons matched only the approved edit; rejection produced no new native proposal in this run |
| Hosted lifecycle, deployment version 2 | Actual stop/resume preserved the exact scene and receipt, reported `SESSION_RESET`, and rejected the pre-restart pending approval |
| Local CPU, Qwen3-4B-Instruct | Native `list_objects` returned its result at about 217 seconds, then the test-only 240-second turn limit cancelled the run before highlighting or a final answer; no edit phase was attempted |

The hosted version numbers identify the isolated deployments used for validation, not repository releases. After the local cancellation, no-model hydration confirmed revision 0, no highlights and no mutation receipt. Application execution budgets were not changed. **The full local-model workflow and network-denied/air-gapped operation remain unqualified.** A local endpoint or a passing scripted test is not proof of either.

## OpenTelemetry and offline use

The Harness supplies agent/model instrumentation. The application adds scene edit/save spans and edit outcome counts. Prompt/tool payload capture is not enabled. Configure standard OTLP variables before starting the application:

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4317"
$env:OTEL_EXPORTER_OTLP_PROTOCOL = "grpc"
$env:OTEL_EXPORTER_OTLP_HEADERS = "x-otlp-api-key=$env:OTLP_API_KEY"
```

Use an authenticated [standalone Aspire Dashboard](https://aspire.dev/dashboard/standalone/), without an AppHost. The qualified dashboard image is `mcr.microsoft.com/aspire/dashboard:13.6.1@sha256:03099004e132a7ca2feb66c5fa24715ffeaaf2fffab6f8b2cda23de3abd3ea45`. Bind its UI and ingestion ports to loopback, retain BrowserToken authentication, and require an OTLP API key. Do not log or commit that key. Verify the actual `agent-3d` service and native run spans in the dashboard; a generated trace id alone does not prove delivery.

Before disconnecting, stage NuGet packages, published application files, UI assets, model runtime/weights and the dashboard image. Run published files without package restore, model downloads, cloud credentials or remote telemetry. Use operating-system egress controls and a qualified local model to verify isolation. Model weights have separate licenses and are not included here.

## Extend and verify

Add skill instructions under `skills`; native skill discovery handles them. Add a typed method in [SceneTools](src/Agent3d/SceneContextProvider.cs), register it with `AIFunctionFactory`, and add a focused behavior test for a new tool. Wrap a mutating tool in `ApprovalRequiredAIFunction`. No planner or transport redesign is needed.

```powershell
dotnet test --no-restore
dotnet format --no-restore --verify-no-changes
```

For official hosted-agent deployment, see [the hosting guide](docs/hosting.md). No CI/CD or resource provisioning runs automatically.
