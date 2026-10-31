# Product experience transformation — delivery ledger

Implementation began 2026-10-30 and verification continued 2026-10-31 (Asia/Ho_Chi_Minh). **Order 6 accepted 2026-10-31 for the controlled local Synthetic working tree; all AC01–AC22 pass.** Baseline `0de4e03d727dbb6d6b8ecd474fd8ba91011a5dfb`, initially clean checkout. Changes remain uncommitted; no new commit SHA or CI acceptance is implied.

The [Product Experience Contract](../product-experience-contract.md) owns direction. The [master manifest](assets/product-experience/coverage.json) records 362 source-derived route/view/panel compositions, their actual owners, dispositions and qualification families. [Route visits](assets/product-experience/route-visits.json) distinguish 123 direct visits across 41 tenant/agent/platform route families at 1440/390/320px from feature-specific state tests. Inventory counts are compositions, not 362 independently screenshot-tested features.

## Resulting experience

Organization selection opens Chat. Ordinary Agent choices lead with name and role. Chat keeps its exact Session and unsent draft across same-tenant management navigation; account/tenant transitions retain document-level retirement. A labeled Tools disclosure owns existing file, sandbox, work, Task, Goal and Workflow actions with stable focus restoration. Attention and immediate response controls remain visible.

Organization task groups replace a flat inventory. Agent and Definition draft pages use contextual navigation, explicit parent links and organization/scope transitions. All existing tenant-qualified destinations and their permission guards remain. Settings categories disclose the existing sparse-draft controller rather than creating separate forms. The Workflow step palette sits above structure/properties; mobile Outline retains editing access without dragging.

A single dark evergreen Ant Design v6 theme, native controls and shared spacing owners migrate the remaining compositions. Existing specialized editors, resource inspectors and protected operations are retained deliberately. No backend contracts, authority rules, persistence model, runtime/voice ownership, dependency/framework or deferred Group Chat feature changed.

## Discovery, critique and bounded finish

- Discovery started from actual routes/source and grew from 189 to 362 entries as static panels, Settings sections, query-selected details, recovery entries and 62 imperative confirmations were reconciled. `node web/scripts/product-experience-inventory.mjs --check` validates registry/type coverage, missing/orphaned entries and source drift. JSX discovery never grants qualification.
- Initial app exercise: sign-in, organization/access, actual Synthetic Chat answer, Files, Agents/Profile/Settings, Spaces, Workflows, Evaluations, Models, Members and system Accounts. Loading captures were rejected and replaced after settling.
- Independent Assessment A used its own CUA browser: invalid sign-in, organization entry, completed Chat, mobile Profile/Settings. UX score **24/40**; priorities incorporated: Chat-first entry, contextual scope, labeled tools, settings disclosure and everyday Agent terminology. No detector findings consumed by A.
- Independent Assessment B returned technical score **16/20**. Its detector exited 0 with actual JSON `[]` ([initial output](assets/product-experience/initial-detector.json)). Its CUA mutation-dependent checks were unavailable; source/DOM/screenshots/CLI were used. Two Chat sends encountered recoverable access changes, with draft retained; parent MCP and CLI separately established a real answer. Neither assessment certified physical devices or blanket accessibility.
- One batched visual inspection, one shared fix batch and rendered confirmation: flattened narrow Chat header, safe compact admin header, aligned conversation preference row, smaller owner identity/preamble, broader Workflow canvas with collapsed palette, and readable placeholders. Further iterations addressed actual functional/test failures, not discretionary redesign.
- Fresh independent finish reviewer actually opened 20 required final images and six baselines, then the replacement settled conflict and hover images. Its only confirmed source defect was primary hover contrast: `#328568` with white labels was 4.48:1. Corrected shared hover to `#2f8064`; **MCP measured 4.780815:1** ([measurement](assets/product-experience/hover-contrast.json)). No rebuild or additional material visual change was recommended. A later real Task timing failure exposed an introduced delayed-focus race: the shared Session Drawer now preserves an already-focused field/newer dialog. Four focused tests passed independently and the real Task/Goal/file/Workflow journeys plus rapid MCP selection passed. The native reasoning Slider handle also received its supported AntD accessible label; 19 picker tests passed independently. The [final independent review](product-experience-independent-review-2026-10-31.md) returns **Ready with Minor Improvements**, disposition ship, with no unresolved material blocker.
- The final manual detector scanned 26 changed production markup/CSS targets at visual confirmation, exited 0, actual `[]` ([output](assets/product-experience/final-detector.json)). Subsequent focus/Slider semantic fixes received normal hook feedback and focused independent/unit/browser review; no second manual detector cycle was run. This result does not establish overall UX quality.
- Reconciled 35 source-backed Impeccable briefs and 116 source fingerprints. Two removed-component briefs are retired link-only records so historical report links resolve. The sidecar is tooling context, never visual acceptance.

