# Exact tenant completion-read Review — 2026-10-20

Successor: [automatic report source-read consent](tenant-report-source-read-verification-2026-10-20.md) closes the positive automatic consent family identified as open in this historical checkpoint. The original checks and counts below retain their dated scope.

This checkpoint implements protected terminal-child reads for human-attributed conversational and immediate-background tasks. It does **not** complete the full IAM proposal. Automatic reports now refuse source reads that lack raw permission or require separate Review; their positive source-read consent continuation remains open, independently of report-Run creation consent.

## Implemented behavior

- The existing `approvals.request_authorization` tool accepts the closed `inspect_background_completion` intent with one exact `read.backgroundSessionId`. Core derives the owner, initial child, exact terminal approval successor, source revision, bounded observation/hash and up to three independently readable artifact metadata witnesses. No file bytes are included. Missing raw Session/Run/Artifact reads cannot be replaced with an exceptional grant.
- Ordinary Review uses the incumbent approval, decision, signal, local read receipt and successor-Run owners. Approval alone does not claim a result or mark it handled. The delivered tool observation carries server-authored request provenance; current source, configuration, capabilities, raw reads and reviewer qualifications remain required when the same task consumes it through list/inspect/take/acknowledge/wait and final-answer persistence. A copied UUID or different human root cannot reuse it.
- Rejection/cancellation retains completion accounting without disclosing the result. A human with independent source reads can still inspect the saved evidence and cancel when executor reads or Agent configuration change; decision/execution qualifications remain stricter. SQLite preserves prior guards, freezes the new evidence and refuses a downgrade that would discard retained consent.
- Automatic report preparation, direct/reviewed admission, provider-context use and atomic final-save now separately check private child Session and initial/source Run reads. Creation authority or a creation-only grant cannot substitute. A policy-required source read stays Pending without admitting a report; no automatic read-consent producer is claimed here.
- The shared native approval inspector shows literal result text, source identities, checksum and related metadata, with explicit read/handled boundaries. Malformed saved detail disables approval/rejection and preserves cancellation. Chat uses the same inspector and names this action explicitly. No CSS, token, UI-kit or visual-world change was introduced.

## Functional evidence

Actual Synthetic HTTP workflows qualified Memory and SQLite: create and finish a child, suspend the parent for exact read Review, approve, resume through the existing successor, inspect/take/acknowledge, then save the answer. Accounting is Claimed before the final answer and Handled afterward; no report activation or exceptional read grant is created. Separate cases dispose/recreate the SQLite application while consent is pending, and follow a child's actual Workspace-read approval successor before reviewing its terminal result.

Focused store cases exercise compound read policies and metadata, revoked source/reviewer authority, new reviewer independence, hidden-source filtering before paging, rejection/cancel suppression, copied proof refusal, stale-configuration cancellation, immutable SQLite guards and lossless schema constraints. Report-fence cases reproduce missing raw reads and new read Review at preparation/admission and after admission at provider/final-save boundaries; the failed final-save leaves the Session revision unchanged.

## Checks

| Check | Evidence and result |
| --- | --- |
| Representative actual HTTP read workflow | `tenant-completion-read-representative4.log`: 2 passed, Memory/SQLite |
| Actual SQLite application recreation | `tenant-completion-read-cold-host.log`: 1 passed |
| Actual child approval successor | `tenant-completion-read-child-successor.log`: 2 passed |
| Read contracts/projection | Initial group: 10 passed; final focused completion/read contracts: 9 passed (`tenant-completion-read-application-final.log`), with overlap |
| Initial store group | 18 passed / 1 failed; corrected SQLite visibility qualified by 2 passing parity cases, then included in the grouped regression |
| Stale readback/cancellation | `tenant-completion-read-stale-cancel.log`: 4 passed |
| Grouped affected store regression | `tenant-completion-read-regression-store.log`: 94 passed; includes completion/read/wait/report cases, with overlap against earlier groups |
| Automatic-report source fences | `tenant-completion-read-report-fence.log`: 8 passed, including provider/final-save cases |
| Synthetic approval continuation | `tenant-completion-read-script.log`: 2 passed after correcting the reproduced prompt-loss fallback |
| Shared inspector frontend cases | `pnpm run test --run src/features/access/TenantApprovals.test.tsx src/features/chat/TenantConversationApprovals.test.tsx`: 78 passed in the one matching test file; the second path has no dedicated test file |
| Final actual read/report HTTP | `tenant-completion-read-report-http.log`: 18 passed (5 read workflow cases and 13 report creation/negative/restart cases) |
| Final migration/context fences | `tenant-completion-read-final-fences.log`: 5 passed (empty SQLite down/up restores 11 guards/model/FK parity, plus 4 current provider/final-save fences) |
| Corrected creation HTTP workflows | `tenant-completion-read-creation-corrected.log`: 20 passed after explicitly granting independent source reads |
| Corrected creation restart | `tenant-completion-read-creation-cold-corrected.log`: 1 passed with actual SQLite application recreation |
| Frontend production build | TypeScript/Vite passed; existing large-chunk advisory remains |
| Browser | Playwright MCP against disposable Synthetic API/Vite, actual cookie/CSRF, canonical setup and visible Chat/review/decision actions; details below |

