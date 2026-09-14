# Milestone 2 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only.

## Commands and results

| Command | Exit status |
| --- | --- |
| `dotnet test AgentCore.sln --nologo` | 0 — Domain 3, Application 6, Infrastructure 6, Api 5 passed |

Observed:

- `agents/examiner.json` and `agents/customer-support.json` load and validate; `GetAsync("examiner", 99)` is null; `languageModel: missing-llm` fails store construction with `ValidationError`.
- Prompt identity sections contain Alex vs Sam; mode section is shared; current user turn appears once.
- `DefaultAgentBrain` returns `StaySilent` for idle when help is not useful.
- Two simultaneous `SessionRuntime` instances keep independent histories; snapshot revision is 4 after one synthetic turn under `FakeTimeProvider` / `DeterministicIdGenerator`.
- Application and Domain sources contain no OpenAI/OpenRouter/ChatCompletion DTO types.
- Scripted text still succeeds for both identities through the same mailbox runtime.

Definition load rules: recursive `*.json` under `AgentCore:AgentDirectory` (default `agents/` at repo root), camelCase JSON, unknown fields rejected, aliases must resolve to Synthetic `primary-llm` / `primary-stt` / `primary-tts`.
