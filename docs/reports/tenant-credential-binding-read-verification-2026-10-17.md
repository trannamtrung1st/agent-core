# Tenant Credential binding read freshness — 2026-10-17

Baseline: `fc326f79`. This focused review hardens the existing binding inventory prerequisite for the remaining Instance configuration work. The full multi-tenant proposal remains active. The working tree was clean at the start; the earlier unfinished credential-check draft files were already absent and needed no further restoration.

## Finding and change

`TenantCredentialBindingService` previously authorized the parent, loaded a binding snapshot and filtered Credential metadata against the original security state, then returned it without a final parent/security check. Both-store regression cases reproduced a captured Credential name/alias being returned after its read grant was revoked or its binding was removed during snapshot I/O.

Inventory now rechecks parent authority and requires the original tenant security revision after snapshot I/O and visibility filtering. Lost parent/Account authority denies the read; changed security returns `stale_revision` without a page. It never relabels captured metadata with a newer revision or silently retries. Filtering still precedes visible totals/paging, and archived read behavior remains intact.

The canonical GET additionally passes the server-derived original Account, authentication-session and principal through `ListForHumanAsync`. The existing human-session authority service validates it before and after I/O. Logout/password rotation can invalidate a read independently of tenant revision; these in-flight requests return 403 `initiator_unavailable` without items, while later unauthenticated requests return 401. Current authorized login/reload recovers normally. Trusted principal-only service reads retain their own current IAM checks.

No UI, secret resolution, provider connection check, mutation transaction, role, schema or authorization engine changed. Bind/unbind retain the existing independent parent/Credential permission and revision gates. This read fix does not establish session fencing for every other management operation or complete the Instance editor.

## Integrated scenarios and observed results

Tests use real Application services and disposable InMemory/SQLite stores. Test-local store decorators capture a real snapshot and perform one deterministic change before returning it; there are no sleeps or production test hooks.

- Exact Credential read-grant revocation and concurrent unlink: the captured page is rejected with `stale_revision`; a new read contains no former item.
- Scoped parent-grant revocation: the captured page is denied. The member has an Approver membership plus an exact agent-read grant, so no inherited parent access masks the revocation.
- Account suspension: the read is denied without metadata, preserving the pre-existing current-Account check. Owner inspection confirms the binding remains saved.
- Logout and password rotation: the original human read is denied with `initiator_unavailable` even though tenant revision is unchanged. The retired session is invalid; a new login can read the saved binding safely.
- Authenticated HTTP journeys create an owned protected Credential and agent, bind/read them, then call the actual CSRF-protected logout, password-change or unlink route during the inventory read. The captured request returns the expected safe 403/409 with `Cache-Control: no-store`, no items, name, alias or protected value. Retired cookies receive 401 on the next request; new login or reload returns the current count and revision.
- Existing binding regressions retain lifecycle/archive/reopen, scoped filtering, independent parent/Credential authority, no automatic use grant, write suspension/CAS/cancellation, agent Connections policy/freeze and actual SQLite foreign-key rejection.

## Focused checks

All commands completed successfully with zero failed/skipped tests. These are local Synthetic checks; no hosted CI or full milestone suite ran.

| Project | Filter terms | Result |
| --- | --- | --- |
| Infrastructure | `Binding_inventory_never_returns`, `Human_binding_inventory` | 12 passed |
| Infrastructure | `Owned_bindings_share`, `Binding_metadata_and_mutations`, `Prepared_binding_writes`, `Agent_binding_changes`, `Sqlite_binding_composite` | 9 passed |
| API | `Binding_HTTP` | 5 passed, including 3 new in-flight read cases |

Total: **26 distinct passing cases**. Commands use `dotnet test tests/<project> --no-restore --filter '<FullyQualifiedName~term|...>' --logger 'console;verbosity=minimal' --nologo`; the unchanged existing Infrastructure regression selection additionally uses `--no-build` after the current source was compiled by the new-case selection. Logs: `/private/tmp/tenant-binding-read-after.log`, `/private/tmp/tenant-binding-read-regressions.log`, `/private/tmp/tenant-binding-read-http.log`. The baseline failure log is `/private/tmp/tenant-binding-read-before.log`.

Canonical ports, backend behavior, HTTP errors and test strategy were synchronized. Local Markdown links/fragments/fences and `git diff --check` passed. No frontend changed, so no new browser/visual acceptance is claimed. No provider, installed-data reset, deployment or unknown-process shutdown occurred.

## Remaining scope

Instance configuration/binding UI, other resource Access panels, voice/rich resources and receipts, live input arbitration, remaining resource/effect families, autonomous ingress, final obsolete authority/schema cutover and the earlier SQLite direct-preference reliability follow-up remain open. This checkpoint is progress, not full proposal or milestone acceptance.