Logs named above are retained under `/private/tmp/`. Counts overlap and must not be summed as distinct cases. The final solution build passed with zero warnings/errors (`tenant-completion-read-final-solution-build.log`). Relative-link validation resolved 1,927 file references across 15 changed Markdown files, with no broken targets or unbalanced code fences; `git diff --check` passed. No full CI suite or hosted CI was run.

The first broad HTTP regression hit eight older creation-fixture failures: those executors had creation authority but no source-reader roles. The positive fixtures now grant reads explicitly; the production gate was not relaxed. That run stopped making progress after the failures and was cancelled. It is not passing regression evidence. Bounded corrected creation/report/read checks replace its affected workflows.

## Browser and finish evidence

The final browser host used Synthetic/InMemory with all writable roots under `/private/tmp/tenant-completion-read-browser-final`, API 7162 and Vite 7163. Setup published an explicit capable definition and independently granted creation/source/metadata permissions; read policy was Always. The UI sent `[test:completion-read-review]`, created a real child Artifact/result and durable Review, displayed escaped `<script>` text, approved with protected confirmation, refreshed the recorded read receipt and saved the expected final answer. Background work then showed **Handled in conversation**. A second real request lost configuration authority, kept readable saved evidence, disabled approval/rejection, cancelled successfully and returned a truthful no-data continuation. A third request independently rechecked the desktop stale state and was cancelled. A deliberately intercepted missing-checksum detail response qualified only the unavailable-preview UI branch, then was removed before real decisions.

The initial manual run exposed an unrelated Synthetic canned-answer fallback after an approval continuation no longer carried the original prompt. The new script now recognizes its retained consent result; two regression cases and the complete final browser journey qualify the correction. Final captures replace the earlier backdrop. Two premature animation/result captures were rejected during inspection and replaced after the actual UI state settled. One attempted verification setup used an invalid `responseLength` and received the expected 400; the valid `balanced` update completed the actual stale check. Remaining cancelled GETs are navigation/query retirement; no unexpected browser error was observed in the completed journey.

Sixteen settled captures live in `.impeccable/review/tenant-completion-read/`: `desktop`, `mobile`, `mobile-bottom`, `intermediate`, `wide`, `confirm-desktop`, `confirm-mobile`, `controlled-malformed-desktop`, `controlled-malformed-mobile`, `stale-desktop`, `stale-mobile`, `cancelled-desktop`, `authorized-desktop`, `authorized-mobile`, `saved-answer-desktop` and `handled-desktop`. Viewports are 1440×1100, 390×1000, 768×1024 and 1728×1100. The native Drawer measures 736px on desktop and fills mobile width; measured scroll/client widths match, footer actions remain reachable, and the saved text creates no script elements. Lower-scroll captures intentionally show the bottom of the same inspector. Keyboard screen-reader and additional-browser acceptance were not independently exercised.

The fresh independent Impeccable finish reviewer opened all sixteen valid captures and returned **disposition: ship**, no material fixes, for this bounded native-inspector extension. Type, material, ground, literal evidence, responsive footer and separate read/handled states matched the incumbent system. One detector pass on the new preview/shared inspector returned no findings. This presentation verdict does not establish automatic-report source-read consent or proposal acceptance. The fresh documenter compared source, context/sidecar and four representative captures, confirmed native system agreement and appended the extension/evidence note only to the existing inspector surface brief. PRODUCT, DESIGN, sidecar, CSS and assets remain unchanged; pre-existing sidecar drift stays uncorrected.

## Remaining goal work

Automatic-report source-read Review needs a separate durable producer/settlement and exact positive continuation, current source proof at every use, negative/expiry recovery and fair bounded selection across pending consent. The current fail-closed fence is a prerequisite, not completion of that family. Native background effects, autonomous origins/occurrences, Interrupt/Steer/voice, remaining System/audit families and final authority/schema consolidation plus requirement-by-requirement proposal acceptance also remain open. The original goal stays active. No CI, live provider, Real host, installed-data reset, PR, push or deployment was run for this checkpoint.
