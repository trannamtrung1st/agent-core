# Demo Scenarios

The MVP demo should be short and interaction-focused.

## Demo 1 - Reusable identity

Run the same Agent Core with two different Agent Definitions.

### Examiner

Expected behavior:

- formal tone;
- one question at a time;
- restrained feedback;
- waits for user answers;
- may prompt after excessive silence.

### Customer support agent

Expected behavior:

- warmer and task-oriented;
- asks for missing information;
- summarizes next steps;
- can react to a simulated order-status event.

The point is to show that identity lives above the underlying provider.

## Demo 2 - Basic interruption

Agent begins a longer response.

User says:

> Wait, stop.

Expected result:

- current speech stops quickly;
- stale generated audio does not continue;
- the agent acknowledges the interruption naturally.

## Demo 3 - Semantic interruption

Agent is speaking.

User says:

> Mhm.

Expected result:

The agent continues.

Then the user says:

> Hang on, what do you mean by that?

Expected result:

- agent stops;
- controller emits an interruption event;
- agent answers the new question.

This demonstrates that the controller is not simply reacting to any microphone activity.

## Demo 4 - Proactive interaction

The examiner asks a question and the user remains silent.

After an appropriate threshold the controller emits an idle event.

The Agent Runtime may choose to say:

> Would you like a little more time, or should I repeat the question?

The exact wording should come from the agent identity, not from the controller.

## Demo 5 - Environment event

While speaking with the support agent, use the in-process synthetic environment driver through `IEnvironmentEventIngress` to submit kind `order_status_changed`, orderReference `DEMO-001`, and status `shipped`. [Event Model](07-event-model.md) defines this application input; it is not an arbitrary public wire event.

Expected result:

The Agent Runtime decides whether the event is important enough to mention and, if so, informs the user naturally.

## Demo 6 - Provider swap

Run the same conversation tests with:

```text
ScriptedLanguageModel
```

and then:

```text
OpenAICompatibleLanguageModel
```

Configure OpenAICompatibleLanguageModel first for OpenRouter hosted text, then a compatible local endpoint fixture or available local server. STT/TTS remain independently selected. No Agent Runtime or Interaction Controller code should change. A local server is optional for this demonstration; offline fixtures prove the boundary without a GPU.

This demonstrates that provider abstraction is real rather than cosmetic.

## Demo 7 - Cancellation correctness

Simulate a slow model stream.

Sequence:

```text
response A starts
user interrupts
response A is superseded
response B starts
late chunk from A arrives
```

Expected result:

The late chunk from A is discarded and never reaches the user.

This proves the runtime handles concurrency rather than only the happy path.

## Demo 8 - Reconnect, mode switch and heard context

Have a text exchange, start voice on the **same** session (preflight then `session.mode.set`), interrupt a long voice response after its first phrase, disconnect and reconnect. Interrupted text remains visibly marked, audio does not replay, and the next answer uses only the conservative heard prefix for that voice-delivered entry. A voice request that was still pending when the connection died does not resume; press Voice again. Return to text mode without creating a new session. Repeat after restarting the backend with SQLite enabled.

## Synthetic 20-turn measurement

Run the same identities through twenty synthetic text turns, then a full-duplex voice exchange, an interruption, restrained idle or environment initiative, and detach/reattach. Record per-stage `AgentCore.Runtime` durations as observed numbers (not SLAs). Distinguish generated, received, and heard text on assistant entries. The automated path is `TwentyTurnDemoTests`; a headset pass remains optional and key-gated.

## Demo preparation and capability caveat

Use Synthetic first to verify scenarios without credentials; scripted STT supplies transcript content and synthetic TTS emits tones/silence for transport checks. Real/demo text uses OpenRouter with a **fixed** operator-selected default model ID when `OPENROUTER_API_KEY` is supplied; `openrouter/free` is for adapter smoke and an explicit catalog choice, not the system default. Live OpenAI realtime transcription STT (recommended `gpt-live-transcribe`) and TTS may wait for `OPENAI_API_KEY`. Native speech-to-speech is excluded. Semantic backchannel demonstration requires partial-capable STT; batch STT intentionally uses speech-activity/final-transcript interruption and may interrupt on acknowledgements. Show that trade-off honestly in the developer panel.

Measure the interruption and playback timeline using [Operations](17-observability-and-operations.md). [Testing Strategy](16-testing-strategy.md) turns these scenarios into deterministic regression tests. Do not substitute a live-provider phrasing assertion for a synthetic correctness test.

