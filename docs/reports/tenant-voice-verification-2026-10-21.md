# Canonical tenant Voice qualification — 2026-10-21

## Scope and ownership

Canonical tenant Chat now supports the existing composed Voice pipeline through its authenticated attachment, original-human admission and immutable per-Run Runtime. This is a focused vertical module, following [bounded speech ingress](speech-ingress-lifecycle-verification-2026-10-21.md). It supersedes tenant-Voice-pending statements for the exercised paths only; it does not close the original multi-tenant proposal or claim milestone acceptance.

An Application-minted `TenantVoiceLease` pins the exact tenant, Session, executor, original Account login/Principal and speaking attachment. Its child input token rotates on unmute or input recovery. The resource owns transport continuity, provider lifetime and supervised setup/retirement; the existing Runtime mailbox remains the sole conversation/controller owner. Each Run retains its fixed `TenantRuntimeFrame` and store adapters. No mutable authority rebind, idle synthetic Run, alternate controller, new schema or public server-transcript injection endpoint is introduced. PCM remains outside the mailbox, persisted history and logs.

Positive activation waits for the active writer, obtains the host's exclusive quiescent reservation and commits mode under current original-human IAM/revision checks. Pending setup acknowledges promptly but grants no input token. Pending cancellation and timeout preserve accepted work. Input and execution share one bounded Session capacity slot, transferred between idle/pending Voice and live Runtime; all new fixtures use `MaxActiveSessions=1`.

Recognized finals reuse the existing human-input transaction with a trusted speech proof, exact utterance/input token and fresh actual-human root. The actual Memory/SQLite stores acquire the input lease operation before their own locks/transaction and repeat current login, ownership and permission checks. Client transcript evidence requires the current enabled input token and the selected client-transcript plan. Observer display access grants no microphone, playback or stream control authority.

The active Runtime evaluates speech and queues typed turn/interruption proposals for supervision outside its mailbox. Permitted barge-in calls the existing current-human cancellation service; chat authority alone cannot cancel another Run. Text mode, disconnect, lost authority and speech failure retire media without manufacturing a human cancellation of accepted model work or approvals. Mute retires recognition while retaining output/root ownership; unmute opens a fresh stream. Discontinuity/max-utterance recovery rotates input, while recognizer/output failure returns to usable Text with safe diagnostics.

Hearing is independent from display receipt credit. Exact source Run/generation, attachment, delivered speech coordinates and delivered sample bounds constrain playback. Runtime computes conservative hearing, commits a typed receipt under the active root operation and current original-human transaction, then advances counters/completion. A muted root can acknowledge already delivered output; disabled input cannot admit speech. Late superseded feedback cannot expand heard context. Orphaned durable Voice mode resets to Text before ready without reopening media.

## Exercised workflows

| Scenario and expected result | Observed result |
| --- | --- |
| Actual tenant speech through both stores admits one original-human turn, dispatches a fixed-frame Run and delivers owner-only audio. | Passed; durable entry/root/initiator identity and conservative hearing asserted. Observers cannot claim, mute, retire or acknowledge the speaker's media. |
| Mute retains playback, fresh unmute rotates input, stale boundaries/evidence cannot admit a successor turn. | Passed in integration and real MessagePack; a server-audio sequence gap additionally forces a fresh token before valid speech. |
| Chat-only barge-in queues a turn; independently authorized barge-in interrupts R1 and admits R2. | Both passed in Memory/SQLite. Half-heard R1 remains bounded after late full feedback; retiring Voice preserves accepted R2 work. |
| Revoke current authority or log out while a hearing receipt is queued. | Both stores refuse durable hearing after the owning transaction rechecks the original login/security witness. Accepted work survives and media retires. |
| Request Voice during active text; apply after writer retirement, cancel pending setup or let it time out. | All three paths passed in both stores. No pending input token/PCM; cancel/timeout do not cancel the text Run. |
| Disconnect the speaker during accepted work. | Media retires, conservative receipt limits persist, accepted work completes without a fabricated human cancellation. |
| Fail an actual recognition worker, then submit text. | Both stores emit safe failure, retire Voice, create no spurious speech Run and complete the text successor. |
| Recreate the SQLite application host with durable orphaned Voice mode. | Fresh canonical attach returns Text with null pending mode/token and creates no fake Run. |
| Cold Voice click from New Chat, active capture during playback, mute/unmute and reconnect. | Final fake-device Playwright flow passed twice at 1440 and 390 px: actual AudioWorklets, independent heard credit, truthful muted caption, retained media, fresh input and a required explicit Voice click after reconnect. No horizontal overflow, page errors or legacy authority requests were observed by its assertions. |
| Actual MCP Voice preflight fails, then the user sends text. | MCP microphone access timed out (`getUserMedia timed out`). Media remained off; the same native Chat completed a text successor at desktop/narrow widths. Successful MCP microphone/audio operation is not claimed. |

The browser runs exposed a cold-start race: the initial ready snapshot clears the preflight flag, and a SetMode acknowledgement can precede the pending-state event. The acknowledgement then released prepared media before Voice applied. The final fix restores the prepared-media hold after attachment and binds the pending intent to the exact connection/Session/attachment. Deterministic race cases cover that ordering and replacement-attachment refusal; the final four browser runs passed with passive diagnostics. Muted capture no longer presents Listening, and server-audio hearing uses speech coordinates rather than rendered-display offsets.

