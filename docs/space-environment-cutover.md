# Space and Environment storage cutover

This is an operator procedure for the breaking Space ownership release. Local qualification is recorded in the [verification ledger](reports/space-environment-implementation-2026-10-25.md); consult its deployment limits before serving traffic. The [model/main integration](reports/model-space-main-integration-2026-10-25.md) qualifies the combined schema and model/context execution boundaries. Development tests use disposable state. No step below authorizes deletion of an installed database or user files.

## Prepare a coherent recovery point

1. Identify the exact checkout/release, configuration, SQLite connection and configured workspace, Resource, attachment, artifact, Browser profile and Data Protection key roots. Record the tenant and host ownership of each root. Do not operate on similarly named roots from another checkout.
2. Stop incoming traffic and stop every API/worker host that can write this state. Follow [canonical shutdown](17-observability-and-operations.md#canonical-host-shutdown-and-worker-cutover). Reconcile or terminate actual Browser/Sandbox resources through their existing owners. A cancelled command or stopped API is insufficient proof that a remote mount has disappeared. Retain any uncertain generation and its original files for investigation.
3. Back up SQLite with its backup API or after all writers have stopped. A main-file-only copy while WAL is active is not a valid recovery point. Copy the associated configured data roots and protection keys with ownership/modes intact. Protect these copies as sensitive data; never print Credential/Secret payloads into a log or export them to audit.
4. Restore the backup into isolated paths with the matching old binary/configuration and verify readable durable state before proceeding. Never start this verification against the active roots or a shared Sandbox server. Record the release and backup identity.

## Select the explicit clean-data path

The release does not migrate Instance-owned homes into Spaces, infer Private ownership for old rows, dual-write homes or expose a legacy route. The Space workspace migration refuses nonempty tenant execution state. The canonical filesystem rejects a nonempty unmarked root or an older `.canonical-layout`; it preserves those bytes and returns `data_reset_required`.

After explicit owner approval of data loss, configure a **new empty database and new empty data roots** for the new release. Retain the coherent old backup and original roots. Prefer separate paths so rollback does not require repairing partially overwritten data. Do not edit a format marker, clear migration history, drop a guard, relabel an Instance directory as a Space or copy old approvals into the new database to bypass cutover checks. If retained historical state is required, stop: an independently designed and verified migration is required before this procedure can continue.

Start one host on the new paths and let the tracked migrations execute normally. Use the normal identity/bootstrap and tenant Agent creation flows. Each new Agent atomically provisions its own Private Space and protected membership. A fresh workspace root receives `.canonical-layout` containing `canonical-filesystem-2` and a newline. Shared homes are tenant/Space owned; `/working` remains Session owned. Data Protection keys use the existing Credential foundation with a distinct Secret purpose; an Environment reference never copies a protected value.

## Qualify before serving traffic

Use Synthetic mode with no hosted provider credentials. Verify migration/model consistency and `foreign_key_check` on disposable SQLite; create an Agent, inspect its Private Space, create a Shared Space/membership, and complete an actual turn there. Check two independent Sessions, `/home` versus `/private` versus `/working`, current IAM denial, next-turn switching and a restart. Verify Environment Off by default, no implicit selected profile, typed variable metadata redaction, explicit invalid-pin failure and absence of the retired Agent workspace route. Run the affected acceptance cases in the feature ledger, including actual frontend interaction.

Qualify the configured Sandbox deployment separately. Contract 5 must attest exact active Space, optional distinct Private root, Session working root, allowed mount modes and generation. The disposable direct-Docker proof checks filesystem semantics but does not establish SDK/gateway attestation. Do not change or restart another checkout's Sandbox server. New commands must retire a mismatched generation; uncertain termination retains files and blocks unsafe reuse or deletion.

Deletion retains Space tombstones for historical provenance and rejects newly authorized work in a deleted Space. Private retirement belongs to Agent decommission. Shared cleanup uses the existing bounded cleanup worker, repeats actual mount eligibility and removes only the exact deleted Shared home. Inspect retained lease/receipt facts and cleanup reason codes when progress is blocked; do not manually erase a mounted home to make the sweep succeed.

## Roll back coherently

Stop the new hosts and settle their actual external resources first. Restore the matching old binary, configuration, complete database, data roots and keys as one recovery point. Keep the new state separate for diagnosis. Database downgrade refuses destructive removal of retained Space context, pins or successor facts; do not force it by deleting rows or guards. A binary rollback against a new-format root is not a supported recovery path. Record any external effects that cannot be undone and use existing factual receipts rather than replaying commands.