## Rendered evidence

[Image manifest](assets/product-experience/images.json) records 53 actually viewed representative captures (15 baseline, 38 final), dimensions, roles, timestamps, hashes and raw provenance. Tracked images use lossless WebP; no image content was edited. Goldens remain PNG. Rejected skeleton and pending-save images carry no acceptance weight.

| Comparison | Before | Final |
| --- | --- | --- |
| Desktop Chat | [baseline](assets/product-experience/baseline/chat-populated-1440.webp) | [final](assets/product-experience/final/chat-populated-1440.webp) |
| Narrow Chat | [baseline](assets/product-experience/baseline/chat-populated-390.webp) | [final](assets/product-experience/final/chat-populated-390.webp), [320 stress](assets/product-experience/final/chat-populated-320.webp) |
| Agent settings | [baseline](assets/product-experience/baseline/agent-settings-1440.webp) | [desktop](assets/product-experience/final/agent-settings-1440.webp), [768](assets/product-experience/final/agent-settings-768.webp), [390](assets/product-experience/final/agent-settings-390.webp), [320](assets/product-experience/final/agent-settings-320.webp) |
| Agent collection | [baseline](assets/product-experience/baseline/agents-1440.webp) | [final](assets/product-experience/final/agents-1440.webp) |
| Workflow | [baseline collection](assets/product-experience/baseline/workflows-1440.webp) | [editor](assets/product-experience/final/workflow-editor-1440.webp), [mobile Outline](assets/product-experience/final/workflow-editor-390.webp), [completed proof](assets/product-experience/final/workflow-completed-390.webp) |
| Sign-in | [baseline](assets/product-experience/baseline/sign-in-1440.webp) | [final](assets/product-experience/final/sign-in-1440.webp) |

Additional reviewed states: [settled stale Space](assets/product-experience/final/space-stale-390.webp), [disabled Environment selection](assets/product-experience/final/space-environment-320.webp), [Task result](assets/product-experience/final/task-result-390.webp), [verified Goal](assets/product-experience/final/goal-completed-390.webp), [exact Evaluation](assets/product-experience/final/evaluation-evidence-1440.webp), [approval](assets/product-experience/final/approval-review-approve-390.webp), [file preview](assets/product-experience/final/file-preview-390.webp), [read-only agent](assets/product-experience/final/reader-profile-390.webp), [platform denial](assets/product-experience/final/permission-denied-390.webp), [1024 platform layout](assets/product-experience/final/system-organizations-1024.webp), [320 platform list](assets/product-experience/final/system-operators-320.webp).

Source attribution: baseline captures are from the clean SHA above. Final captures span one implementation/confirmation cycle in the dirty working tree. The final hover-only correction and duplicate-failure suppression do not change the earlier normal/success captures; the separate browser hover measurement verifies that final state. Final source fingerprints identify the resulting implementation, not an invented historical screenshot commit. The focus/Slider semantic corrections do not alter the supplied normal-state pixels; their actual interaction results are recorded separately. The three Darwin/Chromium goldens were asserted without updates on the confirmation source. Other browser/OS screenshot baselines are unqualified.

Required screenshot categories were reconciled separately after review: [organization selection](assets/product-experience/final/organization-selection-1440.webp), [setup pending](assets/product-experience/final/assistant-setup-recovery-390.webp), [expanded Agent choice](assets/product-experience/final/agent-selection-390.webp), [real recovered reply](assets/product-experience/final/assistant-recovered-390.webp), [Definition draft](assets/product-experience/final/definition-draft-390.webp) and [open mobile navigation](assets/product-experience/final/admin-navigation-390.webp). Parent and fresh reviewer actually opened all six. The setup screenshot uses the existing explicit pending-read simulation; authorized reconciliation and the Synthetic answer are real. [Closure record](assets/product-experience/screenshot-closure.json) preserves method and failed setup/wait attempts. This was required evidence completion, with no source changes or discretionary polish.

## Functional and local check ledger

Fixtures use disposable SQLite, workspace, attachment, artifact, credential-key, Definition-resource and Browser roots under `/tmp/agent-core-order6`. API `127.0.0.1:5088`; Vite `127.0.0.1:5179`. `/health` confirms Synthetic and ready workers. Browser/OpenSandbox execution stays disabled; speech uses Synthetic adapters and Chromium fake media. No live provider credentials are required.

