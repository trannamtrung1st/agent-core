---
name: architecture
description: "Apply Agent Core project dependency boundaries, session ownership and MVP invariants when designing or reviewing technical changes."
---

# Architecture

Read [Architecture](../../../docs/03-system-architecture.md), [Technology Decisions](../../../docs/10-technology-decisions.md) and [Repository Structure](../../../docs/11-repository-structure.md) for boundary decisions. Use [Scope](../../../docs/02-mvp-scope.md) for scope and [Backend Interfaces](../../../docs/04-backend-interfaces.md) for port changes.

- Preserve dependencies: Domain is BCL-only; Application references Domain plus logging abstractions; Contracts contains wire records/BCL/MessagePack annotations. Infrastructure references Application/Domain, never Contracts. Api composes and maps wire DTOs to normalized application values.
- Keep ASP.NET, EF, provider DTOs and vendor options out of Domain/Application. Avoid a catch-all Common project, MediatR, actor framework, internal broker or generic repository framework.
- One Session Runtime mailbox owns mutable state, including the current text/voice mode of that conversation. Supervised workers publish normalized results rather than mutate runtime state. Bounded channels and separate audio ingress keep interruption responsive.
- Keep text, STT and TTS independently replaceable. Native speech-to-speech is future design, not an MVP branch. Synthetic adapters exercise the same boundaries without provider credentials/network. Do not add extra external services solely for testing.
- Cancellation alone is insufficient: preserve response identity and supersession guards across callbacks, transport, history and playback. Continuous capture and conservative heard context are architectural requirements.
- Trace ownership, dependencies, lifetime, failure/cancellation and affected milestone gates before changing a boundary. Surface conflicting canonical decisions rather than silently choosing an alternative architecture.
