# Execution-root lineage verification — 2026-10-17

Baseline: `09cba0cfeee61c069733bb23d97b3acd674f786e`. This implements the explicit execution-root prerequisite from proposal Section 9 in the existing Activation/AgentRun admission and Runtime paths. The full tenant IAM/approval goal remains active. This is causal lineage, not tenant execution authorization or approval authority.

## Delivered behavior

Independent accepted user batches, manual invocations, scheduled occurrences and external/Core events identify their own Activation as root. Immediate background children, configuration continuations and completion reports inherit the actual source Run's root and record its Activation as parent. Retries retain the original admission. A new message in the same Session starts a new root, including a later message in a background Session.

Both stores validate the source Run's actual owner, Session, Activation and root before committing a continuation. Forged root or parent references leave no Session/Activation/Run graph. Same-input replay with fresh candidate IDs returns the originally committed graph/root; inherited root and parent identity participate in the admission fingerprint. Completion reporting batches only children from the same root, and persistence independently rejects a mixed-root batch without taking its receipts or changing the Session revision.

`ActivationRootLineage` persists indexed root identity in Activations and Runs plus parent Activation identity. Restrictive root foreign keys, an Activation/root alternate key, parent/root and Run/Activation/root composite foreign keys and an independent-root check preserve the graph. Mappers reject serialized/relational root disagreement. Missing serialized roots are rejected without a compatibility reader or inferred root.

Historical execution lacks trustworthy root identity. Direct migration refuses nonempty old Activations/Runs without deleting or converting them; ordinary startup already refuses incomplete installed schemas. The new migration prepares the referenced SQLite composite key before EF's self-referencing table rebuild. Existing installations require a separately reviewed backup/export/reset plan; no user data, host, deployment or provider was touched. All execution used disposable fixtures.

## Executed verification

| Check | Result and observed behavior |
| --- | --- |
| Domain filter `ActivationAdmissionTests\|AgentRunContractTests` | 40 passed. Immutable independent roots, new-root later turns, retry identity, source fingerprints, invalid foreign/missing roots and missing serialized roots. |
| Infrastructure filter `AgentRunAdmissionStoreTests\|CanonicalStartupMigrationTests\|CanonicalSchemaRefusalTests\|TenantSessionLifecycleTests\|TenantDefinitionIntegrationTests.Referenced` | 166 passed. Both-store actual admissions, dedupe, claims/recovery, immediate children, configuration continuation, completion report/reopen, forged parent/root rejection, mixed-root transactional rollback, SQLite constraints/payload corruption, migration refusal retaining historical rows, complete registered schema history/refusal and existing tenant logical deletion. The final `Referenced` selector matches no current test; the actual retained-Activation Definition case is verified separately below. |
| Application filter `AgentRunDurabilityTests\|SessionRuntimeCompletionTests\|BackgroundCompletion` | 57 passed. Actual Synthetic SessionRuntime accepts user work, starts and runs a child, reports its completion through the parent mailbox and processes a later independent child-chat message. Explicit assertions confirm inherited root/parent links and a new later root. Existing completion, retry and deadline behavior passes. |
| `Archived_instances_and_retained_session_activations_block_definition_deletion` | 2 passed. Both stores retain the existing Definition deletion reference guard with correctly rooted historical Activation fixtures. |
| `dotnet ef migrations has-pending-model-changes --no-build` with Infrastructure project/startup | No model changes since the last migration. EF tools 10.0.5 report their version warning against runtime 10.0.12; generation and model comparison succeed. |
| Final mixed-root receipt check | 2 passed after adding an explicit assertion that both pending receipts survive rejection. |
| Changed documentation validation | 10 Markdown files, 748 local links, 198 fragment anchors, balanced fences and 7 complete JSON examples checked without errors. `git diff --check` passed. |

The first integrated two-store child/replay check passed. Composite SQLite self-reference migration tests then exposed a rebuild-order mismatch; preparing the old table's referenced composite key corrected it. Existing missing/foreign/cancelled-source error semantics remain enforced before the new lineage check. Schema fixtures now assert the full registered migration history rather than the obsolete single-baseline assumption.

## Remaining goal boundaries

Tenant-owned Sessions still reject legacy Run admission. Trusted tenant Run executor/requester context, tenant-aware Hub/message/audio admission, live persistence/recovery/lifecycle coordination, per-effect IAM, credential use, other resource/effect stores, Sandbox/decommission, inspection tools, complete tenant UI and first-class durable approvals/decisions/outbox/exact grants remain required. Existing global Profile-based Run ownership has not been relabeled as tenant authority. Final obsolete-authority/schema removal and full security/browser acceptance remain open. No CI or milestone acceptance was requested or claimed.
