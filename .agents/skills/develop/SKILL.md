---
name: develop
description: "Implement, fix, plan or review Agent Core code through milestone gates and focused specialist skills. Use document for documentation-only work."
---

# Develop

Read root [AGENTS.md](../../../AGENTS.md) and [README.md](../../../README.md). Infer implementation, fix, plan or review from the request; a review or plan does not authorize implementation.

1. Identify the affected milestone and prerequisites in [Implementation Plan](../../../docs/18-implementation-plan.md). Inspect actual artifacts and completed gates; future specs are not evidence of implementation. For a milestone request, complete its acceptance criteria before dependent work.
2. Read only relevant canonical specs through the specialists below. Read their SKILL.md files and apply them; composition does not require spawning agents.

| Affected concern | Specialist |
| --- | --- |
| Project boundaries, ownership, invariants or design decisions | [architecture](../architecture/SKILL.md) |
| src/, tests/AgentCore.*.Tests/, services or controller | [backend](../backend/SKILL.md) |
| web/, React state, browser lifecycle or UI | [frontend](../frontend/SKILL.md) |
| SignalR, audio, session protocol, response identity or reconnect | [realtime](../realtime/SKILL.md) |
| LLM/STT/TTS adapters, ports or provider selection | [providers](../providers/SKILL.md) |
| Memory store, SQLite/EF, snapshots or recovery | [persistence](../persistence/SKILL.md) |
| Deployment, configuration, logging or measurements | [operations](../operations/SKILL.md) |
| Verification for every code change or review | [testing](../testing/SKILL.md) |
| Visual presentation only | [impeccable](../impeccable/SKILL.md) with [.agents/context](../../context); `/docs` still wins on behavior |

3. Implement the smallest complete vertical change within the requested scope and milestone non-goals. For review, trace behavior and report actionable findings with file/line evidence instead of silently fixing it.
4. Run applicable testing checks and milestone gates, including the [runtime verification workflow](../testing/SKILL.md#runtime-verification). For frontend behavior, use Playwright MCP against the running Synthetic app when available; for backend behavior, execute an affected use case through integration tests or a local Synthetic host. Choose the scenario and expected outcome before running it, check the observed result, and fix/recheck failures within the authorized scope. Reviews exercise existing behavior where practical and report failures without silently fixing them; plans identify required verification without starting the app. Static inspection/build success alone is not functional verification. Default suites must pass without hosted keys; do not treat missing `OPENROUTER_API_KEY` / `OPENAI_API_KEY` as a test failure. A small bug fix need not implement its entire containing milestone.
5. If implementation changed a documented fact, command, contract or decision, use [docs-consistency](../docs-consistency/SKILL.md) to update its owner and necessary references.
6. Report changes or review findings, exact checks/results, exercised scenarios with expected versus observed outcomes, milestone acceptance/status and remaining issues. Identify fallbacks and blocked/unrun verification explicitly; distinguish static review from runtime evidence and completing a local change from completing a milestone.
