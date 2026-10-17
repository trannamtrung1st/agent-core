# Tenant Session lifecycle verification — 2026-10-17

Baseline: `bfd27ad5d2f86a3c7a10c13e8a2a596608ca4f5d`. This successor adds typed lifecycle management for quiescent tenant Sessions. The full proposal remains active. Live Runtime coordination, automatic tenant recovery, trusted Run/requester/root lineage and the remaining resource/effect/approval cutover are still required. Existing Real hosts and user data were preserved; all execution used disposable fixtures.

## Delivered behavior

Canonical Session PATCH and concrete archive/restore/pause/resume/complete/cancel routes require current exact `sessions.manage/read`. Resume additionally requires current exact agent chat and active Instance/executor. DELETE requires `sessions.delete`, permits a known resource without read authority, returns 204 without metadata, and removes direct Session grants. Mutation inputs contain only the action-specific value and security/Session witnesses. No caller-supplied snapshot, Principal, lifecycle source or permission snapshot is accepted as authority.

Application and stores derive the transition from the current saved Session. Existing user completion/cancellation policy remains authoritative. Archived Sessions must be restored before resume, terminal Sessions cannot resume, and restoring catalog visibility does not restart a completed Session. Metadata exposes protocol status, lifecycle status and archive time independently, with no Definition or conversation content.

Both stores commit under current human/session liveness, security/Session CAS and safe audit. Queued/running/waiting nonterminal Runs, attached/ending Sessions and pending input reject mutation with `session_in_use`; there is no direct write around a live mailbox. This is a quiescent management boundary, not a replacement for future Runtime lifecycle coordination. Audit contains actor/target/action/reason rather than title or conversation text.

Logical delete retains the Session root and prevents ID reuse or global adoption. It clears summary/pending context and redacts entry text, structured parts, envelopes, failure/attachment references, offsets and content-related provenance. Entry keys, ordering and opaque effect/Activation identities remain as tombstones, preserving restrictive source-entry foreign keys and durable lineage. It does not physically erase retained Run/Activation evidence or external workspace/artifact/attachment bytes. No new schema, migration, generic store or compatibility route was added.

## Executed verification

| Check | Result and observed behavior |
| --- | --- |
| API build | `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo -v minimal` passed, zero warnings/errors. Subsequent tests compile the final source. |
| `TenantSessionLifecycleTests` | 16 passed. Both stores execute rename, archive/restore, pause/resume, completion and logical delete; retain ownership/source; retire direct grants; reject deleted-ID adoption. Scoped manage without read denies, resume requires independent chat, delete without read yields no metadata. Prepared writes fail after revocation, logout, a competing write or cancellation without false success audit. A stored queued Run fences mutation; its saved terminal transition releases the guard. Pure transition checks preserve stored user policy and reject attached Runtime mutation/control characters. Actual SQLite deletion redacts malformed/private payload fixtures while retaining real Activation/source-entry references across reopen. |
| Infrastructure filter `TenantSession\|Tenant_Session_resolves_owned_publication\|MemoryStoreContractTests` | 69 passed. Covers lifecycle plus prior ownership, scoped access, source-publication pinning, creation races and directly affected memory persistence contracts. |
| `TenantSessionAccessTests` | 1 integrated Synthetic HTTP journey passed. Cookie/CSRF create/read/inventory, typed rename/lifecycle, successful completion and cancellation on separate Sessions, separate metadata states, foreign mutation denial, terminal-resume denial, safe no-store responses and logical delete. Extra client `source=system` does not change the stored User source. Missing delete CSRF denies; an authorized delete returns 204 and the retired resource becomes unavailable. |
| Changed Markdown validation | 10 files checked: 702 local links, 178 fragment anchors, balanced fences and 20 complete JSON examples; no errors. `git diff --check` passed. |

Initial review of deletion found that physical entry removal would conflict with retained Activation source-entry foreign keys. The final implementation redacts tombstones instead, and the SQLite test exercises the real relationship. A test block initially referenced the later-created foreign tenant before declaration; it was moved after ownership setup before the passing HTTP run. No live provider or application host was needed: WebApplicationFactory and real Application/store workflows establish this scope.

## Remaining boundaries

These routes do not stop or mutate an attached Runtime, cancel a live Run, publish tenant lifecycle events, or perform automatic recovery. Those operations must use the trusted runtime/Run/tenant-service paths; no human or executor is impersonated for background maintenance. External resource cleanup and retention remain pending. The canonical UI does not yet expose these controls. Tenant-aware Hub messaging/audio, per-turn requester/root lineage, runtime persistence/recovery, tools/background effects, credential use, other owning stores, Sandbox/decommission, durable approvals, obsolete global authority removal and final schema/reset consolidation remain required. No CI, deployment, user-data reset or milestone acceptance was requested or performed.