Initial sandbox host startup stalled before database creation and Vite returned `listen EPERM`; only task-owned processes were stopped and relaunched through the approved local-process path. The first Vite process inherited the repository's **public JSON protocol selector**, while wire interception tests assumed MessagePack. The later profile explicitly sets MessagePack. JSON Chat evidence remains valid; it is not wire-interception evidence. An early parallel test launch raced shared fixture/security state and hit the unchanged 10/minute login limiter. Runs are now serial; existing auth backoff is reused. No production limiter was weakened.

| Check | Actual result |
| --- | --- |
| Disposable backend build | Passed, 0 warnings/errors; no server source changed. |
| Full frontend unit sweep | 172 files, 1,471 cases: 1,466 initially passed; five obsolete Chat/palette assertions failed. All five repaired and their owning suites rerun successfully (32 cases). This is not reported as one pristine 1,471-case run. |
| Navigation/identity/configuration/Workflow focused checks | Seven files / 47 cases passed. |
| Settings persistence/source/reset/recovery | 13 cases passed. |
| Final Chat failure/disclosure checks | 31 passed, including one failure/retry owner for failed new Chat. |
| Final Drawer timing / nested focus | Four passed in two files, independently rerun; rapid Task selection and full Task/Goal flows pass in the browser. |
| Final ModelPicker accessibility | 19 passed, independently rerun; named native handle and spoken value also checked through MCP/keyboard. |
| Final theme contrast checks | Two passed; body/secondary/tertiary/placeholders/links and normal/hover/active primary labels meet the small-text floor. |
| Final `pnpm build` | Passed strict TS and Vite; 5.89s bundle step. Existing large chunks remain: main 2,950.28kB (gzip 887.53), spreadsheet 2,758.87kB (gzip 593.97). |
| First combined canonical browser run | 24/38 passed; failures retained and diagnosed, not hidden by retries. Includes stale selectors, JSON/MessagePack mismatch and auth-rate-limited fixture login. |
| Repaired MessagePack cases | Real reconnect, voice at 1440/390, Task, scoped Definition recovery, omitted-default recovery passed. Settled Space stale/readback/resave passed in subsequent focused run. All remaining cases were corrected/qualified below; the original failures remain in the logs. |
| Definitive browser aggregation | **76/76 unique cases have passing latest results, zero remaining failures**, across recorded serial/focused runs. This is not one pristine full run. [Per-case results and failure history](assets/product-experience/browser-results.json). The last three-case run passed all three; expanded Settings category/save/reset verification passed in 8.8s. |
| New fixture launcher | Checked-in launcher started a fresh Synthetic host and ran the expanded Settings/IAM case; an existing database was deliberately rejected. |
| Final MCP check | Real reply; rapid Tools → Tasks → Agent selection retained; discarded only an unsaved Task draft; Tools focus restored; named Slider keyboard selection reached High. [Record](assets/product-experience/mcp-final-check.json), [restoration](assets/product-experience/drawer-restoration.json). Final navigation console errors: 0. |
| Master inventory | 362 rows; no missing, orphaned or drifted sources after reconciliation. Each row distinguishes shared/source, owning-feature and direct-route evidence. |
| Docs | All changed/staged/untracked Markdown relative links/fences checked; 0 errors. Final result saved with the evidence. |
| Hosted CI | Not requested or run. |

The combined model checks exposed legacy fixture assumptions: automatic managed General Assistant adds a real default reference and is the ordinary Chat choice; tests for a separately authored agent must explicitly select/prepare that identity. Removed UUID label fragments are replaced with human fixture names. Canonical last concrete Run route (`host`) is distinguished from durable user/inherited `modelPreference`, per the existing wire contract. Model control labels/admission operations use current source owners. These are test migrations, not backend changes.

## Reproduce the qualified profile

Use a fresh disposable directory. From `web/`, start the host and frontend in separate terminals:

```sh
EXPERIENCE_DATA_ROOT="$(mktemp -d /tmp/agent-core-experience.XXXXXX)" node e2e/support/run-product-experience-host.mjs
```

```sh
VITE_AGENTCORE_REALTIME_PROTOCOL=messagepack VITE_API_PROXY_TARGET=http://127.0.0.1:5088 VITE_DEV_PORT=5179 pnpm exec vite --host 127.0.0.1 --port 5179 --strictPort
```

Then run the experience configuration serially; its canonical setup rejects an unknown/non-Synthetic host:

