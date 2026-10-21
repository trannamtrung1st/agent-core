# Authenticated canonical Hub cutover — 2026-10-21

## Delivered boundary

`/hubs/session` now requires the authenticated Account cookie for negotiation, transport connection and every public method. `SessionHub` forwards only to `TenantSessionLiveDelivery` and its exact original-human tenant attachment. It no longer injects or dispatches to the owner `SessionHost`, reads owner headers, accepts owner attach payloads, or chooses another registry when a tenant attachment is missing. The retired `Attach`, `RespondApproval` and `EndSession` RPCs are absent. Current lifecycle and durable Approval APIs remain their independent owners.

The closed public method set is: `AttachTenant`, `SendText`, `CancelResponse`, `SetMode`, `SpeechStarted`, `SpeechEnded`, `SpeechEvidence`, `SendAudio`, `PlaybackStarted`, `PlaybackProgress`, `PlaybackCompleted`, `PlaybackStopped`, `ResponseReceived`, `SetMuted`. Negotiation/upgrade refusal is separate from the current original-login/IAM/Session/recipient checks repeated inside canonical use cases. Authentication alone grants no tenant attachment, Run authority or microphone ownership. Extra actor/token fields in an otherwise authorized attach cannot replace the cookie-derived human.

Stateless envelope validation moved from the old host into `SessionCommandValidation`; both remaining internal callers and canonical transport use that same validation, without an authority adapter. Null envelopes receive a safe fatal refusal. Fatal attach validation now follows the same acknowledgement/connection-close path as other controls. Raw audio remains outside the domain mailbox. The existing per-Run immutable frame, current cancellation checks, durable input/Approval owners and conservative hearing transaction are preserved.

Together with [HTTP retirement](http-authority-cutover-verification-2026-10-21.md), this closes public owner-token network dispatch. It does **not** remove the remaining browser token code, internal legacy host/approval/store paths, old schema or all former feature gaps. The original full proposal goal remains active.

## Executed scenarios

| Action and expected result | Observed |
| --- | --- |
| Anonymous and internally minted, still-valid old-token clients negotiate or open WebSockets. | Both receive negotiation401 and fail upgrade; no tenant events/work are created, in Memory/SQLite and MessagePack/JSON. |
| An authenticated client sends all twelve acknowledgement-bearing control families before tenant attach, plus raw audio. | Every control refuses StaleAttachment; raw audio creates no work. Session revision/mode/history remain unchanged. No owner fallback is selected. |
| Invoke Attach/live-approval/End before and after a valid tenant attach. | All three methods are absent. The same authorized connection remains able to submit canonical work afterward. |
| Submit wrong protocol/type/attach sequence, caller-supplied correlation/causation, invalid UUID or null attach envelope. | Safe fatal acknowledgement, no events/work and connection closure; version mismatch lists supported version1. |
| Attach with correct tenant/cookie plus forged actor/token fields, submit text, retry it. | Saved Run retains the actual original human, exactly one user/assistant pair and the original Run on retry. |
| Existing authenticated text reconnect/switch/logout, Voice ownership/receipts/barge-in/mute/failure/reopen and independent durable Approval recovery. | All 43 selected backend cases pass. HTTP retirement/retention guard also remains passing. |
| Fake-device native Voice capture/output, mute/fresh unmute, disconnect/reconnect and explicit fresh Voice click. | Final desktop1440/narrow390 flow passes twice at each width; no page errors, legacy HTTP requests or overflow assertions fail. |

The [retirement inventory](../../tests/AgentCore.Api.Tests/Fixtures/retired-hub-tests.json) records three obsolete owner-Hub test declarations removed and one optional live test's obsolete final live-approval subsection removed. Its independent direct maintenance checks remain under a corrected name; that optional live-provider test was not run. No skipped Synthetic case or compatibility Hub was introduced. Existing canonical live/Voice/Approval fixtures remain.

## Browser failure and correction

