# Automation destinations and reliable background report-back verification

**Baseline:** `main` at `cd27e073bc7b595250b2c113a3122efef11b4348`, 2026-10-08.  
**Scope:** The authorized Automation destination/reporting enhancement, phases A–G. Existing AgentRun and Browser v2 freeze reports remain historical.  
**Closure owner:** [TODO enhancement closure](../../TODO.md#automation-destinations-and-background-report-back-closure) records the exact final behavior SHA, hosted workflow URL and five-job outcome after acceptance. This avoids a self-referential candidate hash in its own committed report. No prior green SHA verifies this enhancement.

## Implemented behavior

An Automation separates Schedule/Event trigger, exact execution destination and optional completion reporting. ExistingSession admits a zero-user-entry Activation/AgentRun into the owned conversation, including headless resumable execution. BackgroundSession admits a new child. Occurrences freeze destination/delivery; authorship provenance and connected-tab count never choose them. Manual invocation follows configuration. Native source-owned compatible-live routing remains unchanged.

Each Run freezes ConversationResponse, BackgroundOutcome or CompletionReport. Existing-target jobs use the Session model; background jobs use the unattended selection. Explicit Tools/Vision requirements and conflicting model overrides are validated. Missing/user-paused/ended/archived/deleted targets reject visibly, recurring registrations suspend, and no fallback child is created. An unchanged unavailable destination can still be disabled; re-enabling/changing it requires eligibility.

One SessionRuntime mailbox and shared AgentRun store retain execution ownership. Both stores serialize active Session claims, preserve the current transcript/revision on zero-entry admission, deduplicate receipts and enforce a bounded existing-target queue. Runnable batches choose one eligible Run per Session; expired active claims remain recoverable even behind older queued work. Unstarted mailbox refusal defers five seconds without consuming a provider attempt. Checkpoints, approval and uncertain-effect fencing remain intact.

Immediate and Automation initial-child terminal outcomes share reporting. Requested meaningful reporting works with General Assistant v17 Initiative disabled. Opt-out and NoAction remain quiet. Current source/parent owner, policy, lifecycle and model are rechecked. Report requests offer no tools and treat child contents as untrusted evidence. A unique receipt admits at most one parent Run per initial child; continuation Runs cannot repeat the callback or starve the bounded pending-report query. Delivery distinguishes notRequested, pending, admitted, delivered, failed and skipped; delivered requires successful parent assistant output.

Chat tools require currentSession/backgroundSession and Core binds IDs; foreign Session/owner arguments fail. Admin saves canonical target/delivery shapes using its owned eligible picker. Shared Ant Design presentation separates Destination, Reports to and Originally created from. Chat links to the exact Automation editor; completion messages carry a modest owned child link; Background Work retains truthful quiet/unavailable status.

## Acceptance mapping

| Journey | Executable evidence |
| --- | --- |
| J01 exact 30-second greeting | `AutomationDestinationJourneyTests.Greeting_due_in_exact_conversation_survives_headless_dispatch_and_restart`; natural Chat Playwright destination journey; Core relative-time tool calculation |
| J02 detached/reconstructed same Session | Greeting theory detached transport pause, SQLite reopen/replay and untouched unrelated Session; Playwright navigation before due, history return/reload; existing headless runtime/audio tests |
| J03 busy target | `Exact_target_admission_preserves_transcript_replays_receipt_and_serializes_same_session`, coordinator refusal deferral and expired-active/older-queue regression; existing mailbox durability tests |
| J04 unavailable lifecycle | One-shot/recurring API theory for user pause, ended, archived and durably deleted; visible rejection/suspension, no child, unchanged destination disable |
| J05 recurring conversation | `Recurring_conversation_occurrences_keep_one_response_each_in_the_exact_session`; shared scheduler coalescing/timezone/DST/restart tests |
| J06 immediate reporting, Initiative off | `AgentRunDurabilityTests` enabled-report theory; default General Assistant browser journey and MCP creation/report |
| J07 immediate opt-out | Same runtime theory with reportCompletion=false, completed child retained and no pending/report Run |
| J08 scheduled explicit callback | Recurring API reportBack=true, one child/report per occurrence; Admin selected-report picker browser journey |
| J09 quiet recurring background | Recurring API reportBack=false, separate child results and zero parent messages |
| J10 terminal/quiet/approval semantics | API failed/cancelled/quiet/approval theory; no-tools untrusted-evidence brain test; approval/effect recovery store parity |
| J11 parent/source authority and queuing | API archived parent, source policy revoked and removed report model; exact active claim serialization; accurate pending/skipped/delivered projections |
| J12 transactional restart/races | InMemory/SQLite occurrence, input, child and report receipt replay/rollback/concurrent-claim tests; migration/current EnsureCreated checks; Compose restart evidence below |
| J13 Chat/Admin authoring | Required canonical fields, foreign target/conflicting model/mixed delivery API rejection; trusted Core Chat binding; editor create/edit/revision/focus/error and exact-source navigation tests |
| J14 text-only/required tools | Text-only conversational API execution; explicit Tools/background requirements rejected; Session pin remains authoritative |
| J15 Browser v2/native event regression | Full backend; core/browser-stt/browser-browser; native live/quiet receipt parity, webhook event/source tests, browser screenshots/lease/effect recovery and Secretary acceptance |
| J16 responsive operational UI | Shared component/editor tests plus bounded MCP review below, all three sizes, exact link navigation and keyboard interaction |

## Local gates and commands

Native tests used temporary SQLite, Synthetic adapters, isolated ports and no provider credentials. Backend commands use `--no-restore --nologo -p:UseSharedCompilation=false -m:1`; loopback test hosts required sandbox network permission. The following results distinguish aggregate coverage from a single uninterrupted run.

| Gate | Result |
| --- | --- |
| Full `dotnet test AgentCore.sln` | 2,725 passed, 14 optional live/provider/store probes skipped: Domain 168, Application 1,323, Infrastructure 847, API 383, plugin 4 |
| Final report security and store/model follow-ups | `BackgroundCompletionProjectionTests`: 4 passed; `AgentRunAdmissionStoreTests` plus `UnifiedAutomationMigrationTests`: 69 passed, including final expired-claim regression and current model snapshot validation. Together final unique backend coverage is 2,728 passing cases |
| Frontend full `pnpm run test --run --maxWorkers=1` | 744/745 passed initially; unrelated 30-second definition editor timeout. Unchanged affected file rerun: all 13 passed. All 745 cases covered successfully after rerun |
| Frontend `pnpm run build` | Passed; existing Rollup annotation and bundle-size warnings retained |
| Core Playwright Synthetic/browser-stt/browser-browser | 118/120 passed initially. New Admin test collapsed an already-expanded save row; corrected destination suite passed. Progress observer failed once; unchanged isolated rerun passed |
| Final destination and immediate-report browser gate | All 3 passed, including selected completion recipient create/edit/run, parent report status and same-child continuation; repeated after the final prompt boundary correction, 3/3 passed |
| Final prompt/tool boundary | 73 affected Application, 24 API and 12 hosted-tool/background Infrastructure cases passed. Completion payload and trigger evidence are data messages, separate from system instructions; Synthetic reporting reads that same boundary |
| Phase acceptance | faithful-manual 1, admin-lifecycle 1, p76-admin 1, p97-harness 6, p9899-continuity 2, p910-continuity-maintenance 1, secretary-demo 4: all 16 passed after canonical SQLite seed/source-link assertions were updated |
| Compose SQLite volume survival | Passed on final source using isolated `automation-target-closure`, port 5285, including strengthened destination/delivery/requirements comparisons. Earlier isolated port 5280 also passed. Disk exhaustion/Docker storage I/O blocked a rebuild; the engine recovered, then both final rebuild/recreation checks passed |
| Documentation/schema | Passed: migration tests/current EF snapshot, canonical wire fixtures, 16 complete JSON examples, 512 local Markdown links/anchors across 20 changed documents, design.json and `git diff --check` |

Core ports were 5180–5182/5273–5275; focused retry hosts used 5186–5188/5279/5281–5282. Acceptance projects ran sequentially on 5189/5284 with independent databases. Native MCP used 5185/5278. Commands match `.github/workflows/synthetic.yml`; optional provider probes were not enabled.

Failures were not hidden: missing canonical destination fields in old HTTP/direct-SQL fixtures, outdated raw-ID provenance assertions, one editor test toggling an expanded row, an EF GroupBy translation attempt and bounded completion-query starvation were repaired in their owning code/fixtures. A progress observer and definition editor timeout passed unchanged isolated reruns. The final store query also fixes expired active recovery behind an older queued Run. Final prompt review separated completion/trigger data from system instructions; three API cases first caught the Synthetic fixture's old evidence location, then all 24 affected cases and all three browser journeys passed after its correction. Disk pressure blocked a build; only completed task-owned disposable SQLite files were cleaned, preserving durable user data, logs and image evidence.

## Bounded Playwright MCP design review

One context-loader preparation, one coherent shared implementation, one review batch at **1440×900 / 768×900 / 390×844**, and one settled-animation confirmation for Background Work. Existing Ant Design theme, 8/12/16 rhythm, 16px drawer body, 40px narrow controls and persistent Admin footer are retained. No additional UI kit or speculative design primitive was added.

| Consumer | Desktop | Tablet | Mobile |
| --- | --- | --- | --- |
| Chat Automations/exact target | [1440](assets/automation-destinations/chat-1440.png) | [768](assets/automation-destinations/chat-768.png) | [390](assets/automation-destinations/chat-390.png) |
| Admin conditional report picker/long task | [1440](assets/automation-destinations/admin-1440.png) | [768](assets/automation-destinations/admin-768.png) | [390](assets/automation-destinations/admin-390.png) |
| Parent report/provenance | [1440](assets/automation-destinations/report-1440.png) | [768](assets/automation-destinations/report-768.png) | [390](assets/automation-destinations/report-390.png) |
| Reported and quiet Background Work | [1440](assets/automation-destinations/background-1440.png) | [768](assets/automation-destinations/background-768.png) | [390](assets/automation-destinations/background-390.png) |

The first mobile Background Work capture caught its resize animation; the one confirmation batch produced the correct x=0,width=390 drawer. No product layout correction was needed. Long title/actions/status wrap, table overflow stays local, the footer remains reachable and full-mobile Automations is readable. Keyboard Tab/Escape, selected recipient, save-focus return and retained error drafts are covered by MCP/component/browser checks. Exact Chat → Automation record and report → child → parent destination links were exercised.

Concrete MCP fixture: General Assistant instance `01a11b6b-1c71-71ef-965e-fdaccb025e9b`, parent `501e492d-1c0d-4c90-888d-8c8c341974b4`, immediate child `321a80f6-90ab-4979-9df0-a3f0010bd325`. Natural greeting produced Hello in the parent without a child. An explicit scheduled background task settled NoAction and displayed Not reported · quiet outcome; an immediate requested child displayed Reported with Initiative off. No page exceptions occurred in the functional browser gates. The MCP console records the existing Ant Design List deprecation, distinct from application errors; all 1,170 inspected MCP requests had successful responses, with no failed requests.

`.agents/context/{DESIGN,PRODUCT}.md`, `.impeccable/design.json` and Chat/Admin surface briefs now match these shared semantics and measured drawer widths. `/docs` remains authoritative.

## Limits and closure

No live model, microphone/headset, real site credentials or outbound provider spend was required or verified. Native source-owned event routing and voice transport were regression protected, not redesigned. Approval waiting stays pending and is never presented as completed; unavailable report models/policies retain an inspectable skip instead of granting fallback authority. Existing accepted data migrates to background/no-report without deletion; preexisting conflicting active claims fail the uniqueness migration visibly rather than being discarded.

The hosted freeze requires all five **Synthetic backend, Synthetic frontend, Synthetic Playwright core, Synthetic Playwright acceptance, Synthetic Compose smoke** jobs on one final behavior SHA. The exact SHA, workflow link, result and final local retry disposition are recorded in the [closure owner](../../TODO.md#automation-destinations-and-background-report-back-closure). Earlier frozen reports and SHAs are unchanged; P10/P11 remain unopened.
