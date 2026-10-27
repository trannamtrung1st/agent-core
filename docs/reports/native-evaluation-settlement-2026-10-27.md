# Native evaluation settlement contention — verification

Date: 2026-10-27. This bounded follow-up applies to the implementation committed with this report, based on `1f1a31e3039becae54b8bd3b86994f3562a8d16d`. The original [native delivery report](native-evaluation-scripts-2026-10-27.md) remains unchanged.

## Finding and fix

The supplied review finding was reproduced on both InMemory and SQLite. A deterministic competing transaction completed the Manual finding and another native finding after the publisher read the Attempt but before its CAS write. The original publisher marked the Attempt Indeterminate; both pre-fix regression cases failed their expected Completed/Passed assertion.

The fix stays within the existing evaluation/store/OpenSandbox owners:

- Finding publication makes at most three reread/revalidate/write attempts, merging only its exact native finding into current immutable evidence. Ordinary CAS conflicts no longer invoke unavailable-authority recovery. Persistent publication contention leaves Pending/Inconclusive and retries through the incumbent sweep after 30 seconds, without dispatch.
- Confirmed source revision/fingerprint changes and unavailable authority still fail closed. Technical recovery now requires the observed Attempt revision on both stores; SQLite also returns a CAS miss if that recovery transaction loses a concurrency race.
- The OpenSandbox adapter makes at most three retained result-commit attempts, with fresh execution state and full current authority validation for every conclusive commit. It preserves actual exit, output digest and confirmed cleanup. The retry loop never repeats create, command or cleanup. Cancellation and real authority failures remain latched Inconclusive; exhausted commit contention makes one CAS-fenced `sandbox_evaluation_settlement_conflict` / Inconclusive fallback write, leaving any further CAS miss to existing no-replay recovery.

No migration, endpoint/schema change, scheduler, UI work or architecture redesign was needed. The existing API test's store forwarder was updated for the internal recovery CAS argument.

## Executed verification

Commands used `--no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false` where compilation was needed. CI and the full CI suite were not run.

| Check | Result |
| --- | --- |
| Infrastructure filter `Sandbox_finding_settlement_merges_contention_without_replaying`, before fix | 2 reproduced failures: expected Completed, observed Indeterminate |
| Same filter after the first fix | 2 passed |
| Expanded filter, four modes × two stores | 8 passed |
| Combined expanded filter plus opt-in `Native_evaluation_executes_exact_reviewed_script` against actual local OpenSandbox | 10 passed, 0 failed, 0 skipped |
| API filter `Owned_resource_upload_evaluate_publish_and_read_survive_fork_without_hash_or_tenant_bypass` | 1 passed; production API and updated store-forwarding fixture compiled |
| `git diff --check`, changed Markdown local links/fences | Passed |

The eight deterministic cases exercise actual IAM, source Run, Script/Suite, exact Review and CAS stores. They seed already-completed technical receipts to isolate publication behavior; the separate native cases execute real commands. No successful mock command is presented as native execution evidence.

Both-store publication outcomes:

1. Manual and another native finding commit first: publisher rereads, preserves both findings and completes Passed. SQLite reopening preserves that outcome.
2. Two unrelated tenant revisions win before publication: bounded retries complete Passed without unavailable-authority recovery.
3. Every publication CAS is rejected: three writes per native finding, Pending/Inconclusive, no unavailable-authority write and no dispatch. A later existing-sweep pass publishes retained evidence and completes Passed.
4. Original human signs out during contention: Indeterminate/Inconclusive, preserving fail-closed behavior. A stale technical-recovery Attempt revision is rejected in every mode.

Both actual native journeys additionally verified:

- Two unrelated tenant role changes after final authority validation: three validations, one command, exit 0, Passed and confirmed termination.
- Persistent tenant contention at all three result writes: one command, exit 0 retained as technical evidence, Inconclusive with the dedicated contention reason, confirmed termination.
- Sign-out after final authority validation but before commit: the transactional login fence prevents a pass; native and aggregate results remain Inconclusive.
- Existing nonzero assertion, timeout, cancellation, concurrent isolation, before-dispatch revocation, exact Script pins, missing input and no-replay restart cleanup checks still pass. Factual source Run revision and workspace sentinel remain unchanged.

The native tests used the preexisting authenticated local server and immutable installed image `sha256:985c3477b1bcf54385a33c048e465ed6bf7f590d09e9dd4bbfae06d62af91561`, with per-fixture UUID mount roots. No provider inference, server restart or general UI polish was performed.

## Review verdict

The reported reliability issue is resolved: transient contention preserves valid evidence, competing immutable findings are merged, genuine revocation remains fail-closed, and retries cannot replay commands. Internal port/store callers and canonical backend/testing contracts are synchronized. There is no new migration or deployment configuration requirement.

Initiative 7's local implementation is closed with this targeted hardening; Initiative 8 — Session Goals can proceed. Hosted CI, production certification, rejected native hooks and incumbent sampled storage limits retain the qualifications recorded by the original delivery report.

Final cleanup check: authenticated Sandbox inventory and provider workload-container count were both zero. The preexisting Sandbox server was retained unchanged.
