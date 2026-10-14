# Drive credentials, exact Trash and compact file actions — successor verification

Reviewed baseline: `719ac6c55afa4a4b1613c5c7c437c4bf1aba11e2`. The branch also incorporated unrelated committed CSS cleanup `32f5f244` during the run; that change was preserved. Behavior source: [`f73c56f1bd8941a3a62ec655c1add22854a3d597`](https://github.com/trannamtrung1st/agent-core/commit/f73c56f1bd8941a3a62ec655c1add22854a3d597). The pre-commit parent also includes unrelated TODO update `3bd5ec0c`; it was preserved. This evidence-only update changes no behavior. The user explicitly requested local verification without waiting for hosted CI. Historical hosted results are not acceptance evidence for this successor.

Implemented operation-specific parent preflight and safe stage diagnostics; trusted account/file/version approval preparation; closed 1–20-item exact-approved batch Trash using existing durable per-item receipts; General Assistant v28 opt-in; compact provider-neutral Artifact/Workspace actions; one purpose-specific credential creation modal; consistent saved Gmail/Drive connection tags; shared Google OAuth client settings with independent grants and protected client provenance.

The batch stops on the first known failure or uncertainty, reports partial counts without rollback and never blindly repeats a PATCH. Existing versions do not gain the batch tool. Trash remains recoverable `trashed=true`; folders and permanent deletion are excluded. Destination validation remains for creation/upload/move; source membership comes from target metadata.

The generic Save UI, browser services, source-specific routes and obsolete tests/styles were removed after inventory found only those UI consumers and the Compose test fixture. The Compose fixture now invokes the same authorized conversational upload path through SignalR, preserving exact source bytes and verified receipts. Read-only operation lookup and agent import/export remain.

## Observed functional evidence

- Parent-404 fixture reproduced two failures before dispatch (Trash and rename); the narrow fix passed both without any parent GET. Destination validation and source-membership regressions also pass.
- Batch integration exercised 1/7/20 files, rejected 21/duplicates/unknown fields, stale whole-manifest preflight, exact hash/order/account checks, revoked binding, known failure and lost responses before/after effect. Completed/uncertain receipts are inspected before replay; the manifest is copied before awaiting a gate/provider, and a caller-list mutation test preserves every original target and the out-of-scope sentinel.
- Actual SQLite reopen preserves seven per-file receipts and the protected account key ring without repeat PATCH. An official fake-clock 60-second deadline during the first effect yields one uncertain item and six NotAttempted; reopen reconciles the first and completes only the remaining six.
- Playwright MCP created and consented a Synthetic Drive account through Add credential, bound it to v28, generated eight disposable fixtures through normal tools, and opened a trusted seven-file approval. Escape rejected it, restored Message focus and left eight available files. A fresh approval confirmed with keyboard Enter trashed exactly seven; independent list readback left one sentinel. Completion reported requested 7, trashed 7, failed 0, uncertain 0, not attempted 0.
- Batched UI inspection covered 320/390/768/1024/1440 widths, long Unicode filenames, inert names and bounded approval scrolling. Cards retained two labeled icon-only actions with no document overflow. One confirmation round used vertically stacked Drive detail labels and checked single Trash rejection, purpose switching with secret/name wipe, and focus return to Add credential. Functional browser reruns additionally cover saved Custom endpoints and Gmail safe draft restoration.
- OAuth fixtures validate canonical uppercase settings, separate callbacks/scopes/tokens, own legacy-client refresh and encrypted provenance on rotation. Removing the legacy client blocks before refresh HTTP and retains the existing protected payload/profile/epoch. No live Google request or token migration was performed.

## Gates

| Local check | Observed result |
|---|---|
| Domain suite | 196 passed, including durable batch argument bounds without widening old tools |
| Application suite | 1,807 passed / 8 configured live skips; isolated mode-transition race passed, then full rerun passed |
| API suite | 603 passed / 3 configured live skips; final changed Drive matrix additionally passed 30 cases |
| Infrastructure suite | Final complete rerun: 1,383 passed / 10 configured live skips. Final Drive/OAuth/SQLite/fixture subset also passed 45 cases. Earlier failed catalog assertions remain intermediate evidence and were corrected for the intentional v28 opt-in. |
| Event plugin / monitoring scripts | 4 xUnit and 3 Python tests passed |
| Frontend full suites | 1,009 passed across two local shards; final approval recovery/accessibility subset 12 passed |
| TypeScript/Vite | Passed; existing SignalR annotation/chunk-size warnings remain |
| Affected Synthetic browser matrix | All 12 distinct Drive Office, Gmail/Custom creation, monitoring and General CRUD/binding scenarios passed across final runs; final Drive accessibility/approval journey passed in 28.3s |
| Compose survival | Passed: separate Gmail/Drive grants, exact remote bytes, same receipt identity, key ring, bindings and workspace persisted across recreation |
| Docs/source hygiene | 21 canonical Markdown targets/fences checked with zero issues; new definition/sidecar JSON parsed; git diff whitespace check passed |

Local logs are under `/tmp/agent-core-drive-credentials/`. Full frontend checks used Node 22, one worker per shard and a 120-second local per-test budget after measured 30–50-second DOM rendering timeouts on this workstation; repository/hosted limits and assertions were unchanged. No hosted CI result is claimed. Failed intermediate logs remain separate from final checks. No dependency or license change was introduced: existing test protocol dependencies are reused by the Compose fixture.

Mandatory accessibility followups measured 40×40 Artifact actions at 320px and verified exact original DOCX ZIP bytes on download. The native solid-red Trash CTA measured 4.22:1 text contrast; the final outline CTA reuses error hover text over the existing container token, measuring about 6.0:1. Border contrast also exceeds 3:1. Token palette and layout owners remain unchanged. [Final 320px trusted approval](assets/drive-credentials-2026-10-14/approval-320.png) captures the final action style and bounded Unicode filename list. Legacy incomplete single-file approval displays recovery guidance, remains rejectable and requires fresh trusted preparation; it never becomes a batch or gains authority.

Resolved verification/setup issues include disk exhaustion (SQLite Error 13 / Playwright ENOSPC / Docker read-only cache), fixed v28 catalog assertions, an RTL test option typo, corrected Custom preset submission and updated browser selectors. Experimental timer scaffolding was replaced by the existing Infrastructure fake-time fixture. No product test was removed to conceal a failure. Process termination was restricted to freshly verified task-owned processes after automatic review rejected an insufficiently verified PID list.

## Unverified / deferred

Real Trash in an authorized disposable account and actual operator client migration remain blocked: no dedicated fixture/account scope was supplied. No existing user files were touched. Hosted CI is intentionally not awaited. Prior deferred desktop Office-client fidelity and native browser zoom/manual device checks remain unchanged; local Synthetic success is not live Google acceptance.
