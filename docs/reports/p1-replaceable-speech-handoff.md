# P1 replaceable speech handoff

Run `run-20260918T040554-90562c`, plan revision 11. Docs/handoff item `item-2f8934f04e05` after final gate item `item-384fed029b01` (production revision 19). Synthetic/offline plus fake Browser transports. Live hosted/headset/Web Speech were not opted in and are **not** claimed.

## Status

P0 remains closed because its earlier key-free gates passed (TODO P0 checkboxes; [P0 agent-lifecycle handoff](p0-agent-lifecycle-handoff.md); docs/18 P0 table). P1 independent STT/TTS, Browser client transports, selectable OpenAI TTS, selectable `OpenAICompatibleBatch` STT, mixed plans, speech observability, and the final key-free section-44 gate are **implemented and deterministically verified**. HOSTED-04 is **unverified**. OpenAI realtime STT stays **unselectable**.

## Skills

develop, document, docs-consistency, architecture, backend, frontend, realtime, providers, operations, testing.

## Production batches → commits

| Batch | Item | Commit |
| --- | --- | --- |
| 05 | Independent speech configuration | `ecafe55` |
| 06 | SpeechFactory / no silent fallback | `8eb0dc0` |
| 07 | voiceAvailable + ready transports | `13e2dc7` |
| 08 | Mixed combination resolution | `593cb1f` |
| 09 | Protocol `client.speech.evidence` + client-speech segments | `f820b9e` |
| 10 | Client-transcript admission + playback-gated completion | `61239b7` |
| 11 | Frontend recognizer port | `8581f64` |
| 12 | Long-utterance aggregation + client synthesizer | `ae871fa` |
| 13 | Browser STT lifecycle + client-speech player | `d3be036` |
| 14 | Fake Browser/Browser E2E | `98ff2df` |
| 15 | OpenAI TTS resolver | `e874bc0` |
| 16 | OpenAICompatibleBatch STT | `2a67cc1` |
| 17 | Mixed host plans + mixed preflight | `bb1821d` |
| 18 | Section-42 speech meters | `467fbbb` |
| 19 | Final Synthetic/section-44 gate | none (evidence only; HEAD `467fbbb`) |
| 20 | Canonical docs / TODO / this report | prior handoff commit |
| 21 | Native Web Speech corrective (`be0514c`) | `be0514c` |
| 22 | Pending speechend + idle restart cap | `26e9674` |
| 23 | Mid-utterance native onend preservation | this commit |

Local command logs: `local/tdp-workspace/evidence/p0-p1-replaceable-speech/run-20260918T040554-90562c/` (gitignored).

## Observed versus unverified

| Claim | Class | Notes |
| --- | --- | --- |
| Independent Recognition/Synthesis config | Implemented + deterministic | Nested options; text-only starts without speech keys |
| Browser = client transport | Implemented + deterministic | No fake backend Web Speech adapter |
| Session Runtime / controller vendor-free | Implemented | No OpenAI type names in Application session types |
| Client transcript wire + admission | Implemented + deterministic | MessagePack + ClientTranscriptAdmissionTests |
| Fake Browser STT/TTS lifecycle | Implemented + deterministic | Vitest fakes; Playwright injects fakes |
| Playback-gated clientSpeech completion | Implemented + deterministic | Stop does not dequeue; successful ACK may dispatch queue |
| OpenAI TTS selectable with key | Implemented + dummy-key tests | Live HTTP not run |
| Batch STT selectable with key | Implemented + dummy-key tests | Capabilities streaming/partials false |
| Mixed five-way matrix | Implemented + dummy-key tests | Zero outbound HTTP |
| Speech telemetry dimensions | Implemented + deterministic | No transcript/PCM/keys in tags or details |
| Full key-free CI gate | Deterministically verified | See commands below |
| HOSTED-04 live non-Synthetic voice | **Unverified** | `AGENTCORE_LIVE_*` unset/0; live facts skipped |
| Live Web Speech / headset | **Unverified** | CI uses fakes and fake media devices; native adapter contracts are unit-tested |
| OpenAI realtime STT | Not selectable | No-op must not be chosen |

## Privacy (canonical)

- Browser STT does not send PCM to backend STT.
- The browser vendor may still use a cloud recognizer.
- Browser TTS privacy is platform-dependent.
- Select another STT/TTS adapter for backend-controlled or local speech.
- Do not claim Browser STT/TTS is guaranteed local or offline.
- Default `LogConversationContent=false`.

## Part L / MVP non-goals (unchanged)

No WebRTC, native speech-to-speech reasoning, vector/cross-session memory, WorkItems, background research, scheduled tasks, auth/tenancy, Kubernetes, OpenSandbox, marketplace, admin editor, billing, automatic paid-provider routing, or a second UI framework. Historical MVP Milestone 1–12 and P0-A–F records stay intact.

## Final key-free gate (batch 19 baseline; post–`be0514c` refresh below)

Flags: `AGENTCORE_LIVE_PROVIDER_TESTS=0`, `AGENTCORE_LIVE_OPENAI_STT=0`, `AGENTCORE_LIVE_OPENAI_TTS=0`. Playwright `CI=1` on isolated ports 5280/6273, 5281/6274, 5282/6275 with InMemory disposable roots.

| Command | Observed (batch 19) |
| --- | --- |
| `npm ci --prefix tests/realtime-js` | exit 0 |
| Domain tests | 22 passed |
| Infrastructure tests | 101 passed, 9 skipped (live OpenAI STT/TTS, OpenRouter smoke, Docker sandbox) |
| Application tests (`--blame-hang-timeout 5m`) | 291 passed |
| API tests | 105 passed |
| `pnpm install --frozen-lockfile` | exit 0 |
| `pnpm run test --run` | 242 passed |
| `pnpm run build` | exit 0 |
| `CI=1 pnpm exec playwright test` | 25 passed (synthetic + browser-stt + browser-browser) |

## Post–native-STT-hardening gate (after batch 22)

Same flags and ports. Re-run after pending-`speechend` closure and idle `onend` restart-cap fixes.

| Command | Observed |
| --- | --- |
| Domain tests | 22 passed |
| Infrastructure tests | 101 passed, 9 skipped |
| Application tests (`--blame-hang-timeout 5m`) | 292 passed |
| API tests | 105 passed (one `SessionHostRaceTests` flake on first full run; isolated re-run passed) |
| `pnpm run test --run` | 256 passed |
| `pnpm run build` | exit 0 |
| `CI=1 pnpm exec playwright test` | 26 passed |

Count growth versus batch 19 is additional Browser-STT contract coverage, not regressions.

## Remaining work

- Implement a real OpenAI realtime transcription session before Adapter=`OpenAI` recognition is selectable.
- Run HOSTED-04 with explicit `AGENTCORE_LIVE_OPENAI_TTS`/`STT` plus `OPENAI_API_KEY` before claiming hosted live acceptance.
- Optional headset/Web Speech observations remain manual. Native Web Speech adapter contracts (interim/`isFinal`, pending `speechend` closure, capped idle `onend` restart, serialized TTS) are covered by Vitest; they do not replace a live headset pass.
- Browser TTS `playback.started` on enqueue (before `SpeechSynthesisUtterance.onstart`) remains a telemetry fidelity gap; heard-offset logic stays conservative.
