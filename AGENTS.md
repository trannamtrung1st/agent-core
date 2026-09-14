# Agent Core

Read [README.md](README.md) before substantial work.

## Authoritative architecture

- [System Architecture](docs/03-system-architecture.md)
- Ports: [Backend Interfaces](docs/04-backend-interfaces.md)
- Interaction behavior: [Interaction Controller](docs/05-interaction-controller.md)
- Voice: [Realtime Voice](docs/06-realtime-voice.md)
- Wire protocol: [HTTP API and Realtime Protocol](docs/14-api-and-realtime-protocol.md)
- Testing: [Testing Strategy](docs/16-testing-strategy.md)
- Implementation order: [Implementation Plan](docs/18-implementation-plan.md)

Follow [Technology Decisions](docs/10-technology-decisions.md) and [Repository Structure](docs/11-repository-structure.md) for technology and project boundaries. Product/design documents are authoritative; skills guide execution. Do not introduce alternative architecture or MVP non-goals without an explicit user requirement.

## Working modes

- For code implementation, fixes, planning or review, read [develop](.agents/skills/develop/SKILL.md).
- For specifications, documentation, decisions or consistency reviews, read [document](.agents/skills/document/SKILL.md).
- Load only the specialist skills relevant to the task. These shared files apply to both Codex and Cursor; do not maintain editor-specific copies.
- Documentation work stays docs-only unless implementation is explicitly requested. Repository agent instructions and skills do not start an application milestone.

## Critical invariants

- One Session Runtime owns mutable conversation state through its mailbox.
- Raw audio does not enter the normal domain mailbox.
- Provider DTOs never cross Infrastructure into Domain, Application or Contracts.
- Superseded responses never become visible again.
- STT, LLM and TTS remain independently replaceable.
- The microphone remains active while the agent speaks.
- Synthetic mode requires no provider credentials.
- No architecture change without updating authoritative docs.
- Complete milestone tests before advancing.

## Do not introduce unless the specification is revised first

Microservices, MediatR forwarding, Kafka/Redis, generic repositories, a native realtime speech-to-speech path, WebRTC, a vector database, or multi-agent features.

## Completion

Run applicable checks and report their results. Do not claim completion or milestone acceptance when required checks fail or were not run; identify the remaining gap.
