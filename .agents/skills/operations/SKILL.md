---
name: operations
description: "Maintain Agent Core configuration, safe logging, OpenTelemetry measurements, native development and Docker Compose workflows."
---

# Operations

Read [Operations](../../../docs/17-observability-and-operations.md), options in [Persistence and Configuration](../../../docs/15-persistence-and-configuration.md), and the requested [milestone](../../../docs/18-implementation-plan.md).

- Native .NET + Vite is the fast development path; Docker is optional. Provider profile and storage selection are independent. Synthetic runtime needs no provider keys/network; installing/building dependencies may need network. Intended CI is GitHub Actions once project scripts exist; core CI stays key-free. `OPENROUTER_API_KEY` and `OPENAI_API_KEY` are backend-only (process environment, `dotnet user-secrets`, or gitignored Compose `.env` interpolation). Default tests must not require them. Do not bind `IConfiguration` from `local/` or other named secret drop-files.
- Add a minimal key-free Synthetic Compose path at Milestone 5. Milestone 12 hardens packaging: one application container serves REST/SignalR and built SPA, with a persistent SQLite volume. Preserve WebSocket routing and /health; SPA fallback must not swallow API/hub errors. Default `docker compose up` stays Synthetic. Real/hosted Compose is `docker-compose.real.yml` overlaid on `docker-compose.yml` (OpenRouter text, matching `http-openrouter`); interpolate `OPENROUTER_API_KEY` from gitignored `.env` or the host environment, never committed secrets.
- Keep secrets backend-only. Redact provider bodies, URL query strings and AdditionalHeaders; exclude audio/full conversations from default logs.
- Use structured logging, ActivitySource/Meter AgentCore.Runtime and OpenTelemetry. IDs belong in traces/logs; metric tags stay low-cardinality. Measure STT/controller/LLM/segmentation/TTS/transport/playback separately.
- Use local monotonic durations, not unsynchronized browser/server wall-clock subtraction. Report measured sample size, profile/device/network and percentiles; targets are not SLAs.
- Preserve single-process ownership, bounded graceful shutdown and checkpoint recovery. Apply migrations before traffic; back up SQLite through its backup mechanism or controlled stopped-app copy and verify restoration.
- Hosted/hybrid/on-prem replaces infrastructure capabilities, not runtime semantics. Do not introduce Kubernetes, extra application services or horizontal SessionManager replicas for MVP.
- Verify existing workflows through [testing](../testing/SKILL.md); update documented commands as milestones make them runnable and distinguish future commands from executed checks.
