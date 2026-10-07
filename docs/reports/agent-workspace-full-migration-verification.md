# Unified Agent Instance workspace migration verification

Date: 2026-10-07. Physical cleanup baseline: `250b509eaa2a89f86f0570bc18f63c6938d974d5`. Deletion recovery baseline: `7316757d7327d4bb5c689add220e7451aaba4a8e`.

Every Session belongs to a real Agent Instance. The instance owns durable `/home`; the Session owns isolated `/working`. Mailbox cwd starts/resets at `/home`. Definition-only Session creation, compatibility owners, v1 POST creation, checkout/retain execution modes and historical production catalog versions are removed. Unrelated v1 history/read/end routes remain. The supported catalog is Examiner v1, Approval Demo v1, General Assistant v16, Customer Support v3, Compliance v2 and Secretary v3.

## Physical storage

One `Persistence:WorkspaceRoot` owns both workspace adapters. Infrastructure derives all paths through `AgentWorkspacePhysicalPaths`:

```text
data/
  agent-core.db
  workspaces/
    agent-<instanceN>/
      home/blobs/<opaqueBlobIdN>
      sessions/session-<sessionN>/
        working/
        .provisioned
  artifacts/
  attachments/
  definition-resources/
```

Home logical names stay in SQLite metadata, with stable ItemId, revision/hash/tree CAS and immutable opaque blobs. They never become physical names. Scratch resolves trusted Session metadata before direct writes, structure, transfers and sandbox path queries, even before provisioning. Missing Sessions fail NotFound; invalid owner state cannot fall back to a raw GUID directory. `.provisioned` prevents template reseeding over user edits. The private `/workspace/working` adapter prefix and sandbox container mount remain internal; there is no physical `workspace/` intermediate directory, scratch artifacts/state folder or separately configured home root. Artifact bytes belong to IArtifactStore.

Startup rejects obsolete raw-GUID workspace trees, intermediate Session workspace directories and the default obsolete split home tree with a reset-required error. It does not move or delete bytes. Configuration, image, Compose, browser fixtures and normative docs use the single root. Native reset commands are in [Operations](../17-observability-and-operations.md#unified-workspace-reset-and-isolated-verification); the developer's data is preserved during verification.

Agent deletion commits logical removal, home metadata removal and the InstanceDeleted receipt before physical purge. With all referenced Sessions already deleted, purge removes the complete agent tree. Failed commit preserves exact bytes; failed purge is retried at startup, periodically or by exact command retry. Recovery never purges an existing owner. The existing failure-injection matrix remains in place.

## Executed focused evidence

- Infrastructure workspace/sandbox suite: 59 passed, including owner layout, opaque blobs, direct operations before provisioning, missing-owner rejection, Session/agent isolation, no dead folders, non-destructive legacy rejection, bounded transfer/quota, symlink denial and deletion failure/recovery. Docker verifies a single Session working mount and denies host/other-Session access.
- SQLite API workspace journeys: 6 passed. Host reconstruction preserves exact home and scratch bytes; fresh Session scratch is isolated; Session deletion preserves home; owner hard deletion and committed-receipt cleanup remove the complete agent tree.
- Application lifecycle/planner/support focused suite: 22 passed. Archive/reopen/deactivate preserve scratch; durable delete and partial-cleanup retry remove the trusted Session tree.
- Synthetic browser workspace/project/Admin journeys: 4 passed on isolated ports and disposable SQLite. The four-file project checks exact UTF-8/CRLF bytes, empty-directory provenance, copy/rename, relative guarded edit, Artifact download, fresh Session cwd/scratch, stale CAS conflict and archived write denial.
- Isolated Compose SQLite persistence/recreation: passed. Home, current scratch, fresh scratch isolation, publication resources and Background Work survive container recreation; source Session deletion preserves home. Containers/network are removed and the disposable volume retained.
- Frontend production build: passed. Source and documentation removal audits pass; obsolete root configuration and checkout-specific write helpers are absent. Legacy names remain only in explicit rejection tests/detection and historical evidence.

## Full candidate gates

The [Synthetic workflow](https://github.com/trannamtrung1st/agent-core/actions/workflows/synthetic.yml) records the exact candidate SHA and authoritative results. Acceptance requires all five jobs to pass on that SHA, not merely the focused evidence above:

| Job | Required coverage |
| --- | --- |
| Synthetic backend | Full Domain, Infrastructure, Application and API suites; SQLite, wire and browser integration |
| Synthetic frontend | Full frontend unit suite and production build |
| Synthetic Playwright core | All Synthetic, Browser STT and Browser/Browser journeys, including unified workspace project |
| Synthetic Playwright acceptance | Faithful wall-clock reminder, Admin lifecycle/authoring, harness management, continuity/maintenance and Secretary journeys |
| Synthetic Compose | SQLite volume persistence and recreation |

Browser fixtures explicitly provision real Agent Instances. Stale identity labels and the faithful fixture's pre-provision enabled check were corrected. Schedule disable/reload now waits for the successful revisioned PUT and visible enabled-state transition; the clipboard failure test waits for visible UI after its asynchronous rejection. Assertions and timeouts are preserved. Native test factories and process hosts isolate workspace data rather than reading the developer's legacy tree.

P10/P11, persistent cwd, direct home sandbox mounts, cross-store batches, distributed writers and atomic cross-root move remain outside scope. Earlier milestone freeze SHAs are unchanged.
