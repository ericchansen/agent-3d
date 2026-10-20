# Third-party notices

Original application code is covered by the root [MIT license](LICENSE).

The copied application schema and fixtures are from [ericchansen/agent-3d-ui](https://github.com/ericchansen/agent-3d-ui). Their copyright notice and MIT license are retained in [contracts/LICENSE](contracts/LICENSE).

Runtime dependencies are distributed through NuGet with their own license notices. The package lock files record the exact dependency graph; this file does not replace package licenses.

| Dependency | Upstream |
|---|---|
| Microsoft Agent Framework, including Harness and hosting adapters | [microsoft/agent-framework](https://github.com/microsoft/agent-framework), MIT |
| Microsoft.Extensions.AI and extensions libraries | [dotnet/extensions](https://github.com/dotnet/extensions), MIT |
| AG-UI .NET SDK | [ag-ui-protocol/ag-ui](https://github.com/ag-ui-protocol/ag-ui), MIT |
| OpenAI .NET SDK | [openai/openai-dotnet](https://github.com/openai/openai-dotnet), MIT |
| Azure Identity and AgentServer SDKs | [Azure/azure-sdk-for-net](https://github.com/Azure/azure-sdk-for-net), MIT |
| OpenTelemetry .NET | [open-telemetry/opentelemetry-dotnet](https://github.com/open-telemetry/opentelemetry-dotnet), Apache-2.0 |
| Microsoft OpenTelemetry distribution | [microsoft/opentelemetry-distro-dotnet](https://github.com/microsoft/opentelemetry-distro-dotnet), MIT |
| Protocol Buffers | [protocolbuffers/protobuf](https://github.com/protocolbuffers/protobuf), BSD-3-Clause |

Hosting integration uses the public APIs illustrated by the [official Invocations sample](https://github.com/microsoft/agent-framework/tree/dotnet-1.25.0/dotnet/samples/04-hosting/FoundryHostedAgents/invocations/Hosted-Invocations-EchoAgent). No SDK implementation is vendored. Model runtimes and weights are not redistributed.
