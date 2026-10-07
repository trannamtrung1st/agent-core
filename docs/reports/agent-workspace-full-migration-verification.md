# Unified Agent Instance workspace migration verification

Date: 2026-10-07. Baseline: `7316757d7327d4bb5c689add220e7451aaba4a8e`.

The authorized breaking migration removes identity aliases and workspace execution modes. Every new Session requires an active real Agent Instance, whose exact active Definition/persona is pinned. Every instance owns durable `/home`; every Session owns isolated `/working`, with transient mailbox cwd starting/resetting at `/home`. Empty Chat directs the owner to Admin. Definition-only Session creation and v1 POST creation are removed; unrelated v1 history/read/end routes remain.

The active catalog contains examiner v1, approval-demo v1, General Assistant v16, Customer Support v3, Compliance v2 and Secretary v3. Unsupported historical built-ins remain in Git history. Explicit test-only policy fixtures replace historical production dependencies. Shipped procedures choose scratch or durable paths deliberately.

Canonical registry descriptors govern normal offers and capability discovery. `workspace.copy` transfers exact files/trees across writable roots. Move/rename handles complete trees within one root. Retain/checkout contracts, their aliases, the workspace mode discriminator, descriptor adaptation and narrow move branch are removed. `WriteFileAsync` is the durable CAS primitive. Opaque immutable blobs, metadata, stable IDs, revision/hash and whole-tree guards, quotas, portability checks and trusted source-Session provenance remain intact.

`harness.inspect` separates active version/authorized capabilities, future authoring eligibility and current Session pin. Its instruction excerpt is bounded at 1024 UTF-8 bytes with an explicit truncation flag so authority/version fields survive the normal durable tool-result budget. Full owner inspection remains in Admin. The regression publishes N+1, verifies current Session N remains unchanged, and verifies a fresh Session uses N+1.

## Data and deletion boundaries

Migration `20261007014134_UnifiedAgentWorkspace` removes the Compatibility column and makes persisted Session ownership required. It rejects old compatibility owners and null/empty owners with **Legacy data reset required**, preserving the old data and migration history on failure. It never converts, backfills, reassigns or silently resets. [Operations](../17-observability-and-operations.md#unified-workspace-reset-and-isolated-verification) gives the explicit native backup/reset and destructive Compose volume reset commands.

The baseline deletion correction is preserved: logical deletion, home metadata removal and the durable `InstanceDeleted` receipt commit before physical purge. Failed logical deletion preserves exact bytes; post-commit purge failures remain receipt-backed and recover at startup/every five minutes. Recovery never purges an existing owner. Retained execution references still block hard deletion. This migration does not introduce cascading deletion or change that lifecycle policy.

## Executed runtime journeys

Playwright MCP used only the task's disposable Synthetic API on 5086 and Vite on 5176. It verified no-instance Admin guidance and disabled Send, explicitly created General Assistant v16 through Admin, returned to Chat, and sent `synthetic-agent-workspace-v2:project`. The ordinary model/tool loop reported cwd `/home`, created four exact text files and an empty directory under `/working/c#/CsvTool`, copied the tree to `/home/c#`, and renamed it to `/home/csharp` using the current whole-tree token. The move reported four files affected and home listing reported `writable:true`. The completed Chat response had no tool error; browser console had no errors and instance/session requests succeeded.

The repeatable `unified-workspace-project.spec.ts` passed on fresh disposable SQLite and isolated ports 5096/5186. It additionally compared all four UTF-8/CRLF files byte-for-byte, checked empty-directory Session provenance and absence of the old name, opened a fresh Session with isolated scratch/cwd `/home`, patched a relative home file with CAS, and downloaded the exact new Artifact. Deleting the source Session preserved home; stale CAS returned 409; archive made nodes non-writable and rejected writes. Retained conversation executions correctly blocked owner deletion with 409. A separate unreferenced owner exercised home write, Session deletion, archive, successful owner deletion and inaccessible workspace. Infrastructure/API failure-injection tests establish physical deletion/recovery semantics.

Four existing workspace/structural Playwright journeys also passed. The isolated Compose project `agent-core-unified-check` on 5087 passed `compose-sqlite-volume.sh`, including exact binary home bytes, nested scratch survival, fresh scratch isolation, source Session deletion survival, container recreation, publication resources and Background Work. The script removed its containers/network and retained its disposable volume. User Real services and data were not reset or stopped.

## Gates

| Gate | Current result |
| --- | --- |
| `dotnet test AgentCore.sln --no-restore` | PASS: Domain 145; Application 1304; Infrastructure 784; API 384; OrderEvents plugin 4. Twelve explicit opt-in tests skipped; no provider calls |
| Capability discovery focused suite | PASS: 25, including canonical cross-root copy and rename projection |
| Frontend production build | PASS |
| Full frontend units, serialized | Running; an earlier competing two-worker run had timeouts. Sequential affected files passed 72/73; the remaining modal lookup was scoped and passed its focused rerun. No assertion or timeout was relaxed |
| Workspace model/tool acceptance | PASS: new four-file project journey and four existing workspace journeys |
| Complete Synthetic/fake-device browser suite | Running; stale identity-label and scratch-path fixture expectations found and corrected |
| Compose SQLite recreation/persistence | PASS |
| Hosted final-candidate Synthetic CI | Pending candidate push; required before acceptance |
| Active-source removal and documentation checks | PASS: removed symbols absent from runtime/frontend/catalog; historical migrations/reports intentionally retained. Normative ownership/path/reset/inspection chapters synchronized; local link targets checked |

Full migration acceptance remains pending the running frontend/browser and hosted gates. P10/P11, persistent cwd, home sandbox mounts, cross-store batches, distributed writers and atomic cross-root move remain outside scope. Earlier milestone freeze SHAs are unchanged.
