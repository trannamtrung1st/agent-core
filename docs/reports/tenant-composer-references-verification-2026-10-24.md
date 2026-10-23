# Tenant Composer references — scoped verification

Date: 2026-10-24. This restores the existing feature through tenant IAM, following the user's explicit reference-restoration decision. It qualifies one complete Composer integration slice; the original multitenant proposal and its final acceptance remain in progress.

## Delivered behavior and authority

- Restored the incumbent Lexical editor, typed chips, unified `@` discovery and explicit `/` Skill invocation. The picker uses `/api/v2/tenants/{tenantId}/agents/{instanceId}/composer`; the removed owner route stays absent. Existing Ant Design controls, editor/chip styles, queue snapshots, attachment intake and Stop/Steer behavior remain the owners.
- All six reference kinds resolve actual selected-Instance tenant sources: home files, conversations, background work, Artifacts, Runs and Skills. Discovery requires the current authenticated human, communication/Agent access and actual per-source reads. It returns metadata, preserves independent native cursors across filtered pages, and does not consume bodies or mark work read.
- HTTP and actual MessagePack admission accept normalized typed parts with canonical fallback text. Core supplies labels after human source checks. Label-free normalized parts participate in durable message identity. Changed parts under an accepted event conflict; accepted-input replay rechecks current human read authority without demanding that an old selected revision be selected again.
- Executor reads remain independent. Reference bodies require current owned executor grants, requester-aware operation policy and pinned/current state isolation. Human selection does not grant access or satisfy mandatory review. `forbidden` and `requiresApproval` evidence has no body. Existing supported source-read authorization operations remain available; this slice adds no dedicated reference approval operation or exception grant. Where no supported read operation can satisfy mandatory review, the body remains unavailable.
- Bounded text evidence uses the existing private immutable `ComposerRunInput`: 4000 UTF-16 units per reference, 12000 aggregate and at most 64 KiB per file/Artifact read. Unsupported media, invalid UTF-8 and oversized sources are metadata only. Background evidence includes the original task/status/result and independently requires its Run read. Skill inspection does not activate its procedure; explicit invocation does.
- A reference to the current conversation uses its existing context and does not pin or duplicate its growing history. Cross-session isolation applies to sources in other Sessions; same-Session Run/Artifact references retain their independently authorized reads.
- Allowed source fingerprints and current authority are rechecked before model reentry, generated-content mailbox admission, live delivery and retained-source readback. A worker-side guard prevents already-consumed, subsequently revoked or changed reference content from producing a durable generated answer. Supersession/generation checks remain at the mailbox boundary.
- Ready/upsert/history retain typed parts only under the recipient's current source reads. Inaccessible references become `[Resource unavailable]` without dropping independently readable surrounding text.

No permission catalog, operation-policy default, schema migration, source store, execution framework or provider contract was added. Entry/Run JSON, tenant storage ownership and the existing runtime/approval owners remain authoritative. No Profile/LocalOwner resolver or retired endpoint was restored.

## Functional verification

Expected versus observed scenarios:

| Scenario | Observed outcome |
| --- | --- |
| Human-owned home selection, independent executor Allowed/missing/mandatory-review policy, both stores | Allowed body reached the actual Synthetic model request. Missing/review-required reads remained body-free with truthful status. Typed history and idempotent replay survived; changed parts and mismatched fallback were refused. |
| Current human lacks workspace/Skill reads | Home discovery and manual reference admission were refused; Skill discovery was empty. History retained surrounding text and withheld the locator/label. No additional model request occurred. |
| All six source kinds, both stores | Actual background admission/completion, a tenant Artifact and real workspace/history/Run/Skill sources reached bounded model context. The referenced OnDemand Skill stayed inactive. Foreign tenant/Instance selection was refused. |
| Source bounds and media | Eleven distinct references covered all six kinds, exactly 12000 aggregate characters, per-reference limits, unsupported binary, invalid UTF-8 and oversized text. The three unsupported bodies remained absent. SQLite reopen retained evidence and passed current-source readback. |
| Change file, revoke executor role or disable home access while generation waits, both stores | Consumed evidence was present in the first model request; after the boundary changed, the generated reply was absent from durable conversation history and source readback was refused. No second model request occurred. |
| Actual JavaScript MessagePack, both stores | Typed current-conversation selection, factual retry after Session revision changes, changed-part protocol conflict and unknown-field Validation refusal passed alongside existing attach/reconnect/logout/switch checks. |
| Canonical/retired transport inventory, both stores | 250 canonical method/path pairs match the manifest; all 228 retired pairs remain absent, including with old owner tokens and a signed-in platform human. |
| Native Unicode home pagination | Passed: actual native 40+1 pagination, UTF-8 cursor round trip, 8192-character bound and query-rebinding refusal, both stores. |
| Retained schema guards and refused lossy downgrade | Passed: every retained trigger name/body survives empty downgrade/upgrade and refused lossy downgrade; current-schema restoration preserves the saved operation. |

