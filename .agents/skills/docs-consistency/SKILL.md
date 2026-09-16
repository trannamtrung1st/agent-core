---
name: docs-consistency
description: "Audit and synchronize Agent Core canonical documentation ownership, terminology, links, examples and milestone/implementation claims."
---

# Documentation consistency

Use the owning document for each fact. Update related summaries only when that fact changes; skills guide execution rather than defining a second product specification.

| Topic | Canonical owner |
| --- | --- |
| Product goals / inclusion | [Vision](../../../docs/01-product-vision.md), [Scope](../../../docs/02-mvp-scope.md) |
| Architecture / ownership | [Architecture](../../../docs/03-system-architecture.md) |
| Application ports | [Backend Interfaces](../../../docs/04-backend-interfaces.md) |
| Controller policy/state | [Controller](../../../docs/05-interaction-controller.md) |
| Voice pipeline / normalized events | [Voice](../../../docs/06-realtime-voice.md), [Events](../../../docs/07-event-model.md) |
| Roadmap summary / demo narratives | [Roadmap](../../../docs/08-development-roadmap.md), [Demos](../../../docs/09-demo-scenarios.md) |
| Decisions / project dependencies | [Decisions](../../../docs/10-technology-decisions.md), [Structure](../../../docs/11-repository-structure.md) |
| Backend / browser implementation | [Backend](../../../docs/12-backend-implementation-spec.md), [Frontend](../../../docs/13-frontend-implementation-spec.md) |
| HTTP/SignalR schema, names, ordering | [Protocol](../../../docs/14-api-and-realtime-protocol.md) |
| Durable schema, revisions, options/DI | [Persistence and Configuration](../../../docs/15-persistence-and-configuration.md) |
| Test fixtures / scenarios | [Testing](../../../docs/16-testing-strategy.md) |
| Run/deploy commands / observability | [Operations](../../../docs/17-observability-and-operations.md) |
| Milestone order, gates, non-goals | [Implementation Plan](../../../docs/18-implementation-plan.md) |
| Visual tokens and presentation system | [.agents/context/DESIGN.md](../../../.agents/context/DESIGN.md); screens and behavior stay in [Frontend](../../../docs/13-frontend-implementation-spec.md) |

README is the overview/index. AGENTS.md owns shared task rules; .agents/skills/ owns workflows/playbooks for both editors. Do not duplicate them into editor-specific files. [.agents/context/PRODUCT.md](../../../.agents/context/PRODUCT.md) is Impeccable tooling context only; it must not become a second product spec.

1. Search affected terms across README, docs and instructions: interface/provider names, response/attachment identity, PCM, state transitions, receipts, storage/profile defaults. Read context rather than mechanically replacing names.
2. Compare statements to their owner. Preserve generated/received/heard distinctions, provider/storage independence, planned/implemented commands and future native realtime versus the composed MVP.
3. Check relative Markdown links from each containing file and fragment anchors, balanced fences, and parsing of complete JSON examples. Deliberately partial snippets are not full JSON or runnable projects.
4. For skills, validate YAML name/description, matching folder names, existing references and progressive routing. Keep develop/document as the two composition entry points.
5. Compare README, roadmap and milestone gates after edits. Check implementation claims against artifacts; documentation readiness does not prove milestone implementation.
6. Report checks and unresolved contradictions with file/line evidence. Documentation review does not generate application/build/config artifacts or silently start implementation.
