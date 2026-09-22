# P4 — Context compaction, structured memory, and durable identity

This report is the local closure candidate for P4. It is not a hosted GitHub workflow result. The reviewer still has to answer proposal §14 before P4 is frozen.

## SHAs

| Role | SHA | Notes |
| --- | --- | --- |
| Implementation start | `1dee6ba509d47d04e0ca4c22fde0d86cf8989221` | Recorded before product edits. Compose was not run before the first edit, and the first full Playwright run overlapped W01. |
| P4A-0 | `94137955543dd6858cb189e3a5b10a122e488519` | Summary boundary. Review 0003 passed. |
| P4A-1 | `5d07e3dd8b3585b728e0c6b05a51f760a000e04a` | Semantic compaction candidate. Review 0004 passed. |
| P4A-2 | `0c5d703ea073d70d51cc8baac7fb1916523d987c` | Mailbox commit, cancellation, stale results. Review 0006 passed. |
| P4A checkpoint | `928ae08b4dc011644e2277eb0c7738c180d40507` | Local full gate. Review 0007 passed. |
| P4B store | `334410ccbdfef21471a82d22fbd955f837c74a3d` | Review 0008 passed. |
| P4B prompt | `6eb26310e707632cec15616309f186541cb2918b` | Review 0010 passed. |
| P4B checkpoint | `71131dbfe66e1183d2df8bbf3a53c34d0453f86f` | Local full gate. Review 0011 passed. |
| P4C-0 | `c5c6d3ef20d9d5096abc18bac8c8fbf196db2170` | Agent instance and backfill. Review 0013 passed. |
| P4C-1 | `1794720f04e05b3b1a420865020732c21961dcec` | IdentityUser copy. Review 0014 passed. |
| P4C-2 | `836259398bb24cb277a878f9ad09240ad46151a6` | User-scope copy. Review 0015 passed. |
| Closure candidate | the commit that adds this file | Gate below ran before this file and the docs/18 closure paragraph. |

## Status

P4A, P4B, and P4C-0 through P4C-2 are implemented. Optional model memory tools and embeddings were not added. No P5, P6, P7, P8, or P10 feature was added.

`P4ClosureTests` compacts an 80-entry SQLite session, keeps `P4A_LONG_FACT` in the committed summary and in raw history, upgrades the instance to definition version 2 without rewriting the pinned session, promotes IdentityUser and User copies, and after reopen shows the corrected identity item only to that instance, the user item to an eligible second instance, and neither item to the examiner or to another profile. Durable session delete removes the session row and leaves the promoted rows. The instance persona and pinned definition version stay unchanged.

## Acceptance mapping

| ID | Evidence |
| --- | --- |
| AC01–AC02 | Summary boundary prompt tests and the long-session restore window. |
| AC03 | Received text and heard speech prefixes, including the voice unheard-tail sentinel. |
| AC04 | Compaction leaves raw rows in place. `P4ClosureTests` still reads `P4A_LONG_FACT` from history. |
| AC05 | Malformed, oversized, and failed compaction keep the previous summary. |
| AC06 | Stale and cancelled compaction tests. |
| AC07 | Incremental compaction, reopen, and reattach. |
| AC08–AC11 | Structured session memory contract, prompt projection, and `SessionMemoryCheckpointTests`. |
| AC12–AC13 | `AgentInstanceTests`, including highest-version backfill and a pinned historical session. |
| AC14–AC17 | `IdentityUserMemoryTests`. |
| AC18 | `UserMemoryTests`: support retrieval on, examiner retrieval off. |
| AC19 | Learned block label and trusted-precedence sentence. |
| AC20 | InMemory and temporary SQLite pairs for memory and instances. |
| AC21 | This candidate's full local gate, including Playwright. |
| AC22 | No admin UI, scheduler, auth, or vector store. |

## Key-free gate

Commands ran on the working tree before this report file existed. `pnpm exec playwright install chromium --with-deps` exited 0 and the fallback install was not used.

| Command | Result |
| --- | --- |
| `npm ci` in `tests/realtime-js` | passed |
| Domain tests | 77 passed |
| Infrastructure tests | 267 passed, 13 skipped |
| Application tests `--blame-hang --blame-hang-timeout 5m` | 594 passed, 1 skipped, no hang sequence |
| API tests | 167 passed |
| `pnpm install --frozen-lockfile`, Vitest, production build | 50 files / 392 passed, build passed |
| `CI=1 pnpm exec playwright test` | 47 passed |
| `./scripts/compose-sqlite-volume.sh` | `compose sqlite volume check passed` |

## Migrations

- `20260923025000_SummaryMetadata`
- `20260923050000_StructuredSessionMemory`
- `20260923070000_AgentInstance`
- `20260923090000_MemoryScope`
- `20260923110000_UserMemory`

Legacy EnsureCreated databases stamp these ids when the matching table, column, or user-owner index is already present. Rollback after these writes is a restored pre-migration SQLite backup.

## Hosted probes

Not run. Infrastructure skips include live STT, live TTS, OpenRouter, and Docker sandbox probes. The Real GPT-4o mini historical-image reread gap recorded in the P3 report remains a provider gap, not a P4 blocker.

## Deferred

P5 events and triggers, P6 background work, P7 admin and learned-memory reset UI, P8, P9, and P10. Optional `memory.search` / `memory.get` / `memory.write` / `memory.update` / `memory.delete` tools. Embeddings. Hosted compaction probes.
