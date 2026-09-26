# P7 — Agent harness and Admin lifecycle

This report records the **P7 implementation freeze candidate**. P7 is **not frozen** until hosted Synthetic offline gates and Compose smoke are **green on the exact candidate SHA** recorded below. Do not reopen P1–P6 freeze baselines as part of P7 evidence.

## Freeze status

**P7 is not frozen** (2026-09-26) until this W09 batch is accepted; hosted evidence is recorded below. Local W08 gate evidence is on behavior baseline **`df761d8`** and documentation closure **`2b967cf`** (review 0160).

| Item | Value |
| --- | --- |
| **P7 hosted verified freeze tree** | **`f0c9e19`** (`f0c9e19a3e7cc3d2413a32f9e6761979739fb69b` — Vitest CI stabilization over **`cb097f4`** report draft; behavior baseline **`df761d8`**) |
| **W08 documentation closure** | **`2b967cf`** (`2b967cf84d0086c46212cfd35b6c3dbd429a119a`, review 0160 PASS) |
| **Last behavior-affecting SHA (W08)** | **`df761d8`** (`df761d855c58ec5ebf8c9d0044910dc101895746` — Compose Admin draft resource upload/bind + `Persistence__DefinitionResourceRoot` volume) |
| **Prior phase baselines preserved** | P4 **`822028f`** / `35806764609`; P5 **`4bbc0c1`**; P6 **`30adaeb`** / `36085265506` (`bef77d1` last behavior) |
| **Reviewed P7 proposal baseline** | **`4ab5069`** / workflow **`36096331077`** green (pre-P7 product edits) |
| **Verified hosted Synthetic + Compose on P7 candidate** | workflow [**`36218518149`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36218518149) — **green** on **`f0c9e19`** (offline gates + Compose smoke) |

Documentation-only descendants after **`df761d8`** (gate evidence rows, canonical doc sync, observed-status handoff) are not new implementation baselines.

## Slice and work-item SHAs

