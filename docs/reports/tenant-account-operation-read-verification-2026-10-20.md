# Tenant read-only Drive operation reconciliation verification — 2026-10-20

The existing account-read consent, Run, receipt and protected-output owners now support exact `drive.get` with `operationId` instead of `fileId`. The original canonical action request/receipt, effect-owning Run/Session and immutable native intent references are frozen in optional account-read source evidence. Only the same canonical Agent/account/binding/logical Credential/auth epoch can resolve native operation identities. Bare native or legacy IDs never supply authority.

Current original Session and Run reads are independent of account metadata/read/use. A new human activation can inspect a prior operation after the old write/delete/workspace permissions are removed. Source and reviewer scope repeat at decision, native read, checkpoint reuse and the final owning output transaction. The original canonical effect receipt is never rewritten.

The existing native Drive inspector performs metadata verification only. Retained success is labeled `retained_verification`; new verification is `current_remote_metadata`. Missing evidence stays unknown. Unconfirmed verification stays indeterminate. A known trashed upload target retains failed verification proof rather than claiming no effect. Upload inspection sends no bytes or resumable request. Ordered batch inspection never enters the batch write owner or attempts an unattempted suffix. A prior download returns only its recorded outcome, without local-path or byte-verification claims.

## Executed focused evidence

All tests use synthetic providers or controlled offline Google HTTP adapters, without real provider credentials or external effects. Counts below are distinct cases; diagnostic reruns are not added to the total.

| Group | Cases | Observed outcome |
| --- | ---: | --- |
| Reconciliation store/native integration | 54 | Memory/SQLite parity across all seven Drive actions; immutable original receipts; exact separate consent; retained and uncertain accepted effects; no repeated writes; partial batch suffix left untouched; upload inspection without content/status-probe replay; known trashed upload failure; missing native rows; bare native-only ID refusal; independent original Run/Session reads; rejection/cancellation; forged source refusal; SQLite owner recreation; source-Run revocation before retained reuse and atomic final save. Two additional SQLite cases directly prove insertion triggers reject forged canonical source links and malformed native references before unrelated duplicate constraints. |
| Empty migration recovery | 2 | Downgrade through existing read/action predecessors and return to current schema preserves predecessor guards, restores all 19 approval/receipt guards and has no pending EF model changes. Retained operation-source consent separately refuses downgrade and source mutation in the reconciliation cases. |
| Closed read/action/source contracts | 46 | Exact selectors, source hashing/rebuild, independent raw source targets, malformed/duplicate/widened arguments and existing read/action contract regressions. |
| Hosted model/HTTP journeys | 14 | Both stores, all seven Drive actions: actual model offering, saved action, later exact operation-read consent, worker continuation, factual read result and completed answer through the protected final-output owner. |
| Actual SQLite application-host recreation | 2 | Dispose/recreate the composed host while operation-read consent is pending. Saved request and original/native receipts survive exactly. Approval completes the inspection; revoking only original Run access disables decision and permits cancellation without inspection. Neither path changes the original/native receipt or sends external HTTP. |
| Shared account-owner regressions | 71 | Existing exact account reads, OAuth rotation/refusal/revocation, native actions, original-byte transfers, partial batches and immutable ordinary read consent continue to pass. |

**189 distinct focused cases passed.** The final solution build passed with zero warnings/errors. Relative Markdown links/fences and whitespace checks passed. No CI or full local CI suite was requested or run.

The grouped implementation/test/fix rounds corrected SQLite GUID-to-JSON comparison, independently applied source visibility before counts/pagination, raw SQL grouping, and an upload fixture that had treated a zero-byte status probe as content. Passing independent memory/contract/model groups were retained; only affected groups and added boundary cases were rechecked.

## Evidence files

Local logs: `/private/tmp/tenant-account-operation-tests.log` (initial 27 passing cases plus diagnostic failures), `/private/tmp/tenant-account-operation-corrected-tests.log` (six passing cases, including corrected memory uploads, plus diagnostic SQLite visibility failures), `/private/tmp/tenant-account-operation-sqlite-final-tests.log` (seven passing cases, including empty migration and memory folder cases, plus diagnostic SQL grouping failures), `/private/tmp/tenant-account-operation-qualified-store-tests.log` (28 passing corrected/source-boundary cases), `/private/tmp/tenant-account-operation-insert-guards.log` (two passing insert guards), `/private/tmp/tenant-account-operation-contract-tests.log` (46), `/private/tmp/tenant-account-operation-model-tests.log` (14), `/private/tmp/tenant-account-operation-cold-host-tests.log` (two), `/private/tmp/tenant-account-operation-owner-regressions.log` (71), and `/private/tmp/tenant-account-operation-final-build.log`.

## Qualification boundaries and remaining proposal work

No frontend source changed: the shared native read inspector receives exact arguments and literal reconciliation/source disclosure through its existing fields. This checkpoint includes HTTP inspection/decision and actual hosted backend outcomes; a dedicated reconciliation browser journey or refreshed visual verdict is not claimed. No real Google recovery, external OAuth flow, physical process kill or abandoned native dispatch cold-host qualification is claimed here. Email send uncertainty remains factual and unreplayable; no arbitrary email-send replay/reconciliation tool is introduced.

This supersedes read-only operation-ID reconciliation pending statements only for the exercised human-attributed paths. Dedicated abandoned-dispatch application-host qualification, autonomous origins/occurrences/watchers, remaining interaction/voice/System/audit families and final authority/schema consolidation remain open. The full original proposal and goal remain active; no milestone-wide or whole-proposal acceptance is claimed.
