# Agent Core MVP

Unified evaluation now includes independently reviewed native Sandbox Scripts, immutable Suite/Script versions, guarded publication, trusted Run verification and attributable Manual assessment. [Native execution verification](docs/reports/native-evaluation-scripts-2026-10-27.md) records real local OpenSandbox execution, both-store recovery/security checks and the responsive UI workflow. Initiative 7 is ready for closure and Initiative 8 — Session Goals can proceed through the existing owners. Historical [foundation](docs/reports/unified-scripts-evaluation-2026-10-27.md) and [stabilization](docs/reports/unified-evaluation-stabilization-2026-10-27.md) reports retain their original scope; hosted CI and production/provider certification are separate.

Decision Intelligence now has a local foundation, canonical inherit/fixed/Auto preferences, tenant Routing controls and immutable per-Run decisions. It is disabled/shadow by default. [Delivery evidence](docs/reports/decision-intelligence-delivery-2026-10-26.md) records the Synthetic delivery; [native MLX and held-out verification](docs/reports/kev-native-verification-2026-10-26.md) now completes available-hardware qualification, including an external-service Compose bridge. [Routing-specific follow-up](docs/reports/kev-routing-verification-2026-10-26.md) adds bounded task hints, real ModelRouting/ReasoningEffort held-out results and a measured native routing budget. NVIDIA-specific performance qualification remains separately pending; [operator runbook](docs/decision-intelligence-runbook.md) covers the optional service and offline migration.

The Account/tenant IAM successor has completed local implementation and Synthetic proposal acceptance in the final delivery. [Final delivery](docs/reports/tenant-final-delivery-2026-10-24.md) records exact source/commit status, all method/I/O/worker and UI evidence, operating instructions and measured limits. The final 80-file scope is approved for direct commit to main; live-provider/customer outcomes, physical-device certification, rollout and optional CI are not implied. Historical reports retain their own source versions.

The [IAM stabilization follow-up](docs/reports/iam-stabilization-2026-10-24.md) owns subsequent launcher, Compose/HTTPS, canonical Playwright and approval-review integration evidence; earlier delivery fingerprints remain historical.