| Work | Slice | Approved / gate HEAD | Report |
| --- | --- | --- | --- |
| W01 | P7A | `9169bfa` (review 0006) | [p7a-admin-shell-effective-config.md](p7a-admin-shell-effective-config.md) |
| W02 | P7B | `4a2bf99` (review 0020) | [p7b-definition-lifecycle.md](p7b-definition-lifecycle.md) |
| W03 | P7C | `e25cd46` (review 0044) | [p7c-harness-resources-workspace.md](p7c-harness-resources-workspace.md) |
| W04 | P7D | `0aa3ad3` (review 0069) | [p7d-managed-instance-identity.md](p7d-managed-instance-identity.md) |
| W05 | P7E | `18ffecf` (review 0087; slice `59a812a`) | [p7e-memory-automation-admin.md](p7e-memory-automation-admin.md) |
| W06 | P7F | `03e350a` (review 0101) | [p7f-validation-evals-publish-gate.md](p7f-validation-evals-publish-gate.md) |
| W07 | P7G | `ae83bfc` (review 0139) | [p7g-history-rollback-final-gate.md](p7g-history-rollback-final-gate.md) |
| W08 | Whole-phase local gate | `2b967cf` (review 0160) | [p7g-history-rollback-final-gate.md#w08-local-gate-observed-at-2b967cf](p7g-history-rollback-final-gate.md#w08-local-gate-observed-at-2b967cf) |

## What shipped (summary)

- Distinct Admin area and owner/trusted-local `/api/v2/admin/...` APIs with server-side effective config resolution and secret redaction (P7A).
- Durable drafts, optimistic revisions, immutable publications, composite runtime catalog, and metadata-only deprecation (P7B).
- Application-owned definition resources, publish-time binding immutability, read-only runtime `/agent` view; session `/workspace` stays mutable (P7C).
- Managed `Compatibility=false` instances, persona Form/JSON with revision conflicts, managed chat by instance id with pinned definition/persona; archive semantics (P7D).
- Scoped learned-memory inspection/delete/reset and trigger registration revoke through existing P4/P5 boundaries (P7E).
- Layered validation, required deterministic Synthetic evaluation, safe diff, exact-revision publish with configuration fingerprint evidence (P7F).
- Append-only `AdminEvents`, compatible rollback/reassociation, full Admin UX and whole-phase deterministic journey (P7G).
- W08: canonical docs/TODO alignment, migration/reopen parity, full local command matrix, extended Compose Admin path (fork, resources, publish, managed session survival).

Repository runtime seeds (`agents/*.json`, knowledge/templates, `.agents/*`) are not edited by Admin. Session runtime remains the mutable conversation owner; raw audio does not enter the domain mailbox.

## Schema (P7 migrations)

Observed durable Admin and managed-instance schema (SQLite; InMemory parity for Synthetic):

| Migration | Purpose |
| --- | --- |
| `20260925071140_P7DefinitionLifecycle` | Drafts and publications |
| `20260925084907_P7DefinitionResources` | Resource blobs and draft/publication bindings |
| `20260925101355_P7ManagedAgentInstance` | Managed instance lifecycle columns |
| `20260925101918_P7ManagedAgentInstanceRevisionToken` | Instance/persona revision tokens |
| `20260925161000_P7AdminEvents` | Append-only Admin history |

Prior P4–P6 migrations remain unchanged. See [Persistence and configuration](../15-persistence-and-configuration.md).

## Rules and acceptance mapping

| ID | Primary evidence |
| --- | --- |
| R-P7-01 / AC-P7-02 | W01–W02 built-in byte immutability; composite catalog tests |
| R-P7-02 | W01 shell vs repository `.agents`; product resources in app storage (W03) |
| R-P7-03 / AC-P7-03 | W02–W06 publish immutability; no publication update path |
| R-P7-04–05 / AC-P7-05–06 | W03–W04 managed instance id chat; explicit version association |
| R-P7-06–07 / AC-P7-07–08 | W04 persona vs definition; session snapshot pinning matrix |
| R-P7-08 / AC-P7-09–10 | W03–W05 policy via versioned draft; memory/trigger admin boundaries |
| R-P7-09 / AC-P7-04 | W03 resource hashes, `/agent` vs `/workspace` |
| R-P7-10 / AC-P7-01, 17 | W01–W08 owner/trusted-local and P1–P6 regression selections |
| R-P7-11 / AC-P7-14, 16 | W01, W06–W07 projections, diffs, evals, `AdminEvents` summaries |
| R-P7-12 / AC-P7-11 | W04–W05 archive vs accepted P6 WorkItems |
| R-P7-13 / AC-P7-12–13 | W06 pure vs resolved validation; Synthetic evaluation gate |
| AC-P7-15 | W02, W07 metadata deprecation and explicit rollback |

Slice-level AC-P7A–G criteria are satisfied per the slice reports linked above.

## Whole-phase journey (deterministic)

Covered by Playwright `admin-lifecycle` (1 test) and backend/API suites referenced in [P7G W08 local gate](p7g-history-rollback-final-gate.md#w08-local-gate-observed-at-2b967cf): fork built-in draft → edit instruction/capability/resource → validate → evaluate → diff → publish → managed instance → persona Form/JSON → managed chat by instance id → memory inspect/reset → trigger revoke → publish v2 → old session history preserved → upgrade/rollback/deprecate → archive denies new managed/triggered work → safe Admin history list.

Faithful Manual-A wall-clock detached reminder replay: **pass** on `df761d8` tree (`faithful-manual` project; see P7G table).

## Local gate (W08, observed)

Authoritative command table and counts: [p7g-history-rollback-final-gate.md § W08 local gate](p7g-history-rollback-final-gate.md#w08-local-gate-observed-at-2b967cf).

| Check | Result (on recorded SHAs) |
| --- | --- |
| `dotnet test AgentCore.sln --nologo -m:1` | 1596 passed, 8 skipped |
| P3–P6 focused filters + P7 migration/reopen | Pass (see P7G) |
| `pnpm run test --run` / `pnpm run build` | 468 passed; build pass |
| `AdminApiTests` | 56 passed |
| Playwright `synthetic` | 51 passed |
| Playwright `admin-lifecycle` | 1 passed |
| `./scripts/compose-sqlite-volume.sh` | Pass (Admin fork/resource/publish/instance/managed session) |
| Playwright `faithful-manual` (opt-in) | 1 passed on `df761d8` |

## Hosted CI (required for freeze)

| Step | Status |
| --- | --- |
| Push exact candidate SHA to GitHub | **Done** — `f0c9e19` on `main` (2026-09-26) |
| `.github/workflows/synthetic.yml` on that SHA | **Green** — [**`36218518149`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36218518149) on **`f0c9e19`** |
| Prior failed attempt (superseded) | [**`36217996308`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36217996308) on **`cb097f4`** — Vitest timeouts under CI load; fixed in `f0c9e19` |

A parent commit, descendant commit, or pre-push local-only run is **not** freeze evidence.

## Explicit deferrals (unchanged)

Per master proposal §14 — not implemented in P7:

- Admin conversational operator agent; persistent mutable instance filesystem; plugin marketplace; generalized MCP/provider framework; arbitrary custom tool-provider framework; visual workflow builder; multi-agent orchestration; standing/bulk future-action authorization; organization/team management; full RBAC/tenancy; enterprise audit/compliance; distributed scheduler/runtime infrastructure.

**P8** harness/platform extensibility and **P10** multi-user auth/RBAC remain later phases.

## Unverified / limitations

- Hosted exact-SHA Synthetic + Compose green recorded on **`f0c9e19`** / **`36218518149`**; lifecycle acceptance remains W09 review.
- Optional Real provider paths may be skipped in key-free CI; Synthetic remains the deterministic gate.
- Faithful-manual is opt-in and wall-clock; not part of default `synthetic` CI project.
- P7 does not claim freeze until this report’s candidate SHA matches a green hosted workflow run.
