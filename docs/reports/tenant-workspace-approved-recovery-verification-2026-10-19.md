# Approved Workspace effect host recovery — 2026-10-19

This closes the bounded abandoned approved-effect host-recreation gap left by mutation checkpoint `5b77b01c`. It does not complete the full multitenant proposal or qualify a physical process kill, arbitrary external effects, Office or virtual-resource conversion.

## Exercised boundary

`TenantSessionAccessTests.WorkspaceMutationReview` adds two SQLite journeys: `unsettled-review` and `unsettled-grant`. Both run the actual model-offered exact mutation intent through durable waiting, authenticated HTTP inspection/approval, a claimed successor and canonical native file replacement. Only the existing workspace-operation settlement port is made unavailable in the initial host. The write completes, the caller receives terminal `side-effect-indeterminate`, the dispatch persists, and no receipt is invented. An exceptional grant remains Claimed; ordinary Review creates no grant.

The fixture verifies actual retained bytes, a single dispatch and absent receipt, then revokes the executing Agent's role. It disposes the entire first host and constructs a fresh SQLite host with the same disposable store and file roots. The normal hosted recovery pass produces one request/operation/action-hash/successor-bound **Indeterminate** receipt despite lost execution authority. The exceptional grant becomes Indeterminate. The recreated model receives no request, and an instrumented native write adapter records zero entries before I/O, so a rejected replay attempt would also fail the test. Original bytes remain the exact once-written proposal. Audit contains one dispatch and one result; a further recovery pass returns zero and leaves the immutable receipt unchanged.

This is a controlled settlement outage followed by complete host disposal/recreation. It proves retained factual recovery without replay. It does not simulate an operating-system process kill, dispatch-before-I/O loss or every possible filesystem failure; the existing [effect and continuation report](tenant-workspace-approved-effects-verification-2026-10-19.md) records the separate lower-level uncertainty and ownership matrix. The existing completed/unknown and pending-consent restart cases remain in this focused run.

## Executed checks

The initial command compiled successfully but the sandbox denied the test runner's local communication socket before any test executed (`/private/tmp/agent-core-workspace-mutation-cold-recovery.log`). That aborted attempt is not passing test evidence. The already-built focused run was repeated with local socket permission:

```sh
dotnet test tests/AgentCore.Api.Tests --no-build --no-restore --filter 'FullyQualifiedName~Tenant_workspace_mutation_Review_model_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

Result: **13 passed, 0 failed, 0 skipped**, terminal exit 0, duration 1m14s (`/private/tmp/agent-core-workspace-mutation-cold-recovery-qualified.log`). The native write probe was then strengthened to count before dispatch, and the same focused filter was rebuilt/rerun without `--no-build`: **13/13 passed again**, terminal exit 0, duration 1m14s (`/private/tmp/agent-core-workspace-mutation-cold-recovery-final.log`). This is two new cases plus eleven existing focused regressions, not thirteen new distinct cases. Both new cases observed zero provider HTTP attempts through the fixture's outbound probe. No product runtime or frontend source changed in this checkpoint, so no additional UI/build/polish run was required; the prior [shared mutation presentation evidence](tenant-workspace-mutation-review-verification-2026-10-19.md) retains its original scope.

## Remaining proposal work

Workspace virtual resources, Office tools and source publication, autonomous origins, other resource/transport families and final clean authority/schema cutover remain open. A physical process-kill experiment is unrun and no claim is made for it. No CI/full suite, live/paid provider, installed/user-data migration/reset, Real host restart or deployment was performed. All host/store/file roots in these tests are disposable fixture-owned data.

Changed documentation passed relative-link/fragment and fence validation across six Markdown files, 1,010 local links and 96 fragment references. `git diff --check` passed.
