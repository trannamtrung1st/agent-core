# Agent Core MVP

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

Documentation is implementation-ready; the application has not been implemented. Start with [Implementation Plan](docs/18-implementation-plan.md), following the milestones in order.

Use a .NET 10 LTS / C# 14 modular monolith with ASP.NET Core Minimal APIs, SignalR + MessagePack, EF Core 10 + SQLite, and a React/Vite strict TypeScript SPA. One asynchronous Session Runtime mailbox owns each session's mutable state. Provider capabilities remain independently replaceable through ILanguageModel, ISpeechRecognizer and ISpeechSynthesizer. OpenAICompatibleLanguageModel uses direct HTTP/SSE; OpenRouter is its preferred initial hosted text configuration, not an architectural dependency.

The canonical MVP voice pipeline is **STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS**. The microphone stays active during TTS playback, using full-duplex AudioWorklets and canonical PCM16 mono 24 kHz. Cancellation and response identity jointly prevent stale output after interruption. Synthetic mode boots without AI keys or network access and is the default development/test path. Hosted, hybrid and future fully on-prem configurations use the same runtime: replace the text endpoint with a local compatible inference server and select local STT/TTS independently. Native speech-to-speech/realtime reasoning is a future optional optimization, excluded from MVP. Authentication, WebRTC, vector memory and general autonomous-agent platform features are outside MVP.

## Implementation specifications

- [Technology Decisions](docs/10-technology-decisions.md)
- [Repository Structure](docs/11-repository-structure.md)
- [Backend Implementation Specification](docs/12-backend-implementation-spec.md)
- [Frontend Implementation Specification](docs/13-frontend-implementation-spec.md)
- [HTTP API and Realtime Protocol](docs/14-api-and-realtime-protocol.md)
- [Persistence and Configuration](docs/15-persistence-and-configuration.md)
- [Testing Strategy](docs/16-testing-strategy.md)
- [Observability and Operations](docs/17-observability-and-operations.md)
- [Implementation Plan](docs/18-implementation-plan.md)

The future run commands are documented in [Operations](docs/17-observability-and-operations.md#running-after-implementation); they become runnable as implementation milestones create the projects. The application source and build artifacts have not been created.
