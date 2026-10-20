# Tenant account-action execution owner — 2026-10-20

Status: internal backend implementation and focused verification; the connected-account module and the proposal-wide goal remain incomplete. No commit, push, hosted CI, live-provider call or deployment is part of this checkpoint.

## Implemented boundary

One closed `PreparedAccountAction` covers Core email draft creation, exact immutable draft send, Drive folder creation, original-byte upload/download, rename, move, trash and ordered trash batches. Preparation uses actual tenant Run/Session/Agent/binding ownership. It performs no provider I/O; remote target metadata comes from previously approved exact `drive.get` observations in the same task. Source/version/parent membership, upload source SHA/length and new destination observations are frozen.

Primary family authority supports ordinary Review or a primary-only exceptional grant. Raw Session, Credential metadata/use, Drive source reads and workspace source/destination authority remain independent. Preparation also checks secondary operation policies: Core's Sensitive minimum for `credentials.use` requires exact compound Review even when the primary draft/write policy alone would permit an action. Inbox filtering applies compound scopes before totals/pages.

The existing Run/approval/receipt transaction owns dispatch reservation and claimed grants. Core draft creation commits atomically with its local receipt, grant and Run update. The existing `IEmailStore` retains immutable draft/send claims; the existing `DriveService` retains native operations and exact generated file identities. Tenant callbacks prevent legacy Credential resolution and adoption of another root's pending native command. No new table, queue or executor is added.

The shared worker prepares external effects outside the mailbox; mailbox continuation consumes saved receipts only. Native Gmail/IMAP/Drive adapters remain operation-local and tokens stay inside Infrastructure. Current authority repeats around refresh/retry/chunks and source commits. Normalized OAuth rotation changes only secret revision; already retrieved observations of the same logical OAuth source remain current, while new I/O pins its current source and accepts only its own committed rotation. Password replacement, reconnect, binding or logical Credential changes invalidate source authority.

Acknowledged Gmail sends retain factual receipts after revocation. Private result details require current raw/source/reviewer authority inside settlement, before model reuse and at the final atomic output transaction. A Drive acknowledgement still requiring unreadable metadata verification remains uncertain. Unknown effects and abandoned claims never replay. Ordered batches retain completed items, the first refusal/uncertainty and the unattempted suffix.

## Focused evidence

The controlled Google adapters use a local `HttpMessageHandler`; no Google service or provider credentials are contacted. These are application/store/native-adapter integrations, not model-generated action acceptance.

| Group | Evidence and observed outcome |
| --- | --- |
| Actions and transfers | 38 passing cases across memory and temporary SQLite. Exact successor preparation/decision/reservation/settlement, atomic draft, primary exception, default-policy compound Review, controlled Gmail known/unknown sends, post-dispatch authority loss, scoped inventory counts, immutable consent, original binary upload/download, occupied destination/source drift, rename/move/trash and whole/partial ordered batches. Source metadata reads cross actual read consent, Google adapter HTTP and successor checkpoints before action preparation. Repeated continuation performs no additional native writes. |
| Closed action contract | 16 passing cases: nine canonical action round trips through authorization; injected/duplicate authority fields, unsafe recipients/paths, duplicate manifests and invalid move are refused; draft hash binds exact content/sender/account with Email epoch zero or one. |
| Account-read regression | 32 passing controlled real-adapter/store cases retained from the grouped run: both stores and Email/Drive, rotation, exact primary exception, invalid grant, revocation at token/resource gates, secret replacement, independence and missing family access. |
| Reservation recovery and schema | Three passing cases: abandoned claimed effects settle as Indeterminate after lease expiry and owner recreation without native calls; repeated recovery is inert; exact primary grants become Indeterminate; lossy action downgrade is refused. Empty action-schema down/up restores fifteen predecessor guards and returns to seventeen with model parity. |
| Existing read schema | Two passing cases: immutable read consent, lossy downgrade refusal, predecessor guard survival and current model parity under the follow-on schema. |
| Hosted Synthetic regression | 32 passing actual model/HTTP cases through the composed host and real worker: exact read consent, primary exception, rejection/cancellation, changed binding, missing family/metadata, current post-I/O and final-save access, and scoped inventory. This verifies host/DI/recovery composition for existing account reads, not new model-originated account actions. |
| Atomic final output | Four passing cases: retained acknowledged-send results permit a current atomic final answer; revoking raw access after result retention refuses provider reuse and the final owning transaction in both stores, leaving Session revision and Run terminal state unchanged. |
| Password and preflight boundaries | Six passing new cases: password Email read consent and atomic draft creation accept initial auth epoch/secret revision zero without provider I/O; a known invalid-grant preflight refusal records Denied with no native send and no draft-send claim, across both stores. |

