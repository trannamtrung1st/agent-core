# Admin shared Events and authoring verification

## Scope and status

This working-tree enhancement implements the requested Admin capability selection, shared Event resource model, global Connections navigation and Automation authoring UX. It preserves the existing trusted-local owner boundary and Occurrence → Activation → Session/AgentRun execution owners. It does not establish a new milestone freeze or exact-candidate hosted CI acceptance. Historical closure reports remain unchanged.

Global Connections manages Credentials and Events. Instance Credentials manages explicit system-credential grants; Instance Automation manages Schedule/Event subscriptions and reaction instructions. Event management therefore no longer appears as an Instance binding tab. An Event subscribes multiple Automations by stable ID, independently of webhook authentication and agent capability authority.

## Implemented behavior

- Capability Form authoring uses Selected/All. Legacy editable drafts convert exact grants to Selected and existing discoverable projection after a trusted catalog read; conversion uses the latest candidate so concurrent instruction edits survive. Published legacy versions remain immutable. Authorized and Always available use the same category order, search and availability indicators; always projection reconciles with exact grants and excludes context-only capabilities. Core retains context-only ownership.
- Shared Events have stable ID, validated unique immutable key, editable name, Active/Revoked lifecycle and independently hashed bearer secret. Only create/rotate responses return a raw secret. Safe collection/details include actual subscriber counts, timestamps and recent delivery admission metadata.
- Generic webhook envelopes accept bounded arbitrary JSON data; no Event Type catalog or `order.placed` restriction remains. Authentication precedes persistence; duplicate delivery IDs reuse the same receipt and original subscriber snapshot. Disabled/ineligible subscribers do not create work. Correlation/recursion, untrusted evidence, normal execution policy, pending delivery recovery, Session concurrency and completion reporting remain on the same runtime.
- Automation’s drawer separates General, Trigger, Execution, applicable Completion and collapsed Advanced fields. Event selection searches name/key, explains Definition restrictions and provides nested global Event management without losing unsaved input. Newly created Events are selected on return. Supported timezone selection defaults locally, explains DST and retains finite timing validation.
- Global Event details deep-link to an owning Instance’s exact Automation; Automation details return to the global Event; Runs retain exact source navigation. Event selection supports refresh and browser history. Drawers/modals preserve focus, lock dismissal during writes and retain failed drafts for explicit retry.

## Upgrade and client impact

`20261008161708_SharedWebhookEvents` preserves resource IDs, hashes, subscriptions, receipts, deliveries, lifecycle/revisions/timestamps and existing occurrence admission identity. It assigns old GUID public keys as `event.` plus the old key without hyphens. Existing clients must update webhook URLs and remove the retired envelope `type` field. Keys identify resources; secrets authenticate them. The nopCommerce producer/helper emit the generic body with order reference data.

