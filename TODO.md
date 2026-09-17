# TODO

Ordered roughly by dependency and product value. Earlier sections should normally be completed before later platform work.

## Maintainer notes
* [ ] Should we pause auto or manual pause session? in the future we may have background work.
* [ ] Test the extra md content along with message/reply/voice response.
* [ ] In admin mode later, agent just prepare the intent/payload and show in UI, its user to confirm/approve and execute the action.
* [ ] Fail status in chat is too generic, cannot know which is the cause. we may have some tooltip or popup to show the cause.

## P0 — Conversation lifecycle and natural interaction

* [ ] Let the agent choose the next wait instead of using one fixed silence interval.

  * Brain may suggest short/medium/long or a duration.
  * Runtime owns the timer and clamps it to configured min/max bounds.
  * Waiting must remain interruptible by user/environment events.

* [ ] Define inactivity/session timeout behavior.

  * Decide when inactivity causes another proactive turn, pause/deactivate, or terminal end.
  * Keep `deactivate/pause` distinct from `end`.
  * Add clear UI state when the agent pauses or ends because of inactivity.
  * Show the reason/status rather than silently disappearing.

* [ ] Add queued user messages.

  * Allow a user to send a message without immediately interrupting the current agent response.
  * Preserve the existing explicit interruption/barge-in behavior separately.
  * Define ordering when multiple queued messages arrive: the trailing durable user suffix is one next assistant response.

* [ ] Finish rich turn presentation semantics.

  * Keep display text/Markdown separate from optional speech text.
  * Define how extra blocks, artifact references, tool results, status/progress, and voice-only/display-only content appear in the conversation.
  * Keep accessibility and reconnect/history behavior consistent.

## P1 — Cheap, interchangeable real voice

* [ ] Formalize speech implementations behind the existing STT/TTS interfaces.

  * Synthetic provider.
  * Browser provider.
  * OpenAI/hosted provider.
  * Future local/provider implementations.

* [ ] Add browser STT for the inexpensive development/demo path.

  * Stream partial recognition results to the backend while the user continues speaking.
  * Send normalized partial/final events through the same backend speech contract.
  * Keep provider-specific browser behavior out of Session Runtime.

* [ ] Add browser TTS.

  * Backend sends speech segments/text.
  * Browser performs synthesis.
  * Browser reports start/progress/completion/interruption where possible.
  * Preserve conservative spoken/heard semantics.

* [ ] Add explicit speech-provider selection/capability discovery.

  * `Synthetic`
  * `Browser`
  * `Hosted`
  * future local provider
  * STT and TTS must remain independently replaceable.

* [ ] Verify Real-mode voice end to end with at least one actual non-synthetic configuration.

## P2 — Assistant tool capability

* [ ] Formalize the tool architecture as:

  * Tool Registry: what capabilities exist.
  * Policy/Authorization: whether this agent/session/user may call them.
  * Executor: performs the operation.
  * Result/Artifact handling: returns bounded structured output.

* [ ] Keep common operations as typed first-class tools instead of forcing everything through shell.

* [ ] Add public web tools.

  * `web.search`
  * `web.fetch`
  * bounded response sizes/timeouts
  * safe URL/network policy

* [ ] Add/standardize workspace tools.

  * `workspace.list`
  * `workspace.read`
  * `workspace.write`
  * `workspace.patch`
  * enforce session workspace boundaries

* [ ] Add/standardize artifact tools.

  * create artifact
  * inspect metadata
  * expose/share artifact with the user
  * preserve provenance/hash information

* [ ] Continue using `sandbox.run` as the generic execution escape hatch.

  * No unrestricted host shell/process tool.
  * `/workspace` writable.
  * agent/attachment inputs read-only where appropriate.
  * bounded CPU/memory/PID/time/output.
  * network disabled by default.

* [ ] Add sandbox network policy only when required.

  * `none`
  * restricted web access
  * explicit host allowlist
  * never unrestricted by default

* [ ] Prefer typed domain actions over generic HTTP mutations.

  * Example: `github.create_issue`, `calendar.create_event`, `support.update_ticket`.
  * Tool implementation owns credentials.
  * Credentials must never be placed into model context.

* [ ] If generic `http.request` is eventually added, constrain it.

  * allowed hosts
  * allowed methods
  * request/response limits
  * timeout
  * named credential aliases
  * policy/approval checks

* [ ] Add approval policy for higher-risk tool actions.

  * automatic safe/read-only actions
  * configurable write actions
  * explicit approval for sensitive/destructive operations

## P3 — Context compaction and memory

* [ ] Add LLM-based session compaction.

  * Keep deterministic fallback.
  * Preserve important unresolved topics, decisions and references.
  * Never replace the durable raw history.

* [ ] Introduce structured session memory.

  * facts
  * preferences
  * goals
  * decisions
  * open loops/tasks
  * provenance/source
  * confidence/freshness

* [ ] Add memory tools.

  * `memory.search`
  * `memory.get`
  * controlled `memory.write/update`
  * keep policy separate from storage

* [ ] Add cross-session memory only after session memory works reliably.

  * opt-in/configurable scope
  * agent/user ownership rules
  * provenance and correction/deletion semantics

