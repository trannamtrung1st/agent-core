# Repository Structure

The repository contains the Milestone 1 solution (`AgentCore.sln`), versioned demo definitions under `agents/` (examiner, customer-support, compliance, plus `agents/knowledge/` citation bodies), a strict React/Vite skeleton in `web/`, a `Dockerfile` with `docker-compose.yml` (Synthetic) and `docker-compose.real.yml` (hosted overlay), README, docs, shared agent instructions in [AGENTS.md](../AGENTS.md), and twelve skills in [.agents/skills](../.agents/skills) used by both Codex and Cursor. The two composition skills, develop and document, load the relevant specialist playbooks. Impeccable is a UI-design skill, not a composition entry; its product/visual adapters live in [.agents/context](../.agents/context) and are not canonical product specification. `.agents/` contains development-agent instructions and tooling context; `agents/` holds product Agent Definitions. Existing editor-specific files configure Playwright MCP and Impeccable hooks. `npx impeccable install` may also write a Cursor-local copy under `.cursor/skills/impeccable` and `.cursor/agents/`; those paths are gitignored. The committed skill is `.agents/skills/impeccable`. Hooks in `.cursor/hooks.json` and `.codex/hooks.json` call that shared launcher so both editors stay in sync without duplicated skill trees.

The application layout matches the project boundaries below. Root `local/` is a gitignored personal scratch folder (notes, temp files); it is not an application project, not a configuration source, and is never committed. [Persistence and Configuration](15-persistence-and-configuration.md#local-personal-workspace) owns secret-handling rules (process environment, `dotnet user-secrets`, and gitignored Compose `.env`).

```text
src/
  AgentCore.Domain/          # definitions, policies, conversation values
  AgentCore.Application/     # runtime, controller, ports, normalized events
  AgentCore.Contracts/       # HTTP and SignalR DTOs only
  AgentCore.Infrastructure/  # HTTP adapters, synthetic adapters, EF/SQLite, JSON store
  AgentCore.Api/             # composition root, endpoints, hub, startup
tests/
  AgentCore.Domain.Tests/
  AgentCore.Application.Tests/
  AgentCore.Infrastructure.Tests/
  AgentCore.Api.Tests/
web/                        # React/Vite SPA and its frontend tests
agents/                     # versioned JSON definitions, created during implementation
.agents/skills/             # shared agent skills (develop, document, specialists, impeccable)
.agents/context/            # Impeccable PRODUCT.md and DESIGN.md; /docs remains canonical
docs/                       # this specification
local/                      # gitignored personal workspace; never committed
Dockerfile                  # one application container (API + SignalR + built SPA)
docker-compose.yml          # default key-free Synthetic Compose
docker-compose.real.yml     # Real/hosted overlay; interpolates gitignored .env
.env.example                # Compose key template; copy to gitignored .env
```

Arrows below mean “references,” not data flow:

```mermaid
flowchart BT
    Application[AgentCore.Application] --> Domain[AgentCore.Domain]
    Infrastructure[AgentCore.Infrastructure] --> Application
    Infrastructure --> Domain
    Api[AgentCore.Api] --> Application
    Api --> Infrastructure
    Api --> Contracts[AgentCore.Contracts]
```

| Project | Allowed dependencies and responsibility |
| --- | --- |
| Domain | BCL only; immutable Agent Definition, policy and conversation value records |
| Application | Domain and Microsoft logging abstractions; Agent Runtime, Interaction Controller, Session Runtime/manager, ports including IEnvironmentEventIngress, text context builder, ResponseTextAccumulator/SpeechSegmenter, TimeProvider, Channels |
| Contracts | BCL and MessagePack serialization annotations only; wire records, no Domain/Application/provider references |
| Infrastructure | Application, Domain, EF Core 10/SQLite, HttpClient factory/resilience and concrete adapters; never Contracts |
| Api | Application, Infrastructure, Contracts; ASP.NET Core 10, SignalR, serialization, OpenAPI, health, DI and boundary mapping |
| Tests | Corresponding subject project; Application.Tests may use Infrastructure synthetic adapters for conversation suites; Api.Tests uses WebApplicationFactory |
| web | Consumes wire specification, no backend assembly dependencies |

Namespace folders follow features: Application/Sessions, Interaction, Agents, Ports, Events; Infrastructure/Providers/OpenAICompatible, Providers/Synthetic, Persistence, Definitions; Api/Endpoints, Realtime. Do not add a shared catch-all “Common” project.

Use ordinary services and DI. No MediatR, generic repository per entity, actor framework or internal message broker. The API maps wire DTOs to application commands and application outputs back to DTOs. An application `ISessionOutput` boundary is implemented in Api; it exposes normalized application output, never hub objects. ASP.NET concerns do not travel inward.

Singletons: SessionManager, TimeProvider.System, ID generator, immutable validated definition cache and provider factories. Per runtime: Agent Runtime, controller and cancellation/state. Per operation: EF DbContext via factory. HttpClient lifetime is managed by IHttpClientFactory; no per-frame client creation. Provider adapters must either be stateless/thread-safe or instantiated per provider session.

OpenAICompatibleLanguageModel lives in Infrastructure and accepts OpenRouter/direct-hosted/local-server configuration. There is no OpenRouter core project or interface. STT and TTS adapters are separate capability implementations, including synthetic and local variants. Intended CI is GitHub Actions (`.github/workflows/synthetic.yml`) after project scripts exist. Native realtime remains documentation-only future design, not an MVP project or startup dependency.

## Post-MVP observed layout

Infrastructure-only physical layout for session workspaces (`data/workspaces/{sessionId}/workspace/{working,artifacts,state}`), attachment blobs (`data/attachments`), and artifact blobs (`data/artifacts`). Domain/Contracts never receive host paths. Application data stays outside developer `local/` scratch, including `local/tdp-workspace`. Observed sandbox: Infrastructure `DockerSandboxExecutor` bind-mounts only that session working directory into `/workspace/working`; leftover labeled `acsbx-*` containers are removed after success, failure, and cancellation.