Tenant-owned dynamic model management is implemented locally: five adapter kinds, protected provider connections, explicit offerings/defaults and canonical Chat/authoring/runtime selection. The [delivery audit](docs/reports/dynamic-model-management-delivery-2026-10-25.md#final-requirement-audit) records focused offline/Synthetic evidence, migration/reset policy and unrun live-provider/CI limits.

The OpenRouter preset is DeepSeek V4.1 Flash 1017 (`deepseek/deepseek-v4.1-flash-1017`). The [tracked snapshot upgrade](docs/15-persistence-and-configuration.md#real-model-catalog) preserves tenant offering IDs/defaults and historical pins; stop the host, back up SQLite and apply the offline EF migration before restarting.

The [model and Space integration](docs/reports/model-space-main-integration-2026-10-25.md) records combined migration, authority and conversation verification after synchronizing main. Inference offerings remain tenant scoped; Space/Environment selection retains independent membership and IAM checks.

Agent Self-Management completion uses ordinary tenant Instances and the existing IAM/configuration/approval owners. The [completion delivery](docs/reports/agent-self-management-delivery-2026-10-26.md) records the one-time sparse-policy migration, retained immutable history, UI refinement and current local verification limits. No special Admin Assistant feature is installed.

Chat supports Saved or fixed 24-hour Temporary conversations, default-Off scoped agent completion, and deliberate long-text attachments. [The cutover report](docs/reports/chat-simplification-2026-10-26.md) records local Synthetic verification and operational limits. [The retention follow-up](docs/reports/retired-run-content-verification-2026-10-26.md) removes settled Run conversation copies and qualifies deletion, expiry and restart recovery while preserving required effect evidence.

Agent Core is a lightweight runtime for creating persistent AI identities capable of natural, real-time conversation.

An agent can represent a customer service representative, examiner, tutor, friend, interviewer, receptionist, or another role. The goal of the MVP is not to build a general-purpose autonomous-agent platform. The goal is to prove that one reusable Agent Core can inhabit different identities and interact naturally through text and voice.

The distinctive idea is the combination of:

- persistent agent identity and behavioral policy;
- continuously running conversational state;
- a separate Interaction Controller that observes the live environment;
- interruption and turn-taking support;
- proactive interaction initiated by the agent when appropriate;
- provider-agnostic backend interfaces;
- a simple personal-chat-style web UI with a voice-call mode.

## MVP thesis

> Make an AI agent feel present in a live conversation.

The most important qualities are:

1. low latency;
2. natural turn-taking;
3. interruption / barge-in;
4. identity consistency;
5. restrained proactive behavior;
6. conversation continuity.

Complex tool orchestration, multi-agent systems, workflow builders, marketplaces, and advanced long-term memory are intentionally outside the first MVP.

## License

Agent Core is proprietary software. Source availability does not grant permission to use, copy, modify, or redistribute it. See [LICENSE](LICENSE) for details.

## Project documents

- [Product Vision](docs/01-product-vision.md)
- [MVP Scope](docs/02-mvp-scope.md)
- [System Architecture](docs/03-system-architecture.md)
- [Backend Interfaces](docs/04-backend-interfaces.md)
- [Interaction Controller](docs/05-interaction-controller.md)
- [Realtime Voice and Conversation](docs/06-realtime-voice.md)
- [Event Model](docs/07-event-model.md)
- [Development Roadmap](docs/08-development-roadmap.md)
- [Demo Scenarios](docs/09-demo-scenarios.md)

## Implementation baseline

The [clean baseline delivery](docs/reports/clean-baseline-delivery-2026-10-26.md) records the canonical configuration cutover, retained Google grant/reconnect policy, General Assistant v34 and real local Sandbox PNG publication proof. Follow the [cutover runbook](docs/clean-baseline-cutover.md) before deploying this binary with existing configuration and state. MarkItDown remains deferred and absent from runtime dependencies.

The Space/Environment successor replaces persistent Instance home ownership with tenant Spaces, protected Private Spaces, per-Session context and immutable Run captures. Membership controls contextual resource availability; optional Environments contain on-demand typed variables, and Secrets remain protected tenant resources. [Current implementation evidence](docs/reports/space-environment-implementation-2026-10-25.md) tracks qualification and remaining integration; [operator cutover](docs/space-environment-cutover.md) covers matching code/schema/root deployment and recoverable backups. Historical IAM/MVP delivery reports do not accept this successor.

The original MVP and post-MVP freeze summaries are historical records. Their old authentication/route examples are not current interfaces. Milestone status and links to their frozen evidence belong to the [Implementation Plan](docs/18-implementation-plan.md) and [dated reports](docs/reports/); see the [MVP handoff](docs/reports/m12-mvp-handoff.md) for that original scope.

Current code uses .NET 10/C# 14, the composed provider-neutral speech pipeline, tenant-qualified Session/Activation/Run execution and Ant Design v6. Native development and Docker Compose remain supported workflows; use [Operations](docs/17-observability-and-operations.md) for current bootstrap, configuration, launch and recovery instructions. Synthetic needs no AI credentials. Real/native effects require their documented readiness, current authority and explicit verification.

| Current concern | Authoritative owner |
| --- | --- |
| Runtime, IAM and resource ownership | [System Architecture](docs/03-system-architecture.md) |
| Tenant resource/model/approval contracts | [Backend Interfaces](docs/04-backend-interfaces.md) |
| Text/voice conversation and browser transport | [Voice](docs/06-realtime-voice.md), [Protocol](docs/14-api-and-realtime-protocol.md) |
| Agent/Definition settings, Skills, Resources and Self-Management | [Backend contracts](docs/04-backend-interfaces.md#unified-self-management-application-owners), [Frontend](docs/13-frontend-implementation-spec.md) |
| Automation, background destinations, reports and waits | [Architecture](docs/03-system-architecture.md#automation-destinations-and-completion-obligations), [Protocol](docs/14-api-and-realtime-protocol.md) |
| Workspace, Office, Attachment, Artifact, account and native operations | [Backend contracts](docs/04-backend-interfaces.md), [Persistence/configuration](docs/15-persistence-and-configuration.md) |
| Scope of completed local checks and remaining acceptance | [IAM delivery ledger](docs/reports/multitenant-iam-delivery-2026-10-16.md), [Testing](docs/16-testing-strategy.md) |

## Implementation specifications

- [Technology Decisions](docs/10-technology-decisions.md)
- [Repository Structure](docs/11-repository-structure.md)
- [Backend Implementation Specification](docs/12-backend-implementation-spec.md)
- [Frontend Implementation Specification](docs/13-frontend-implementation-spec.md)
- [Design system and presentation guidance](.agents/context/DESIGN.md)
- [Shared Admin compositions](web/src/features/admin/README.md)
- [Ant Design migration handoff](docs/reports/antd-migration-handoff.md)
- [Current design system sync and focused UI checks](docs/reports/design-system-sync-2026-10-16.md)
- [HTTP API and Realtime Protocol](docs/14-api-and-realtime-protocol.md)
- [Persistence and Configuration](docs/15-persistence-and-configuration.md)
- [Testing Strategy](docs/16-testing-strategy.md)
- [Observability and Operations](docs/17-observability-and-operations.md)
- [Implementation Plan](docs/18-implementation-plan.md)
- [P0 agent-lifecycle handoff](docs/reports/p0-agent-lifecycle-handoff.md)
- [P1 replaceable speech handoff](docs/reports/p1-replaceable-speech-handoff.md)

Run commands and recovery guidance are documented in [Operations](docs/17-observability-and-operations.md#running-after-implementation). Native `scripts/dev-real.sh` loads the private root `.env` automatically; [`.env.example`](.env.example) contains the startup preset; [the host reference](docs/host-configuration-reference.md) documents advanced options and boundary translations and fresh identity setup fields. No manual shell export or user-secrets setup is needed for that launcher.

## Working with Codex and Cursor

Both editors use the shared [AGENTS.md](AGENTS.md) instructions and the skills in [.agents/skills](.agents/skills). This uses their native shared skill discovery ([Codex documentation](https://learn.chatgpt.com/docs/build-skills), [Cursor documentation](https://cursor.com/docs/skills)); no editor-specific skill copies are needed. Existing Playwright MCP configuration remains in `.codex/config.toml` and `.cursor/mcp.json`. Impeccable’s installer may create a local `.cursor/skills/impeccable` copy; it is gitignored. Use [`.agents/skills/impeccable`](.agents/skills/impeccable) as the committed skill.

Use [develop](.agents/skills/develop/SKILL.md) for code implementation, fixes, planning and reviews, and [document](.agents/skills/document/SKILL.md) for specifications, decisions and documentation reviews. For visual UI quality, use [impeccable](.agents/skills/impeccable/SKILL.md); product and visual adapters live in [.agents/context](.agents/context) and defer to `/docs`. In Codex, invoke `$develop` or `$document`; in Cursor, select `/develop` or `/document` from the skill menu. Both workflows also support selection from a matching natural-language request.

| Task | Codex example | Cursor example |
| --- | --- | --- |
| Implement a milestone | `$develop implement milestone 1` | `/develop implement milestone 1` |
| Review code | `$develop review the frontend realtime implementation` | `/develop review the frontend realtime implementation` |
| Review specs | `$document review the repo for implementation readiness` | `/document review the repo for implementation readiness` |
| Update a decision | `$document update specs for local STT support` | `/document update specs for local STT support` |
| Visual UI review | `$impeccable audit` / `$impeccable polish` | `/impeccable audit` / `/impeccable polish` |

These two composition skills load only relevant specialists: architecture, backend, frontend, realtime, providers, testing, persistence, operations and docs-consistency. Impeccable is a separate UI-design skill, not a third composition entry. Product/design documents remain the source of truth. Documentation work stays docs-only unless implementation is explicitly requested; these agent instructions and skills do not start Milestone 1.

Development and behavior reviews include [runtime verification](.agents/skills/testing/SKILL.md#runtime-verification) when runnable. Both editors should use Playwright MCP to exercise affected frontend journeys against the running Synthetic app, with Playwright E2E or another browser tool as a fallback. Backend work should execute representative use cases through integration tests or a local Synthetic host. Reports distinguish observed outcomes from static inspection and name any blocked verification; default runs remain key-free.

The Playwright MCP configurations use isolated browser profiles so editor sessions do not contend for one persistent profile. Output uses Playwright's default workspace/temporary-directory resolution rather than paths relative to the server's launch directory. Reconnect the MCP server after configuration changes; use the returned artifact paths when reporting evidence. See [Playwright MCP configuration](https://github.com/microsoft/playwright-mcp#configuration).