Total: **133 distinct passing focused cases**. Later password/preflight runs repeated three already counted schema cases after the new zero-secret-revision shape; repeated cases are not added to the total. No focused test failure remains.

Logs: `/private/tmp/tenant-account-action-transfers-fixed-tests.log`, `/private/tmp/tenant-account-action-contract-tests.log`, `/private/tmp/tenant-account-action-grouped-tests.log`, `/private/tmp/tenant-account-action-api-regression-tests.log`, `/private/tmp/tenant-account-action-final-output-tests.log` (four final-output passes; password failures superseded by the next log), and `/private/tmp/tenant-account-action-password-preflight-tests.log`. The earlier grouped run's 16 transfer failures came from a test helper resetting checkpoint accounting between approved reads. Correcting the helper preserved the runtime's counters; all 20 transfer journeys then passed. The 57 already passing cases were retained; changed action-policy/visibility paths were rechecked with the passing 38-case action/transfer group.

The default parallel build stalled before compiler output and was explicitly terminated after checking its live process/stack. Isolated builds use `--disable-build-servers -m:1 -p:UseSharedCompilation=false`. A sandboxed test run compiled successfully but its test runner could not bind a loopback socket (`SocketException (13)`); focused tests run outside that restriction. The initial action group exposed a missing saved preview branch, then SQLite semantic-root matching. Both product defects were fixed before the passing action group. Subsequent password boundary tests found an OAuth-only positive secret-revision assumption; initial password credentials use revision zero. The shared witness validator and follow-on schema were corrected and all password/preflight/schema checks passed. The final solution build (`/private/tmp/tenant-account-action-final-build.log`) passed with zero warnings/errors. Documentation checks covered 20 changed Markdown files, 1,978 relative link targets and 22 complete JSON examples; `git diff --check` passed. No frontend implementation, browser qualification, full local CI or hosted CI suite was run for this internal slice.

## Persistence

`20261020160000_TenantAccountActionReview` freezes the predecessor SQL and all fifteen guards, adds exact source insertion and immutable account-action consent guards, and keeps the actual Credential FK for workspace-download primary scope. Current approval/receipt trigger count is seventeen. The follow-on shape accepts initial auth epoch and secret revision zero for password Email accounts while Drive requires positive witnesses. Downgrade refuses retained account-action consent or a read requiring the new zero-epoch shape. Previous migrations are unchanged.

## Remaining qualification

- Offer all supported account intents through canonical tenant model projection and verify actual model/HTTP workflows.
- Complete shared Organization/Chat approval presentation, literal exact email content and remote source/transfer review, functional Synthetic browser checks and fresh finish review.
- Convert Agent `email.accounts` / `drive.accounts` discovery and exact owned operation-ID read-only reconciliation.
- Qualify dedicated immediate-background account workflows and actual cold application-host pending/in-flight recovery; store-owner recreation is narrower evidence.
- Qualify Custom IMAP/SMTP native actions and larger multipart/chunk authority-loss cases. Password consent/schema checks alone do not prove a live SMTP/IMAP workflow.
- Complete autonomous origins/watchers, remaining interaction/voice/System/audit families and final authority/schema cutover, then audit the full proposal requirement by requirement.

The internal execution owner does not establish connected-account module acceptance or full proposal completion. Tenant model schemas do not yet advertise these new account-action intents, and their shared UI is not qualified.
