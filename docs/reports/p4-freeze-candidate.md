# P4 — Context compaction, structured memory, and durable identity

This report records the **P4 implementation freeze**. Proposal §14 closure review accepted the architecture and repair pass on `822028f`. Do not reopen P4 without a reproducible regression or an explicit product requirement that belongs in a later phase.

## Freeze status

**P4 is frozen** on implementation HEAD **`822028f`** (`822028f7cf17e5a978aced4996022e4085c4efa2`, 2026-09-23). Hosted Synthetic offline gates and Compose smoke are **green** on that commit (workflow **`35806764609`**). Optional model memory tools and embeddings were not added. **P5** (events and configurable triggers) is next.

| Item | Value |
| --- | --- |
| **P4 implementation freeze** | **`822028f`** |
| Verified hosted Synthetic + Compose | workflow **`35806764609`** — **green** on `822028f` |
| Prior closure bookkeeping | `d051c9a` (initial local candidate report) |
| Post-review repairs | `2fbc7d5` (summary boundary, pinned persona, layered item budgets); `822028f` (layered character budget through `Render`) |

## SHAs

| Role | SHA | Notes |
| --- | --- | --- |
| Implementation start | `1dee6ba509d47d04e0ca4c22fde0d86cf8989221` | Recorded before product edits. |
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
| Initial closure candidate | `d051c9a` | `P4ClosureTests` and first report revision. |
| Review repair | `2fbc7d5` | Invalid summary omitted from system memory; `EffectiveIdentity` from pinned persona; layered scope item budgets. |
| **Final implementation freeze** | **`822028f`** | Layered memory preserved through `Render`; global character cap; hosted gate green (`35806764609`). |

## Status

P4A, P4B, and P4C-0 through P4C-2 are implemented and frozen. Optional model memory tools and embeddings remain deferred. No P5, P6, P7, P8, or P10 feature was added as part of P4.

`P4ClosureTests` compacts an 80-entry SQLite session, keeps `P4A_LONG_FACT` in the committed summary and in raw history, upgrades the instance to definition version 2 without rewriting the pinned session, promotes IdentityUser and User copies, and after reopen shows the corrected identity item only to that instance, the user item to an eligible second instance, and neither item to the examiner or to another profile. Durable session delete removes the session row and leaves the promoted rows. The instance persona and pinned definition version stay unchanged. Post-freeze repairs add boundary/persona/layered-memory regressions without changing the ownership model.

**Retrieval policy note (not a blocker):** when the cross-session character budget is saturated, IdentityUser items are projected before User-wide items. That prioritizes instance-specific relationship memory over profile-wide memory.

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
| AC21 | Hosted Synthetic + Compose on `822028f` (workflow `35806764609`). |
| AC22 | No admin UI, scheduler, auth, or vector store. |

## Key-free gate

Hosted workflow **`35806764609`** on **`822028f`** passed the repository Synthetic offline job and Compose smoke (`.github/workflows/synthetic.yml`). Provider opt-in probes were not required for P4 freeze.

Earlier local gate on the pre-repair tree (594 Application tests) remains historical; freeze evidence is the hosted run on **`822028f`**.

| Command | Result (hosted `35806764609` on `822028f`) |
| --- | --- |
| Synthetic offline gates (Domain, Infrastructure, Application, API, web unit/build, Playwright) | **passed** |
| `./scripts/compose-sqlite-volume.sh` | **passed** (`compose sqlite volume check passed`) |

## Migrations

- `20260923025000_SummaryMetadata`
- `20260923050000_StructuredSessionMemory`
- `20260923070000_AgentInstance`
- `20260923090000_MemoryScope`
- `20260923110000_UserMemory`

Legacy EnsureCreated databases stamp these ids when the matching table, column, or user-owner index is already present. Rollback after these writes is a restored pre-migration SQLite backup.

## Hosted probes

Live STT, live TTS, OpenRouter, and Docker sandbox probes remain opt-in/skipped in default CI. The Real GPT-4o mini historical-image reread gap recorded in the P3 report remains a provider gap, not a P4 blocker.

## Deferred

P5 events and triggers, P6 background work, P7 admin and learned-memory reset UI, P8, P9, and P10. Optional `memory.search` / `memory.get` / `memory.write` / `memory.update` / `memory.delete` tools. Embeddings. Hosted compaction probes.