## Focused checks

**528 distinct focused cases passed, zero skips.** The affected store group ran once; its five stale fixtures passed focused corrected reruns. Passing groups were not repeated for unrelated fixture corrections.

| Group | Result |
| --- | --- |
| `TenantSessionAccessTests.ComposerReferences`, native MessagePack, plain tenant worker and HTTP cutover | 21 distinct passed, including the final two Unicode pagination cases. |
| `TenantRunAdmissionTests` | 421 distinct passed: initial 416 plus five corrected fixture cases in focused reruns. |
| `UserMessageContentTests` | 4 passed. |
| `UserMessageProjectionTests` | 4 passed. |
| Composer, choice discovery, tenant realtime, message parts and ChatApp Vitest | 78 passed. |
| Solution build | Passed, 0 warnings/errors; serial `-m:1 -nodeReuse:false -p:UseSharedCompilation=false`, no restore. |
| Frontend build | Passed; existing Vite large-chunk advisory remains. |

The broader store group ran once. Its stale fixtures were reconciled with current authority rather than weakening guards: a Memory receipt now has actual tenant/Instance/user ownership; a report reviewer needs exact Agent communication access in addition to approval/source reads, with refusal verified before that grant; the old thirteen-trigger assertion is replaced with retained trigger names/bodies and current-schema restoration after refused downgrade. No migration or installed data was changed.

The first negative output checks exposed a real integration defect: live delivery withheld source-dependent output, but it could still reach durable history. The worker-to-mailbox source guard fixes that path and all six change/revocation/isolation cases pass. Initial history conversion and keyboard checks also found the blanket reference placeholder and missing Ctrl+Enter Stop-permission guard; both were corrected in the grouped implementation.

The first long-path fixture exceeded macOS native path limits, including cleanup. Its disposable directories were removed with descriptor-based cleanup. The final fixture uses host-supported international paths, verifies native 40+1 pagination and lossless UTF-8 cursor encoding, and retains the 8192-character limit/query binding. This is not a claim that macOS supports every logical path allowed by the cross-platform ceiling.

## Rendered Synthetic UI

Actual Playwright MCP against an isolated Synthetic API/Vite pair:

1. Signed in through the product, selected the international home filename through `@`, sent a reference-only turn, received the Synthetic reply and verified typed durable history. Reload retained the chip and reply.
2. Selected `/Record lookup` by keyboard, sent the invocation-only turn, received a second reply and verified its stored invocation identity separately from the reference.
3. Opened the Add content picker, searched for no matches, observed loading/empty states, and used Escape to return focus. A controlled Artifact 503 retained successful choices; Retry requested the missing source and recovered without an error prompt remaining.
4. Inspected the shared picker at 320, 390, 768, 1024 and 1440 CSS pixels. Widths were 294, 364, 494, 640 and 640 respectively, all within the viewport; no document horizontal overflow occurred. Editor inner padding remained 8px. Native mobile choice/control hit heights were 44px, and live resizing re-anchored the picker.
5. Network inspection showed only tenant Composer routes. Normal journeys had no browser errors; the deliberate 503 was the expected failure/recovery observation.

No new CSS, generic control wrapper, UI kit or design-system mutation was needed. The disposable API/Vite servers were stopped. User databases and the Real host were not reset or restarted.

## Evidence and remaining boundary

Local logs: `/private/tmp/tenant-composer-{complete-api,last-api,cursor-final,store,store-fixture,reviewer-fixture,guards-final,domain,projection,complete-web,finish-build,web-qualification-build}.log`. Browser host evidence: `/private/tmp/tenant-composer-browser.6nwk3i/`. Earlier failed qualification logs are retained alongside these final runs for diagnosis.

CI and live providers were not requested or run. This does not establish proposal-wide security/isolation/UI acceptance, eliminate the remaining hash-bound inert serialization, or qualify every final integration combination. The existing broader goal remains active.
