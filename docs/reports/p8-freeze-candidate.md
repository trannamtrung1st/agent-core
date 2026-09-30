# P8 — Agent Step, Chat action, and Skills closure evidence

This report records the P8 closure. The hosted Synthetic workflow is green on `ca3eb235a3b458e55002d2c8f610df868b2cd39d`. Workflow [`36693649631`](https://github.com/trannamtrung1st/agent-core/actions/runs/36693649631) failed earlier on `af3b4cd` because the Skill journey left a voice-disabled managed instance as the default chat identity, and 10 Voice-control tests could not find the Voice button. `ca3eb23` archives that instance after the journey's pin check. The commit that records this hosted URL is documentation only. **P9** is next. Do not reopen P8 without a reproducible regression. Do not reopen frozen P4–P7.7 reports.

## Candidate

| Item | Value |
| --- | --- |
| Baseline | `f9d64e84b1346fce3fce9acf2329f0bf08d5e3fe` |
| Agent Step | `c1be72c` |
| Chat action | `e61baf4`, Wait return-control `cce65e4` |
| Definition Skills | `53b2eb1` |
| Activation pin | `545dc69`, batch pin `91e1f8b` |
| Admin authoring | `de8b4d2` |
| Skill-form context | `4610cfd` |
| Bounded journey | `af3b4cd` |
| Shared-catalog Voice cleanup | `ca3eb235a3b458e55002d2c8f610df868b2cd39d` |
| This report | The commit that records the hosted URL. Its parent is `ca3eb235a3b458e55002d2c8f610df868b2cd39d` |
| Closure candidate | `ca3eb235a3b458e55002d2c8f610df868b2cd39d` |
| Hosted checkout | [workflow `36696902928`](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) — **green** on `ca3eb235a3b458e55002d2c8f610df868b2cd39d`. Public page: Status Success, total 23m 59s. Jobs: Synthetic offline gates 23m 55s, Synthetic Compose smoke 1m 14s |
| Prior hosted attempt | [workflow `36693649631`](https://github.com/trannamtrung1st/agent-core/actions/runs/36693649631) — **failed** on `af3b4cde3efd2c88321fba385d308408f123a9a9`. Compose smoke succeeded. Synthetic Playwright failed 10 Voice-control tests and passed 58 |

## Decisions implemented

A terminal model result normalizes to one provider-neutral Agent Step. Disposition (`Continue`, `Wait`, `Complete`, `Blocked`) stays separate from the Chat action. The model cannot supply a destination. `chat.respond` is an application action, not a tool. Wait, and Complete or Continue with no Chat action, return control without a successful assistant entry and without a failure diagnostic. Blocked uses the existing diagnostic path. Optional Definition Skills publish with the version and do not grant tools, credentials, or approval. `chat.respond` may be required and is not inserted into the tool allowlist. At most three Skills activate by case-insensitive keyword match, in definition order. The ordered ids are pinned on the turn before the model request, including the last entry of a queued batch and a compaction-deferred replacement. Only those procedures enter the prompt. Admin Form and Advanced JSON edit one candidate. Published skills are read-only. Schema version stays 1. Missing `skills` is an empty set.

## Compatibility retained

Chat remains the only P8 application proof. Streaming, voice, response identity, MessagePack default, JSON diagnostic parity, reconnect, tool limits (12 steps, 30 seconds per tool, 120 seconds overall, 8 MiB), approval, WorkItems, triggers, and Core-admitted memory stay in place. `IAgentBrain` stays the pre-generation decision. Provider DTOs stay in Infrastructure. One Session Runtime mailbox owns mutable conversation state. Synthetic mode needs no provider credentials. P4–P7.7 reports were not rewritten.

## Non-goals left deferred

Browser or computer automation, a Playwright runtime provider, MCP as the semantic center, Application Binding persistence, a Skill marketplace, a generic Agent Step event store, an autonomous recursive loop, a general Instance filesystem, PostgreSQL, Redis, Kubernetes, microservices, RBAC, and production infrastructure. P9 owns the next provider/extensibility exercise.

## Verification

Local commands below are the recorded pre-closure runs. The hosted row is workflow `36696902928` on `ca3eb23`. This URL-recording commit does not change runtime behavior.

| Command | Result |
| --- | --- |
| `dotnet test AgentCore.sln --nologo` on the parent of the docs-status commit | Passed. Domain 137. Application 926 passed, 1 skipped. API 252. Infrastructure 559 passed, 13 skipped |
| `pnpm run test --run` in `web/` on `ce14ee7` | 75 files, 546 tests passed |
| `pnpm run build` in `web/` | Passed. Chunk-size and SignalR comment warnings only |
| Playwright `synthetic` + `browser-stt` + `browser-browser`, `CI=1`, on `ce14ee7` | 67 passed. Those projects did not open the Skill form |
| Faithful Manual-A, admin-lifecycle, p76-admin on `ce14ee7` | 1 passed each |
| `./scripts/compose-sqlite-volume.sh` after the pin migration | Passed |
| `web/e2e/p8-skill-journey.spec.ts` on an isolated database | 1 passed in 21.5s |
| Shared `synthetic.db`: `p8-skill-journey`, `speech-locale`, `voice-capture` | 5 passed in 28.2s before `ca3eb23`; review 0028 re-ran the same command on that commit and recorded 5 passed in 36.8s |
| Hosted workflow `synthetic` on `af3b4cd` | [workflow `36693649631`](https://github.com/trannamtrung1st/agent-core/actions/runs/36693649631) failed. Compose smoke succeeded. Synthetic Playwright failed 10 Voice tests |
| Hosted workflow `synthetic` on `ca3eb23` | [workflow `36696902928`](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green. Synthetic offline gates and Compose smoke both succeeded |

The Application and Infrastructure skips are opt-in live-provider tests. Live-provider probes were not run for this closure.

## UI

Impeccable context loaded `.agents/context/PRODUCT.md` and `DESIGN.md` for the definition editor. No Admin surface brief is selected. Skill cards stay on the existing Admin field grid. The bounded journey covered desktop 1280×900, Tab from Skill id to name with a visible focus shadow, empty, dirty, invalid JSON, publish-blocked, loading, Form/JSON, about 390px with no sideways scroll, and a read-only published skill list. This closure did not redesign that surface.

## Deferred

P9 is next and is not started. Browser automation, Application Binding persistence, a global Skill platform, and production infrastructure remain out of P8.