## Final checks

All commands ran locally with Synthetic/controlled adapters and disposable data, without hosted provider calls or credentials. Tests used `--no-build --no-restore --nologo`; builds were serialized with `--no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false`. The vstest loopback requirement used the existing authorized escalated test invocation.

| Check | Final result and local evidence |
| --- | --- |
| API test project build | Passed, zero warnings/errors; `/private/tmp/tenant-voice-build-final.log`. |
| New API filter `FullyQualifiedName~Tenant_voice_` | **25 passed**, zero failed/skipped; `/private/tmp/tenant-voice-final-module-tests.log`. Includes four real JavaScript transport cases. |
| Affected original-human voice/receipt/Chat regression group | **24 passed**; `/private/tmp/tenant-voice-authority-regression.log`. Includes eight cases also present in the 25-case group; counts are not disjoint. |
| Augmented `Tenant_voice_real_JavaScript_MessagePack_uses_owned_server_and_client_transports` filter | **4 passed** after adding discontinuity/fresh-stream assertions; Memory/SQLite × server audio/client transcript-speech. `/private/tmp/tenant-voice-wire-final.log`. These repeat four of the 25 cases, rather than adding four unique cases. |
| Application test project build | Passed, zero warnings/errors; `/private/tmp/tenant-voice-application-build.log`. |
| Nine affected speech Application classes | **85 passed**; `/private/tmp/tenant-voice-application-regression.log`. Filter: `SpeechRecognitionIngressTests`, `SpeechIngressTests`, `SpeechDuplexTests`, `SpeechPlaybackTests`, `VoiceRealtimeRegressionTests`, `SpeechTranscriptDurabilityTests`, `SpeechTelemetrySurfaceTests`, `SpeechLocaleRuntimeTests`, `ClientSpeechSegmentRuntimeTests`. |
| Existing real MessagePack regressions | **8 passed**; `/private/tmp/tenant-voice-legacy-wire.log`. Filter: `DisplayName~speech-boundary-order\|DisplayName~speech-stream-retirement\|DisplayName~parallel-controls\|FullyQualifiedName~ClientSpeechMessagePackTests`. |
| Frontend focused Vitest | **100 passed** in six files; `/private/tmp/tenant-voice-web-tests-final.log`. |
| Frontend `pnpm run build` | Passed; existing Vite chunk-size warning remains. `/private/tmp/tenant-voice-web-build-final.log`. |
| Fake-device browser | **4 passed** (two widths, repeated twice); `/private/tmp/tenant-voice-playwright-final.log`. |

Frontend command from `web/`:

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run \
  src/services/realtime.protocol.test.ts src/services/realtime.race.test.ts \
  src/services/realtime.tenant.test.ts src/services/realtime.clientTranscriptBlocked.test.ts \
  src/services/sessionRouteNavigation.test.ts src/speech/clientTranscriptLifecycle.test.ts
CI=1 NODE_OPTIONS=--no-experimental-webstorage TENANT_CHAT_API_PORT=5097 TENANT_CHAT_WEB_PORT=5187 \
  pnpm exec playwright test --config playwright.tenant-voice.config.ts --repeat-each=2
```

Node's experimental global webstorage caused misleading JSDOM localStorage failures on the first frontend attempt; the explicit Node option above resolved the environment incompatibility. The existing receipt-race wrapper also lacked forwarding for actual background completion/outcome/live-input facets after prior store evolution. It now forwards those production store owners while retaining its intended receipt/load gates. No production authority was bypassed and no failing case was disabled. The final grouped results above replace earlier intermediate failures.

## Presentation and limits

No JSX, CSS, font, token, asset or second UI kit was added. Existing Ant Design v6 dark Chat controls remain the presentation owners. The detector ran once on `web/src/services/realtime.ts` and returned `[]`. Fresh no-history Impeccable finish review returned **ship** with persistence, fidelity, ceiling, material_fixes and keep sections. It inspected all four original 1440×900/390×900 live-muted and text-recovery captures. Both live captions show Speaking rather than Listening; text recovery shows Ready/inactive Voice. The narrow muted glyph retains a green tint in its capture; the settled transition color was not independently established and no material fix was requested. Pending/error-before-recovery/permission/browser-speech states, keyboard behavior and measured contrast were outside this visual review.

Local captures and review/documentation audits are in ignored `.playwright-mcp/tenant-voice-20261021/`. The incumbent design sidecar remains byte-identical (SHA256 `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`). Documentation comparison preserves the incumbent visual system; it does not certify runtime correctness.

Physical microphone, acoustics, audible quality, live vendor speech, permanently blocked vendor open/cleanup timing, production data, multi-instance operation, exhaustive crash windows and full proposal-wide route/security/design acceptance remain unqualified. No CI/Actions or full local CI suite was requested or run. Disposable hosts were stopped; pre-existing user hosts/data were left untouched. Remaining platform guardrail families, obsolete authority/schema removal and exhaustive proposal qualification keep the original goal active.