## Demo 9 - Streaming speech and continuous listening

Use a long scripted answer. Confirm that a natural first Speech Segment starts playback before the LLM finishes, and that STT continues receiving input during that playback. Show “okay” as a backchannel, then “wait” as a confirmed interruption. Assert that pending R1 segments never start and late playback events cannot alter R2. Repeat in hardware-free Synthetic Mode with simulated sample counters.

## Demo success feeling

The audience should come away thinking:

> This feels like talking to an agent that is present and listening, not sending prompts to a chatbot.

That perception is the actual MVP objective.

## P5 schedule management (historical freeze evidence)

The following P5/P6 and P9.5/P9.6 records describe their original versions. Current Automation, credential and Background Session behavior is demonstrated in [Prepared Secretary](#prepared-secretary-with-system-credentials) and the [Background Session cutover journey](#background-session-cutover-journey).

On Riley (`general-assistant` v8–v10) with a trusted profile timezone when wall-clock scheduling is used, “remind me tomorrow” persists one owner-scoped one-shot schedule without an approval dialog. Relative delays such as “after 1 minute” do not require a profile timezone. Learned cross-session memory is enabled on `general-assistant` v9+ (not v8). An admitted agent proposal is recalled for the same Agent Instance and trusted profile in a new session. A remember phrase by itself is not the write. Sub-minute fixed-interval recurrence (“every minute”, not “every 30s”) is enabled on `general-assistant` v10 (not v8–v9). The chat header **Schedules** drawer lists intent, schedule, timezone, status, and next occurrence, and cancels the active row after confirmation. A second session for the same instance and profile sees the same row. Customer support does not offer user scheduling. There is no calendar and no admin trigger console.

## P6 background work (historical freeze evidence)

Ending the chat before a one-shot reminder fires still completes that reminder as one Background Work item. The result is in the Background Work drawer and is not a new transcript turn. An approval-demo application event can wait in that drawer across an API restart and then run the exact approved action once. Cancelling a retrying item stays cancelled after restart. Phase I continuation of Support, Compliance, and `sandbox.run` after deactivation is still not this behavior. P6 frozen on `30adaeb` (workflow `36085265506` green).

## P7 Admin harness lifecycle (observed)

With trusted-local owner capability in Synthetic mode, an operator can fork a built-in definition, edit instructions/capabilities/resources, validate/evaluate/diff/publish, create an Agent Instance, edit persona through Form and JSON, start User chat by instance id, inspect and reset scoped memory, revoke an eligible schedule, publish v2, upgrade and rollback the instance, deprecate a publication, archive the instance, and inspect redacted Admin history—without rewriting pinned session snapshots. Slice browser gates include `e2e/admin-shell.spec.ts`, slice journeys under `e2e/z-admin-*.spec.ts`, and the whole-phase `e2e/admin-lifecycle.spec.ts` (`admin-lifecycle` Playwright project on disposable `PLAYWRIGHT_SQLITE_PATH`). W07 closure: `ae83bfc` (review 0139). See [P7G report](reports/p7g-history-rollback-final-gate.md).

## P9 visible browser (observed)

Riley on the shipped `general-assistant` v11, with the scripted Synthetic model, accepts a user message that contains `record AC-1042`. The first browser generation navigates the trusted fixture start URL and does not send a chat message. The turn then loads `browser.record.lookup`, sends one application message (`I found the record. I'm checking the details now.`), and finishes with one assistant answer (`AC-1042 is In review.`). While a `browser.*` tool runs, the existing activity row shows `Using browser…`. The journey does not add a page panel, screenshot, or click log. Browser evidence is `e2e/p9-browser-journey.spec.ts` on the `synthetic` Playwright project. Closure evidence, including the headed demo, is [p9-freeze-candidate.md](reports/p9-freeze-candidate.md).

## P9.5 secretary connection and attention (historical closure evidence)

An Agent Instance shows one application connection on Admin. Admin names nopCommerce as the supported application type. Chat does not show connection status or a manage link; operators use Admin for connect, reauthenticate, and revoke. Background Work labels a completed attention result with the words Needs attention and leaves a quiet completion unlabeled. The local nopCommerce store is `scripts/nopcommerce-demo.sh`. Earlier real headed Journey A attempts, including on the interactive browser budget, did not establish the storefront postcondition. Session `fcd46bb2-d1fb-4ea1-9560-4b39b0873281` later published AC Keyboard at `$99` with the image. A live Journey C attention result observed the connected store. Journey E wrote a workspace note and completed a quiet reminder. Journey D is accepted on a recovery result: the mutating work item failed `tool-result-lost`, then a later one-shot re-observed AC Keyboard at `$89` and stored the owner-facing result. P9.5 is closed on `1012653`. Hosted Synthetic [`37111979501`](https://github.com/trannamtrung1st/agent-core/actions/runs/37111979501) is green. See [p9.5-freeze-candidate.md](reports/p9.5-freeze-candidate.md).

## P9.6 secretary modes (historical closure evidence)

`agents/secretary-v2.json` is the Secretary revision used for these modes. `secretary` v1 is unchanged. One Agent Instance on version 2, one persistent profile, and that definition version cover four Synthetic paths. Interactive work navigates and observes the store without taking a screenshot. A later visual check captures once, and only a vision-capable model receives the image; a text-only capture fails `model-capability-unsupported`. A scheduled review completes quietly when nothing needs attention. An authenticated `order.placed` event wakes the same instance, inspects the store, and can complete with attention. Background Work shows Scheduled reminder or Order placed, the updated time in the viewer locale, and does not render the webhook body. Admin sets the unattended model. Event source credentials are created on Admin home and shown once. An instance subscribes to `order.placed` separately from its application connection. The nopCommerce plugin under `deploy/nopcommerce/plugin/AgentCore.OrderEvents` is the storefront emitter (`NopStartup` registers `OrderPlacedConsumer`). Guest checkout **order 14** on `http://127.0.0.1:5088` emitted `order-14-placed` automatically (no `emit-order-placed.sh`); the host admitted through the source-owned webhook, fanned out through `ExternalEventDelivery`, and created one durable WorkItem; duplicate replay stayed at one row. An earlier opt-in run admitted `order-12-placed` manually and completed a real-model unattended WorkItem after browser inspection of the connected admin store via a separate admitted event. Details are in [p9.6-freeze-candidate.md](reports/p9.6-freeze-candidate.md). Those runs are not Synthetic CI. P9.6 is **closed** on `d033bc61`. P9 stays frozen on `bba1de4` and P9.5 stays closed on `1012653`.

## Post-MVP planned until verified

Observed later demos: durable multi-chat Support and Compliance flows with attachments, bounded work, artifacts, rich presentation, deactivation, and reopen (`SupportComplianceWorkflowTests`). Docker `sandbox.run` is a runtime capability, not a separate UI demo. Examiner MVP conversational demos above remain.

## P9.7 — Teach an operations assistant in Chat

Create a managed General Assistant v17 instance in Admin, choose Managed with Knowledge & resources, then return to Chat. Say `Learn this order policy for future conversations: https://example.test/p97/order-policy`. Synthetic reads the exact deterministic public fixture through ordinary `web.fetch`, retains provenance and reports the verified durable result. No source-list/preparation/publication controls are required. Start a new Chat with the same instance and ask `What is the learned order policy?`; actual published knowledge retrieval returns payment/shipping/fraud review.

For Instance Skill learning, the general-purpose v16 instance already authorizes ordinary Skill management, independently of Harness mode. Say `Learn this accounting Skill.`, then `Use my accounting Skill.` in the same Session. Synthetic creates the local Accounting procedure and the next execution loads its pinned canonical key. Admin → instance → Skills shows the new local Skill, supports editing/disable/delete, and Customize copies a reusable Definition procedure while disabling its source. These procedures never grant authority or alter the Definition version.

In Assisted mode, reject a knowledge proposal once (active version unchanged), then approve a fresh exact proposal in Chat. In Managed Tool proposals scope, `Propose disabling http.request for future conversations.` still requires approval. In the still-pinned current Session, `Try a sensitive HTTP action now.` still requires its own normal approval; reject it. Managed instruction proposals also await approval.

Freeze in Admin and return to Chat: durable learning is unavailable, while `Hello` still works. For recovery, change policy revision while a Chat approval waits; the old approval fails without adoption, and a fresh request succeeds. Inspect recent change/evidence in Admin. See [new executed journeys](reports/p9.7-chat-first-freeze-candidate.md).

Historical Real Kubernetes evidence (superseded for Skill ownership): select the existing **DeepSeek V4.1 Flash Reasoning** model for each session. Ask naturally to learn supplied materials and create reusable knowledge/Skills, start a new session to use them, request only a context/namespace-first refinement, and verify it in another session. The [final verification report](reports/p9.7-final-verification.md) records observed creation, activation, partial update and missing-Knowledge recovery. GPT-4o mini now accepts the corrected full tool payload, but this bounded scenario exposed unreliable Knowledge creation, Skill discovery and procedure preservation; it is not the verified model for this demo. Core validation and authority still apply with either model; model choice is not a grant.

## P9.8-P9.9 continuity demo

Use Synthetic and disposable SQLite data. Create a managed `general-assistant` v16 instance with a distinct persona; this version authorizes ordinary Skill management and existing schedule admission. Enable Experience in the instance detail. Create a Chat with that instance, send `synthetic-fail-turn`, observe the failed result, then send `A correction: observe the current page before acting.` and wait for the completed reply.

Pause the completed Chat (or end it) to exercise automatic checkpoint admission, then open the instance's Experience panel. Enter that Chat's Session id and choose Retrospect now. Wait for completed structured observations, including failure/correction evidence and source/Definition/model provenance. Repeat the request: the same checkpoint remains one logical record. Start another Session of the same instance and send `Use my recent experience before acting.` The Synthetic answer commits to observing current page state under current policy; another instance receives no Experience. The integration composition fixture seeds two checkpoints with the same harmless friction before activation.

In Automation → Automations, create “Review recent experience” with Schedule timing and `synthetic-automation-improve: review recent experience and improve only when useful. Otherwise do nothing.` Use Run now and inspect the normal Run details. The authorized Automation creates an independent Instance Skill through ordinary capability tools; verify it under Instance → Skills. Harness mode and approval do not govern this Skill write. Enable Experience and supply a manual reviewed source first.

Run the same Automation again: successful No action creates no owner alert. `synthetic-automation-attention` exercises ordinary AttentionRequested. For Event reaction, create an Event Source under Connections and an Automation with order.placed and configured instructions; duplicate envelopes create one Run per matching Automation. Event data is untrusted evidence.

These marker prompts are deterministic Synthetic fixtures using production contracts, not claims about hosted model judgement or actual external store improvement. Hosted smoke uses a normal unmarked thinking prompt, explicit provider opt-in and operator credentials. [Acceptance/evidence](reports/p9.8-p9.9-freeze-candidate.md).


## Prepared Secretary with System Credentials

Secretary v4 preserves earlier immutable revisions and adds `store.signin`: the local store location is knowledge in the Skill (`http://127.0.0.1:5088/Admin`), not a Core application record. Start nopCommerce with the existing demo bootstrap. In Admin → Credentials explicitly create `nopCommerce Admin` Kind Password, metadata `username`/`application=nopCommerce`, exact AllowedOrigin matching the store, and its protected value. Do not automatically import the bootstrap `.env` password. Bind to Morgan as `store-admin` in instance Credentials.

Ask Morgan to inspect store orders. Normal Skill or capabilities.load projects authorized Browser; credentials.list supplies safe username metadata, and fill_credential uses the observed password ref plus store-admin internally. When requesting reuse across Chats/restart, use the site-offered Remember me control and verify its checked state before submitting; cookie lifetime remains site-controlled. Observe authenticated store state. End Chat, start another with the same owner, then restart Agent Core and verify authenticated profile reuse. Bind the same resource to a second owner under another alias, rotate once and verify both grants. Unbind one owner and retain the other's access. Reset/expire authentication; detached work must require attention instead of decrypting a password. Credential rotation/unbind does not log out a browser.

P9.5/P9.6 scenarios above are historical evidence at their recorded freeze SHAs. Their Application Connection setup is retired. Current acceptance evidence and outstanding gates are recorded in [System Credentials verification](reports/system-credentials-verification.md).

## Background Session cutover journey

With an eligible Synthetic parent policy, submit `[test:background-start]`. The parent reports the committed start promptly; a child Session runs independently and reports its first completion once. Open Background Work, inspect history and Continue in chat: the chat URL must retain the child Session ID. Send `Check B too`; a new user Run completes in that child, while the parent retains one completion report. The shipped General Assistant disables initiative by default, so its parent report is safely skipped unless the parent policy is explicitly enabled. Authored Automation creates separate background Sessions and does not report automatically to its authoring chat.

Background Work shows Session rows, readable status/progress and unread attention. History exposes safe run outcomes, exact approval previews, cancellation and bounded artifacts. Quiet NoAction remains inspectable without an assistant bubble. Continue closes the drawer after the same Session connects. Admin Runs uses AgentRun identity and links back to Automation or Experience. Shared Ant Design reading regions, cursor paging, focus return and responsive drawer sizes remain consistent with the current design system.