* [ ] Do not require a vector database initially.

  * Add embeddings/vector retrieval only when measured retrieval needs justify it.

## P4 — Events and configurable triggers

* [ ] Expand the event/trigger model beyond current idle/environment triggers.

* [ ] Define a generic trigger contract.

  * trigger type
  * payload
  * source
  * timestamp
  * dedupe/idempotency key
  * expiry
  * target agent/session/work item

* [ ] Add useful trigger sources.

  * scheduled time
  * recurring schedule
  * webhook/external event
  * application/domain event
  * environment update
  * session inactivity

* [ ] Make allowed triggers configurable by agent definition/admin.

* [ ] Later expose safe trigger configuration to users.

* [ ] Add UI for trigger/scheduled-work visibility where appropriate.

## P5 — Durable background work

* [ ] Introduce durable `WorkItem` only when work must outlive the active Session Runtime.

  * Do not duplicate normal synchronous tool execution.
  * Persist execution state/checkpoints.
  * Support cancellation and idempotency.
  * Guard against stale session epochs.

* [ ] Allow work to continue after session deactivation when explicitly intended.

* [ ] Allow a paused/reopened session to reconnect to existing work without repeating the original user turn.

* [ ] Add background research.

* [ ] Add scheduled tasks.

* [ ] Add long-running sandbox jobs where useful.

* [ ] Stream/store progress as structured events rather than fake assistant messages.

* [ ] Deliver completed work back into the appropriate session as a result/event/artifact.

## P6 — Agent harness / admin mode

* [ ] Treat agent settings as part of the agent's harness/context when appropriate.

  * Agent should be able to inspect relevant effective configuration.
  * Never expose secrets.

* [ ] Separate Admin mode from User mode.

* [ ] Admin mode:

  * agent can inspect and modify its harness workspace
  * edit instructions/configuration
  * edit tools/plugins configuration
  * edit knowledge/assets
  * run validation/tests
  * preview behavior

* [ ] User mode:

  * uses an immutable/pinned published agent version
  * gets its own isolated session workspace
  * cannot mutate the source harness

* [ ] Define harness publishing lifecycle.

  * draft
  * validate
  * test/evaluate
  * publish immutable version
  * rollback/deprecate

* [ ] Allow the admin agent to help build/refactor/improve its own harness, but only through the same bounded tool/policy system.

## P7 — Full harness/platform capabilities

* [ ] Consolidate the full harness model:

  * identity/instructions
  * runtime configuration
  * tools
  * policies/permissions
  * memory
  * knowledge
  * triggers
  * workspace template
  * validation/evals
  * plugins/extensions

* [ ] Add plugin/tool-provider extensibility.

  * MCP-like external tool providers are a possible adapter.
  * Native Agent Core tools remain supported.
  * Plugins must still pass through Agent Core policy/authorization.

* [ ] Add reusable harness validation.

  * schema validation
  * missing tools/providers
  * invalid permissions
  * incompatible model capabilities
  * unsafe configuration
  * test scenarios/evals

## P8 — Sandbox evolution

* [ ] Keep Docker sandbox as the current implementation while it meets requirements.

* [ ] Introduce `ISandboxProvider` only when a second implementation is actually needed.

* [ ] Evaluate OpenSandbox when requirements include:

  * remote execution
  * stronger multi-tenant isolation
  * sandbox pools
  * faster provisioning
  * distributed workers
  * multiple runtime images

* [ ] Keep the model-facing `sandbox.run` contract stable when changing providers.

* [ ] Consider Kubernetes only when deployment/scaling requirements justify it.

  * Do not use Kubernetes merely to replace the current working sandbox.

## P9 — Multi-user/product infrastructure

* [ ] Authentication.

* [ ] User/admin authorization and tenancy.

* [ ] Per-user resource quotas and ownership.

* [ ] Secrets/credential management for external integrations.

* [ ] Audit history for privileged tool/actions.

* [ ] Public-hosting hardening.

* [ ] Horizontal/distributed Session Runtime only when single-process ownership becomes an actual constraint.

## Continuous quality work

* [ ] Keep Synthetic/offline tests as the default deterministic verification path.

* [ ] Add regression tests alongside every new lifecycle/tool/memory/background-work feature.

* [ ] Maintain browser/Playwright coverage for user-visible workflows.

* [ ] Keep hosted-provider tests explicit opt-in.

* [ ] Periodically run real OpenRouter/OpenAI/browser-speech smoke tests.

* [ ] Maintain observability for:

  * model calls
  * tool calls
  * sandbox execution
  * trigger decisions
  * background work
  * speech pipeline
  * policy denials
  * resource limits

## Already implemented / baseline

* [x] Persistent multi-session catalog/history.
* [x] Session reopen/read-only ended history.
* [x] Attachments and later-turn attachment recall.
* [x] Markdown/rich responses and artifact references.
* [x] Repeated proactive initiative and deactivation.
* [x] Versioned role environments.
* [x] Session-owned workspaces and artifacts.
* [x] Bounded typed tools and multi-step tool execution.
* [x] Docker-backed `sandbox.run` with isolation/resource limits.
* [x] Synthetic STT/TTS and full-duplex voice pipeline.
* [x] Interruption/barge-in and conservative spoken-until handling.
