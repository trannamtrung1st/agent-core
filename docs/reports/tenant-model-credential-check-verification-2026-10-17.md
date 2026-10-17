# Model-requested tenant credential checks — 2026-10-17

Successor to the [trusted credential execution checkpoint](tenant-credential-runtime-check-verification-2026-10-17.md), after `2098e8ec`. This delivers model initiation, host dispatch outside the mailbox and exact successor consumption for a bound Google connection check. It does not complete the proposal or a milestone.

## Delivered flow

The existing `approvals.request_authorization` function accepts `check_credential_connection` with one normalized binding reference. It accepts no selected tenant, principal, credential/binding ID, permission, grant or revision witness. The closed intent matcher distinguishes credential checks from persona/preferences. Offering requires pinned bindings and Email/Drive account capability; the offered schema omits persona/preferences when Settings is disabled. Preparation independently resolves the actual current target, metadata-read authority, credential-use decision and mandatory Sensitive review. Credential eligibility does not bypass Settings management for local configuration operations.

Runtime uses its existing persistence barrier and off-mailbox preparation to save the exact wait and release the claim/capacity. The human decides through the existing cookie/CSRF approval interface. The processed signal atomically links source Run A to exact successor B, preserving the requester, root, response, frozen configuration and remaining budget. The shared-capacity tenant host validates the current claim, reserves capacity and invokes the credential continuation before mailbox dispatch. Provider I/O therefore never enters the Session Runtime mailbox.

Both service and transaction validate the actual source handoff before acquiring a new dispatch fence. The existing credential service performs at most one logical check. `ResumeAsync` consumes its immutable receipt, completes the exact saved tool call and supplies scoped result evidence to the model. `applied` is true for completion, false for definite denial and null for uncertainty. Provider time consumes the pinned active budget, while the human wait remains paused. Lease and 30-second provider bounds remain in force.

An uncertain receipt permits only the final response function while current authority and budget allow, preserves the Indeterminate effect marker and leaves B failed. The immediate-fault hosted cases verify a persisted factual uncertainty response. Checkpoint saving does not reinterpret this recorded credential result as a browser interaction requiring observation. No retry, new effect authority or browser recovery is introduced. Known host pre-dispatch checkpoint/authority/budget failures terminate the same current claim without model/provider work instead of leaving it idle until lease recovery.

## Executed scenarios

The hosted API fixture uses the actual production worker, coordinator, Runtime, durable approvals and both store implementations. It creates/connects/binds an owned Synthetic Drive credential through HTTP, disables Settings management, grants scoped executor read with or without ordinary use, submits text, observes the model-issued wait and decides through HTTP. A normalized verifier fixture counts calls without networking; this is separate from the earlier offline real Google-adapter transport coverage.

Seventeen cases cover Review and ExceptionalGrant approval, rejection, request cancellation, uncertain provider failure, executor-read revocation after approval, SQLite host recreation before the human decision, and immediate host failure for changed pending arguments or exhausted active budget. They verify exact B receipt ownership, preserved response/requester/root lineage, one or zero provider calls as appropriate, completed pending transcripts, truthful outcome data, response-only uncertainty and secret-safe API boundaries. The existing HTTP cases separately verify persisted audit readback. The recreated SQLite host uses the same disposable database and protection keys with a fresh authenticated client; this is host recreation, not an OS reboot.

Persisted response text is checked through a fresh trusted Runtime frame admitted for the same owned Session, and the inspection Run is then cancelled. Metadata APIs intentionally return no conversation entries. Private execution has no recipient acknowledgement: these checks do not manufacture received/heard prefixes, imply that unseen output enters later model history, or claim live browser delivery.

Six additional both-store integration cases preserve a 10-second checkpoint across a 20-minute human wait, charge exactly two seconds of provider work, replay the saved receipt without another verifier call, reject altered pending references before grant consumption, and close an over-budget provider result as uncertain with zero remaining execution budget. Strict contract tests reject mixed/duplicate/authority-bearing arguments and validate capability/schema projection and operation suppression under uncertainty. Synthetic script tests reject invalid references or unavailable tools and recognize the saved typed call across the continuation task boundary.

Implementation checks caught and corrected missing source-Run propagation at the credential effect boundary, a generic unsafe-effect checkpoint flag that incorrectly selected browser observation recovery, and a Synthetic fixture that initially relied on the source user marker instead of the saved call. Early fixture assertions used the intentionally empty metadata entry list and then assumed unseen private output would appear in model history; the retained-byte check now uses the scoped Runtime port without weakening those production boundaries.

## Focused verification

All commands use disposable Synthetic fixtures, no hosted credentials and no provider network. Earlier failing development runs are not counted as passing evidence.

| Selection | Result |
| --- | --- |
| Infrastructure: `TenantOperationAuthorizationTests` OR `TenantPreferenceOperationTests` OR `TenantCredentialIntegrationTests`, before adding the successor cases | 401 passed |
| Infrastructure: `Credential_successor_consumes` | 6 passed |
| Infrastructure: `SyntheticAuthorizationAuditTests` | 5 passed |
| Application: `TenantApprovalToolContractTests` | 7 passed |
| API: `Model_credential_check_waits` OR `Credential_approval_HTTP_projects_exact_consent` OR `TenantSessionAccessTests` | 61 passed |

**480 distinct focused cases passed**: 412 Infrastructure, 7 Application and 61 API, with none skipped. The 412 Infrastructure cases are the three disjoint selections above; earlier four-case and narrow hosted runs overlap the final selections and are not counted again.

Commands are `dotnet test tests/AgentCore.<project>.Tests --no-restore --filter '<selection>' --logger 'console;verbosity=minimal'`; the final API regression pass uses `--no-build` after the focused hosted build. Changed Markdown local links/fences and `git diff --check` also passed.

## Remaining boundaries

No frontend source changed. Existing inspector browser evidence remains separate; this work verifies the backend journey through actual HTTP/model/worker execution. Live tenant Chat/output, original-Chat approval links/notifications, active tenant Run cancellation, live Google acceptance, other credential/external operations and the remaining tenant resource/transport/obsolete-authority/schema cutover are still required. No optional CI, deployment, installed-data migration/reset or Real-host operation was performed. The full goal remains active.