The first browser run reached Ready and successfully resumed Voice, but the narrow case recorded an unhandled `Invocation canceled due to the underlying connection being closed` error during deliberate disconnect. Playback feedback was already fire-and-forget, while its async wrapper could still reject into the page. The shared `sendPlayback` now settles lost feedback without inferring local credit, retry, replay or user-state mutation; the owning server transaction decides durable hearing even if its acknowledgement is lost. A deterministic tenant service case holds the old invocation, replaces its attachment, rejects the old feedback and verifies an independent new feedback call. Display receipts retain their separate owner.

The next repeated browser run had three passes and one fixture failure: the hearing poll asserted immediate success on a history409 while a receipt transaction changed its coherent read witness. The poll now retries only that read conflict; authentication and other errors still fail, and positive durable heard credit is still required. No mutation is retried. Final four-case repetition passes. These corrections do not weaken the Hub or receipt authority.

Independent Playwright MCP used a separate disposable Synthetic API5096/Vite5186. Visible native sign-in and organization/Chat navigation succeeded. Voice preflight timed out (`getUserMedia timed out`), left media off and returned Ready; successful MCP microphone/playback is not claimed. The same Chat completed a text reply, explicitly disconnected/reconnected through the existing debug transport hooks, retained the same Session with Voice off/media unprepared, then completed a second text reply on the fresh attachment. The captured page-error/unhandled-rejection list stayed empty. Native End confirmation issued canonical `POST .../sessions/{id}/complete`200 and projected Completed with the conversation retained. One retired GET was aborted during scope remount. A harness wait initially looked for Ended; the actual terminal contract observed was Completed. Local MCP facts are saved under ignored `.playwright-mcp/hub-cutover-20261021/`.

## Final local checks

| Check | Result |
| --- | --- |
| API test project build | Passed, zero warnings/errors,50.11s; `/private/tmp/hub-cutover-build.log`. |
| New closed-method/reflection and actual network group | **5 passed** (four store/protocol combinations plus method contract),22.28s; `/private/tmp/hub-cutover-qualification.log`. |
| Affected canonical backend regression group | **43 passed**, zero failed/skipped,3.03min; `/private/tmp/hub-cutover-regression.log`. |
| Frontend focused Vitest | **101 passed** in six files,4.43s; `/private/tmp/hub-cutover-web-tests.log`. |
| Frontend build | Passed,10.90s; existing chunk-size warning remains. `/private/tmp/hub-cutover-web-build.log`. |
| Final fake-device browser repetition | **4 passed**,23.1s; `/private/tmp/hub-cutover-browser-final.log`. |

Exact commands (backend from the repository root):

```sh
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~Tenant_Hub_ --logger 'console;verbosity=normal'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~Tenant_voice_|FullyQualifiedName~Tenant_live_reconnect_and_authority|FullyQualifiedName~Original_conversation_receives|FullyQualifiedName~Tenant_HTTP_cutover' --logger 'console;verbosity=normal'
```

From `web/`:

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run src/services/realtime.tenant.test.ts src/services/realtime.race.test.ts src/services/realtime.protocol.test.ts src/services/realtime.clientTranscriptBlocked.test.ts src/services/sessionRouteNavigation.test.ts src/speech/clientTranscriptLifecycle.test.ts
NODE_OPTIONS=--no-experimental-webstorage pnpm run build
CI=1 NODE_OPTIONS=--no-experimental-webstorage TENANT_CHAT_API_PORT=5097 TENANT_CHAT_WEB_PORT=5187 pnpm exec playwright test --config playwright.tenant-voice.config.ts --repeat-each=2
```

All providers are Synthetic/controlled, and the new actual-network cases assert zero outbound provider HTTP. No live keys, OAuth, production data/reset, external CI/Actions or full local CI suite was used. `CI=1` only isolates local Playwright servers. Own hosts were stopped; pre-existing user hosts/data were left untouched.

No JSX, CSS, theme, font, asset, visual system or UI kit changed. This behavior correction requires no new visual-world acceptance or detector pass. The existing design sidecar remains byte-identical (SHA256 `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`). Physical audio, live providers, whole-surface accessibility/design, all internal/schema removal, full feature parity and exhaustive proposal qualification remain open. No milestone or full-goal acceptance is claimed.