The migration also rewrites Event-linked occurrence dedupe keys so interrupted deliveries cannot create duplicate work after upgrade. It deletes no non-demo data. Back up SQLite before upgrade; downgrade requires the pre-upgrade backup and matching application version. See [persistence contract](../15-persistence-and-configuration.md#shared-event-resource-upgrade), [protocol example](../14-api-and-realtime-protocol.md#shared-event-webhook-envelope) and [operations](../17-observability-and-operations.md#shared-event-webhook-operations).

## Executed verification

| Check | Result |
| --- | --- |
| Full `dotnet test AgentCore.sln --no-restore --nologo --disable-build-servers -m:1` | 2,786 passed, 14 opt-in skips; Domain 173, Application 1,333, Infrastructure 892, API 384, nopCommerce emitter 4 |
| Final SQLite upgrade/store/tool regression | 11 passed after immutable identity/CAS and occurrence-dedupe migration changes |
| Final Event/Automation API regression | 6 passed after the final Event policy projection change; generic JSON, unique keys, safe metadata, fan-out, disabled policy, dedupe, auth lifecycle and existing execution path |
| Focused frontend Event/Automation/routes/capability tests | 30 passed |
| Final Automation policy editor regression | 19 passed, including missing-policy rejection before saving an existing Event subscription |
| Frontend full suite | Explicit Node 22, one worker: 744 passed and 9 failed while concurrent Chat edits were being applied. Fresh reruns covered all 49 tests in the three affected files: 22 passed in drawer/files, 26 passed in Chat, and its remaining regression passed after fixture/selector alignment. All 753 encountered cases have passing results across these runs; one unchanged-candidate full invocation was not green |
| Production build | TypeScript and Vite passed, including an explicit Node 22 build; existing bundle-size advisory remains |
| Synthetic Playwright journeys | Final closure batch: 11 passed, including shared Event create/subscribe/dedupe, capability reconciliation, resource publication and history; Schedule/destination, navigation, credential and responsive browser regressions also passed in affected batches |
| Isolated Compose/SQLite survival gate | Passed; separate project `agentcore-admin-events-verify`, port 5099, isolated volume; container recreation, runs, Automation, workspace, resources, system credentials and key ring survived |
| Documentation and whitespace checks | 14 changed/report Markdown files: relative paths, heading anchors and balanced fences passed; 15 complete JSON examples parsed; `git diff HEAD --check` passed |

Required changed-path browser execution used Playwright MCP against the running Synthetic host at 5098 and Vite at 5188, with disposable SQLite and separate workspace, attachment, artifact and credential key roots. The following expected outcomes were observed:

1. Create an Event with key `invoice.paid`; invalid key input blocks submission; the secret appears once and clears after Done. Rotate and revoke retain existing authentication guarantees through API/browser tests.
2. Author an eligible Instance Event Automation, POST arbitrary invoice amount/currency JSON, then replay it. Admission returned 202 then 200; the existing background Session/Run completed with No action, with no duplicate work.
3. Open Event subscribers, navigate to the exact Instance Automation, inspect its Run and return to the source. Browser Back/Escape restores the Event/collection URL correctly.
4. Keep unsaved Automation name/instructions, create `payment.received` in nested Event management, close the credential/manager and return. All input remained and the new Event was selected.
5. Open capability groups, authorize/project workspace read, remove authorization and observe projection reconciliation, restore and save the canonical draft. Both selectors exposed the same category; context-only capability handling remained independent.
6. Keep a failed Event creation draft, retry explicitly, lock dismissal during a gated save, reconcile filters, rotate and return focus. Schedule/destination and hidden-editor navigation tests retain prior behavior.
7. Open an Examiner Instance with no Event policy and start an Automation. The Event trigger option and invalid submission are disabled before any write.

The earlier two-worker frontend run recorded three rendering timeouts; both affected files passed all 21 tests in a serial rerun. A subsequent `pnpm run test --run --maxWorkers=1` unexpectedly launched Homebrew Node 26.7.0 instead of the shell/CI Node 22 and produced 70 `localStorage` failures in six service files. Those 70 tests passed with the explicit Node 22.18.0 executable. The final full unit gate used `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node node_modules/vitest/vitest.mjs run --maxWorkers=1` from `web/`, retaining fresh file isolation and existing timeouts. This enhancement added no production or test shim for the local launcher mismatch.

During that final invocation, concurrent work changed the background drawer, original result fixtures and artifact ownership outside this Admin enhancement. The workspace grew from 90 changed files to more than 120; the running test process had already loaded earlier components when newer tests were read. Fresh drawer/file reruns passed 22 tests. The full Chat rerun passed 26 and retained one stale fixture: it selected the retired “View history” action and supplied its result only through conversation run history. That fixture now selects “View original result,” provides the same completed run in the background snapshot and tolerates the action’s attention-count label; its isolated live/paused/ended regression passed. Concurrent implementation edits were preserved.

## Follow-up functional review

The requested second review corrected four gaps without changing the execution architecture:

- Schedule policy projection now requires an enabled Definition policy with Schedule source permission. The editor also fails closed when policy is absent, explains the restriction and enforces the backend's 366-occurrence ceiling.
- Event/model catalog failures have independent persistent recovery feedback. Successful Automation polling cannot erase them; explicit retry reloads the catalogs, and returning to the Instance tab refreshes them while retaining the draft.
- Per-subscriber evidence overflow records Skipped and continues fan-out. The bounded instructions plus generic payload are never truncated, and replay cannot duplicate eligible work or leave the invalid subscriber pending forever.
- Event details disables Close, Escape and mask dismissal during rotation/revocation, retaining ownership of the pending operation and one-time credential response.

Fresh verification passed: 9 Event/Automation API integration tests, 25 frontend Event/Automation tests, TypeScript and the Vite production build. The API tests include disabled/Event-only Definition policies and SQLite fan-out with oversized Unicode instructions, one eligible subscriber and replay. The frontend tests include missing policy, catalog retry and rejection of occurrence count 367.

Playwright MCP exercised the restarted Synthetic host at 5098 through Vite at 5188. Injected catalog failures survived an Automation refresh, then explicit retry recovered. Nested Event creation retained the Automation draft and saved its subscription; generic webhook delivery returned 202 then 200 on replay and appeared in Event details. A gated rotation kept details open after Escape, then cleared its one-time secret after Done and rejected the old secret with 401. A missing-policy Examiner Instance blocked Schedule creation before a write. Test harness routing/selector errors were corrected before these successful checks; startup and injected HTTP failures produced expected console network errors. These were functional checks, without another visual polish cycle. Logs are retained under `local/verification/admin-events/logs/admin-review-*`.

The full unchanged-candidate combined-workspace gate remains the limit described below; this review's focused checks do not imply that unrelated concurrent changes have been accepted.

## Rendered review

One initial batched inspection and one confirmation round covered Capabilities, Events collection, Event details and Automation at 1440/768/390 pixels, including long content and real subscriber/delivery records. Corrections were batched: Connections heading, shared search sizing, subscriber column width and wrapping detail actions. Initial full-page captures retained scroll/sticky positions; opening-drawer captures also required settling their transforms. Capture corrections did not introduce further discretionary UI edits. The final settled Automation bounds were 640 pixels at desktop/tablet and 390 pixels at mobile; drawer body scroll width equaled client width. All reviewed documents matched viewport width; wide tables scroll locally. Mobile controls retain the shared 40-pixel policy.

Local screenshots and logs are under `local/verification/admin-events/` (ignored verification artifacts), with command output in its `logs/` directory. Screenshot names are `capabilities-confirm-{1440,768,390}.png`, `events-confirm-{1440,768,390}.png`, `event-details-confirm-{1440,768,390}.png` and `automation-confirm-{1440,768,390}.png`. Changed-path browser checks reported no JavaScript errors; inherited Ant Design deprecation warnings remain.

## Limits

Hosted GitHub Actions was not dispatched for this uncommitted working tree. Its applicable backend/frontend/build/Compose gates were exercised locally; optional Real provider/store probes and unrelated voice/acceptance projects were not rerun. No hosted milestone acceptance is claimed. Event details describes admission status for recent receipts, not model execution success; Runs owns that result.

A single green full-suite result against an unchanged combined workspace remains unverified. The recorded full frontend invocation and targeted rechecks establish the passing cases above; acceptance of concurrent Chat/artifact changes and the latest combined candidate requires the full gate after those edits settle.

Impeccable context reported its design sidecar stale against `DESIGN.md`. The requested reusable design guidance was synchronized, while tooling drift was not repaired as a side effect; a separate Impeccable document refresh remains optional.
