---
name: backend
description: "Implement or review Agent Core .NET services, runtime/controller logic, API mapping, DI lifetimes and cancellation behavior."
---

# Backend

Read relevant sections of [Backend Implementation](../../../docs/12-backend-implementation-spec.md), [Backend Interfaces](../../../docs/04-backend-interfaces.md) and [Repository Structure](../../../docs/11-repository-structure.md). For state transitions, consult [Interaction Controller](../../../docs/05-interaction-controller.md) and [Event Model](../../../docs/07-event-model.md).

- Use .NET 10 / C# 14, ordinary services and constructor DI. Keep Minimal API endpoints and SignalR hubs thin; runtime logic belongs in Application.
- Match lifetimes: singleton SessionManager, TimeProvider, ID generator, immutable definition cache and provider factories; per-runtime controller/state/cancellation; per-operation DbContext via factory. Adapters are thread-safe/stateless or instantiated per provider session.
- Use TimeProvider and IIdGenerator. Keep model/classifier/TTS/persistence work outside the mailbox; supervise tasks and route results through it. `RequestInterruptionClassification` is the classifier; `RequestAgentDecision` is IAgentBrain. Validate response/candidate/revision/timer identity before accepting delayed results.
- Mark supersession before cancellation. Preserve session-level STT when response-level LLM/TTS is cancelled. Bound queues and keep interrupt/end control usable under load.
- Pin validated definition versions. Build specified prompt sections from eligible history, include the current turn once and respect received/heard prefixes instead of unplayed generated text.
- Apply [realtime](../realtime/SKILL.md) for transport, [providers](../providers/SKILL.md) for adapters, [persistence](../persistence/SKILL.md) for durable state and [testing](../testing/SKILL.md) for gates. Do not expose public debug/event-injection endpoints for test convenience.
- For implementation, fixes and behavior reviews, follow [runtime verification](../testing/SKILL.md#runtime-verification): execute a representative affected use case through an existing integration fixture or a local Synthetic host when runnable. Assert externally observable results and relevant state effects, including a failure/recovery or boundary case where applicable. Use WebApplicationFactory for HTTP lifecycle, loopback Kestrel plus the real JavaScript client for SignalR/MessagePack, and temporary SQLite for durable/reopen behavior. A build, mocked unit test or `/health` check alone does not verify an integrated workflow. Add or extend focused integration coverage when a behavior change lacks it; report execution blockers and fallback evidence honestly.