```sh
CANONICAL_SYNTHETIC_URL=http://127.0.0.1:5179 CANONICAL_SYNTHETIC_API_URL=http://127.0.0.1:5088 TENANT_UI_LOGIN=tenant-chat-owner@example.test TENANT_UI_PASSWORD=synthetic-tenant-chat-password pnpm exec playwright test --config playwright.experience.config.ts
```

Use `--grep` or file arguments for focused reruns. `EXPERIENCE_CAPTURE_ROOT` optionally collects feature captures. Goldens are asserted by default; update only deliberately after image review. Inventory check from the repository root:

```sh
node web/scripts/product-experience-inventory.mjs --check
```

## Accessibility and qualification limits

Actual evidence includes named controls, native disclosures, heading/focus review, visible field validation, Tools keyboard entry and Escape restoration, protected Drawer focus, reduced-motion paths, local table overflow, 320px reflow and 768/1024/1440 layouts. Semantic state text remains independent of color. Measured resolved text/placeholder/link and primary-state contrast has a regression guard. Custom Chat/Workflow motion and scrolling honor reduced-motion CSS; short native AntD transitions remain library-owned. This is a focused WCAG 2.2 AA pattern review, not universal compliance certification.

Two optional low refinements remain: at 320px the organization trigger truncates heavily (full accessible name/title remains), and the visible inherited/customized section summary is aria-hidden to preserve the concise native heading name (field override/reset controls and inheritance guidance remain accessible). Neither was judged material by the fresh reviewer.

Physical microphones/speakers, screen-reader hardware, real providers, real OAuth/Browser/OpenSandbox, sustained native workloads, public deployment and owner taste validation are not established by Synthetic emulation. Those boundaries remain for subsequent qualification. Order 6 refinement stops at this accepted baseline. Next are local self-host readiness and owner manual validation, followed by the existing roadmap; dependent initiatives remain unstarted.



## Source identity and cleanup

Base Git SHA: `0de4e03d727dbb6d6b8ecd474fd8ba91011a5dfb`. Exact changed-source/dependency digest: `1cea2eaa157e826ee76fc4be78ca1501f41a8bc45f2fab937dbc671310a1007b` (SHA256). The [source manifest](assets/product-experience/source-manifest.json) fingerprints 76 source/test/fixture/dependency files and final build artifacts. No backend file changed. There is no fabricated final commit SHA: this is an uncommitted working-tree qualification. The index/working tree remain available for owner review.

[Cleanup](assets/product-experience/cleanup.json): task API/Vite ports 5088/5179 are closed, no task launcher/API/Vite process remains, MCP has no open tabs, disposable databases/resource/credential roots were removed. Unrelated processes were not stopped. Curated artifacts are retained in the repository and raw task evidence remains in scratch storage.

## Journey qualification

| Journey | Visible outcome / proof |
| --- | --- |
| J01 | Invalid sign-in, organization creation/selection, invitation acceptance, read-only role inspection, password change/restore and logout; `access-foundation`, product entry. |
| J02 | Managed assistant setup retry/hide/reset; actual Synthetic answer from ordinary entry; `managed-general-assistant`, product entry. |
| J03 | Exact Session/draft retained through management; reload/rename/archive/completion, tenant/logout retirement, Activity continuation; product, tenant Chat, Automations. |
| J04 | MessagePack reconnect, queue/Stop/Steer and independent authority; actual fake-media capture/playback/heard credit/mute/reconnect at 1440/390; tenant Chat/voice. |
| J05 | Attachment-only input, protected Markdown preview, resource import/preview/download/edit/copy/delete, stale/partial/lost-reply/read-revocation recovery; product, Chat, resources. |
| J06 | Definition authoring/publication and scoped resources; agent creation, exact version adoption, Settings draft retained across categories, persisted override/reload/reset and inherited readback; Definitions, agent access/version, product. |
| J07 | Shared Space and Environment/variable authoring, disabled selection, exact scope/resource recovery; real private-context executor grants; product, resources and controls. Named-secret component behavior also has retained focused unit coverage. |
| J08 | Protected provider key, model/default/reference management, stale/lost/default absence, exact model pin versus inherited preference, Definition/Instance inheritance and read-only boundaries; model management. |
| J09 | Real delegated question/wait/guidance/completion and independent Goal/evaluation evidence; Task/Goal suites. |
| J10 | Author/validate/publish/start and attributable human process sign-off, terminal 1/1 required steps; product Workflow. |
| J11 | Saved Automation UI, stale destination, exact admission Review, report delivery, exact existing-conversation execution and attributable Activity; actual Goal Evaluation and proof inspector; Automations/Goals. |
| J12 | Different signed-in person approves/rejects both Review and Authorize once; requester original Chat recovers the durable decision; approval independence and tenant Chat. |
| J13 | Member/role/IAM authoring and last-owner protection; exact scoped reader/revocation and authoritative denied mutation; access foundation, agent access and feature readers. |
| J14 | Platform organization/Account status, operator assignment/final-operator protection, privacy stale/lost/keyboard paths and organization-only denial; platform suites. |
| J15 | Explicit stale/readback, uncertain outcomes, non-replayed mutations, dirty/discard guards, disabled services, auth/transport recovery and 320px stress; owning feature suites above. |

