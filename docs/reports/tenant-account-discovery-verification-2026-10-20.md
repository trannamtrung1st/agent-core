# Tenant account-family discovery verification — 2026-10-20

## Scope and status

Human-attributed conversational and immediate-background `email.accounts` / `drive.accounts` now reuse the canonical Credential metadata read/Review owner. Independent family reads filter exact pages, current compound human scope filters approval counts/details, and retained metadata is protected by atomic final answer/Run completion. This closes the account-discovery prerequisite left by the [account-action model/UI checkpoint](tenant-account-action-model-ui-verification-2026-10-20.md).

Read-only operation-ID reconciliation, abandoned account dispatch across cold application-host recovery, autonomous origins/watchers, remaining interaction/System/audit families and final authority/schema cutover remain open. The [full proposal](../../local/proposals/agent-core-multitenant-iam-approval-final-proposal.md) and original goal remain active. No CI, real provider call, deployment, push or new commit was requested or performed.

## Shared owner and boundaries

`TenantCredentialDiscoveryContract` derives the direct read's family from the offered tool and admits only bounded cursor/limit. Existing `inspect_credential_metadata` accepts an exact optional family restricted to selected capabilities. The existing `PreparedCredentialRead.ArgumentsJson`, exact saved references, approval/receipt and Run owners retain authority; no inventory table, migration or consent type is added.

`TenantCredentialRuntimeReadService` filters admitted/current purpose/capability and raw Credential metadata plus family reads before pagination. Current Session read remains independent. Metadata exposes saved connection identity/status/auth epoch and Synthetic indication, without provider I/O, protected values or account-use authority. Generic `credentials.list` retains its metadata-only behavior. Ordinary read Review supplies no missing raw permission and creates no exceptional grant. The same compound sources/policies/reviewer repeat before prepared data delivery and later model use. Background admission now includes this converted metadata Review alongside the existing native/file read families.

The page's private provenance binds every displayed record and the one additional record that establishes `hasMore`. Budget fitting preserves whole records and a progressing cursor. Current scopes filter human approval list counts/pages and detail before release; an unreadable frozen family cannot leak through a count.

`TenantCredentialReadReferences` and both owning IAM adapters repeat exact binding/Credential, current raw Session/metadata/family, direct read policy or exact approved receipt/reviewer checks inside final output transactions. The Session Runtime now selects its existing protected atomic answer path for retained Credential metadata as well as account reads/actions and protected completion work. Source loss records a metadata-read failure, saves no completed answer and terminalizes the Run truthfully. This adds no mutable conversation owner.

## Grouped local evidence

Current qualification comprises 129 distinct focused cases: 84 hosted API/model cases, 35 shared-owner cases and 10 contract/projection cases. Counts below preserve passing independent groups and do not add the initial failing attempts or the previous module's199 cases.

| Group | Cases | Result and retained log |
| --- | ---: | --- |
| Both families/stores: direct and exact Review, rejection/cancel, stale Agent family, hidden source, human approval visibility, direct final metadata/family revocation; filtered paging/lookahead; eight generic metadata Review regressions | 52 | Pass: `/private/tmp/tenant-account-discovery-qualified-model-tests.log` |
| After-Review final metadata/family revocation, background direct discovery, generic pending-metadata host recreation | 14 | Pass in `/private/tmp/tenant-account-discovery-background-final-review-tests.log`; four initial background Review failures are excluded and superseded below |
| Corrected background Review, exact family pending-Review SQLite host recreation with current versus newly revoked scope, independent policy with no alternate human | 12 | Pass: `/private/tmp/tenant-account-discovery-cold-review-fixed-tests.log` |
| Existing foreground draft/send and immediate-background Email/Drive action offering regressions | 6 | Pass: `/private/tmp/tenant-account-discovery-action-offering-regressions.log` |
| Existing metadata direct/retained Session policy, exact page preparation/delivery, source/role/reviewer changes, empty page, inbox visibility, refusal suppression and SQLite guard regressions | 35 | Pass: `/private/tmp/tenant-account-discovery-owner-regression-tests.log` |
| Closed family arguments, exact Review/hash/resource targets, selected family schema, binding/origin boundaries and affected all-capability tenant projection | 10 | Pass after final selected-family schema: `/private/tmp/tenant-account-discovery-selected-schema-tests.log` |

The52-case group uses actual model tools, canonical connected-account setup and authenticated HTTP decisions. Read-only roles deliberately omit `credentials.use`; API output includes safe saved connection facts and states separate authorization. Final revocation is injected after the result reaches the model and before it yields its answer. Expected and observed: current metadata/family loss produces `protected-output-unavailable`, no final result and no completed private assistant answer. Review delivery has the same final boundary. Hidden earlier aliases do not displace the first readable alias, affect `hasMore` or appear in result witnesses. A valid lookahead remains separately witnessed; revoking only that extra record still refuses the final saved answer.

Human visibility cases use a separate authenticated member: approval totals are zero and exact detail is403 without the frozen family read, then one/200 under the exact Credential family scope, then zero/403 after revocation. This is actual Memory/SQLite list/detail behavior, not post-query filtering in a client.

The background group creates a real child Session through ordinary `background.start`, uses its exact metadata owner and optional Review, and finishes through `work.complete`. The final result stays child-owned as BackgroundOutcome; no account send/write or fan-out is executed. The cold-family group disposes the first complete SQLite WebApplicationFactory while exact family Review is pending, recreates the application with the same database/key/file roots and inspects the unchanged saved request. Valid approval delivers only that page; newly revoked Agent family scope disables decisions while preserving cancellation and returns no metadata. All fixtures are Synthetic and disposable; actual model groups assert zero outbound provider HTTP, and cold-family fixtures assert zero OAuth transport attempts.

## Failures found and corrected

The initial32-case matrix passed24 ordinary cases and failed eight final metadata/family revocation cases. The new store guard refused the old Runtime path's post-save Run completion, leaving the Run Running: metadata had not yet selected the existing atomic protected-answer path. A diagnostic reproduction captured the exact store → finalizer stack in `/private/tmp/tenant-account-discovery-final-diagnostic.log`. Wiring the metadata source marker into atomic answer selection fixed it; the reproduced case passes in `/private/tmp/tenant-account-discovery-final-fixed.log`, and both the corrected full52-case group and eight post-Review final cases pass. No failing attempt is counted as qualification.

Background direct discovery passed, but four background Review cases initially reached the old protected-intent admission refusal and returned no metadata. Adding converted metadata intent to that existing admission boundary fixed the gap; all four re-executed Review cases pass in the12-case final group. The earlier log intentionally retains its four failures and14 passing independent cases. No duplicate provider effect or background child was used to repair either issue.

## Final checks and limits

The complete backend integration built cleanly before runtime qualification in `/private/tmp/tenant-account-discovery-build.log`. The final solution build passes with zero warnings/errors in `/private/tmp/tenant-account-discovery-final-build.log`; changed-document link/fence/untracked-whitespace verification and `git diff --check` pass. No frontend source, visual world, design token or shipping raster was changed in this module; the existing inspector renders server-saved family labels through its incumbent metadata composition. Dedicated discovery browser/visual journeys were not run. Actual hosted HTTP/model and cold-host behavior are the functional evidence for this backend change; previous account-action browser evidence retains its original scope.

Existing typed metadata payload storage already admits bounded canonical JSON arguments. Both stores use the same parsed family, frozen references and authority checks; no historical migration was edited. No full milestone or proposal acceptance is claimed. Operation reconciliation and the remaining proposal inventory continue under the original goal.