These are the critical exercised branches for the controlled Synthetic profile. The inventory distinguishes source-retained nested compositions from direct browser visits and tests; it does not imply an individually executed case for every panel/state/role combination.

## Acceptance matrix

The following statuses refer only to the controlled local profile and exact working source above. The [independent review](product-experience-independent-review-2026-10-31.md) supports acceptance; only the two optional low refinements above remain.

| Criterion | Status | Evidence |
| --- | --- | --- |
| AC01 Product direction | Pass | Canonical experience contract; independent initial critique and fresh finish review. |
| AC02 IA | Pass | Contextual registry/shell; 123 route visits; exact context/draft return. |
| AC03 Inventory | Pass | 362 owner-dispositioned routes, panels, overlays and imperative confirmations; drift check clean. |
| AC04 No feature loss | Pass | Registry/type guard, source-retained operation owners, owning-feature tests and visible critical journeys. |
| AC05 Unified system | Pass | AntD v6/theme/AppShell/CSS owners; no new dependency or UI kit. |
| AC06 Complete redesign | Pass | Shared migration across all discovered compositions; specialized domain editors deliberately retained. |
| AC07 First use | Pass | Real managed-assistant setup and Chat, invitation/account/organization journeys. |
| AC08 Chat/voice | Pass | Canonical text/receipt/reconnect/control/retirement plus both fake-media voice widths. |
| AC09 Journeys | Pass | J01–J15 table and 76-case explicit browser ledger; conditional external services remain outside profile. |
| AC10 Security/IAM | Pass | Exact readers/revocations, protected keys, approvals, denied server mutation and stale/lost-reply boundaries. |
| AC11 Responsive | Pass | Viewed 1440/1024/768/390/320 compositions, route stress and owning-feature overflow checks. |
| AC12 Accessibility | Pass | Focus/name/validation/reflow/reduced-motion pattern review, contrast guard, corrected hover/Drawer/Slider defects; two non-material optional refinements. |
| AC13 Real images | Pass | 53 actually viewed captures, provenance/hashes and explicit rejected captures. |
| AC14 Visual regression | Pass | Three reviewed stable PNG goldens subsequently asserted without updates. |
| AC15 State design | Pass | Initial/empty/active/terminal/error/denied/read-only/stale/discard/cancel and uncertain outcome branches. |
| AC16 Verification | Pass | Local build, relevant full/focused unit evidence, 76 browser cases; no changed server contract requiring new .NET tests. |
| AC17 Migration | Pass | Existing scoped URLs retained deliberately; retired URLs remain unavailable; metadata/fixtures/selectors synchronized. |
| AC18 Impeccable | Pass | Initial A/B, selected code-led direction, bounded inspection/fix/confirmation, detector/hook records, metadata and fresh review. |
| AC19 Documentation | Pass | Canonical product/frontend/testing/roadmap owners and tooling context synchronized; link/fence checks clean. |
| AC20 Independent review | Pass | [Fresh final standard implementation review](product-experience-independent-review-2026-10-31.md): Ready with Minor Improvements; no Critical/High or material Medium blocker. |
| AC21 Evidence integrity | Pass | Base SHA + exact working-source digest, per-run outcomes/history, images, fixtures/cleanup and explicit limits. |
| AC22 Scope | Pass | No backend contract/architecture change, new UI framework, Group Chat or unrelated product feature. |

## Order 6 closure and handoff

All mandatory criteria pass for the exact local Synthetic working-source digest recorded above. The fresh independent review supports acceptance with two optional low refinements. **Stop Order 6 refinement.** Hand off this baseline and its reproducible fixture to local self-host/development readiness, then owner manual UI/UX validation. Those initiatives and Group Chat remain unstarted. No commit, hosted CI, physical-device/provider qualification or public deployment is implied.
