# P3 — Tools and external integrations (capability closure)

This report records the P3 capability closure after the `27efe17` freeze was reopened for email/approval corrections and then for assistant workspace, HTTP, and diagnostic follow-up. P3A remains independently frozen on implementation HEAD `c0f8a85` ([p3a-freeze.md](p3a-freeze.md)).

## Freeze status

**P3 key-free capability closure is implemented** (not “verified on every optional Real provider”). `general-assistant` v7 can read, write, patch, list, search, and move its workspace with working-directory paths; inspect attachments in Synthetic and fake-provider paths; search the public web when Brave is configured; fetch pages; make bounded approved `http.request` calls (with canonical `Content-Type` validation and shared bounded text charset decoding); create and export artifacts; use the offline sandbox; and use the existing email approval boundary. `demo.sensitive_action` is on `approval-demo`, not Riley. Sandbox networking is deferred and is not a P3 blocker. Stop adding P3 tools; next architectural value is **P4** (context compaction and memory).

| SHA role | Commit | Notes |
| --- | --- | --- |
| P3F implementation bulk | `da93489` | Workspace search/move, `http.request`, v7 allowlist, cwd normalization |
| Key-free gate + probe hardening | `3243d58` | Hosted Synthetic green (workflow `35642864725`); strengthened live SessionRuntime assertions; Playwright `retries: 0` |
| **P3 key-free implementation freeze** | **`4dbb920`** | `Content-Type` canonicalization, charset parity for `http.request`, Real probe results recorded |
| Docs-aligned HEAD | **`d9180df`** | P3 key-free bookkeeping complete (canonical framing from `b672c7a`) |
| Intermediate docs alignment | `ac795b6` | Shared `OutsideWorkspaceMessage`; freeze SHA references |

**Closure bookkeeping (2026-09-22):**

| Item | Value |
| --- | --- |
| P3 key-free implementation freeze | **`4dbb920`** |
| Docs-aligned HEAD | **`b672c7a`** |
| Local key-free gate | **Green** (see [Key-free gate](#key-free-gate-p3f-capability-closure)) |
| Previous hosted gate (pre–MIME tail) | **`3243d58`** / workflow **`35642864725`** — green |
| Final-tree hosted gate | **`7497563`** → workflow **`35682607660`** **green**; **`ac795b6`** → workflow **`35682808408`** **in progress** (2026-09-22); **`b672c7a`** docs-only (workflow **`35683144956`** in progress) |
| Real GPT-4o mini / OpenRouter | **Known red gap** (tracked separately from P4; see below) |

**Real GPT-4o mini historical-image reread (OpenRouter, opt-in):** Executed locally on 2026-09-22 with `AGENTCORE_LIVE_PROVIDER_TESTS=1` and configured `OPENROUTER_API_KEY`. Failure character differs from the original UI follow-up **400**:

| Probe | Outcome |
| --- | --- |
| `Real_session_historical_image_reread_completes_with_visible_output` | Turn **one** timed out at 90s — the run never reached historical reread |
| `Gpt4oMini_historical_image_follow_up_is_opt_in_only` | No terminal `ModelCompleted` with visible text |

Do **not** conclude the historical `attachments.read` wire projection is broken from these results alone; they point to broader Real OpenRouter generation/streaming or structured-response interoperability (initial-turn timeout / missing visible completion). **Synthetic and fake-provider historical reread remain verified.** Track remediation under **known Real-provider gaps**, not P4 memory/compaction. Proceed to P4 unless historical-image reread is essential for an immediate Real demo.

Historical key-free gates on `e255916` (workflow `35630920349`) and `e564565` (workflow `35631259704`) stay historical. Provider logs keep status, model, phase, code, and type by default; free-form provider messages require `LogProviderErrorMessages` (off by default).

| Gate | Status |
| --- | --- |
| Email/approval implementation review (`ec4dedc`–`2561167`) | **Accepted** |
| Hosted offline Synthetic + Compose on `3243d58` (workflow `35642864725`) | **Green** — Domain 77; Infrastructure 255 / 13 skip; Application 556 / 1 skip; API 165; Vitest 384; Playwright 46 passed (3.8m, no retries) |
| Hosted offline Synthetic + Compose on `7497563` (workflow `35682607660`) | **Green** (final-tree doc-freeze commit; MIME/charset tail on `4dbb920`) |
| Hosted offline Synthetic + Compose on `ac795b6` (workflow `35682808408`) | **In progress** (2026-09-22) |
| P3 Real historical-image SessionRuntime probe | **Failed** (2026-09-22 local opt-in; turn-1 timeout — see above) |
| P3 Real historical-image adapter probe | **Failed** (2026-09-22 local opt-in; no visible completion) |
| **P3 key-free implementation freeze** | **`4dbb920`** (architecture/tool surface closed for Synthetic CI) |
| P4 | **Next** (context compaction and memory; unrelated to Real OpenRouter probe gap) |

Historical image reread still requires a model with **Tools and Vision**. DeepSeek V4.1 Flash remains tools-capable and vision-incapable (`vision_required` is expected). GPT-4o mini has both capabilities; Real follow-up remains red on the strengthened probes above.

## Why `27efe17` is not the freeze SHA

| Severity | Finding | Correction |
| --- | --- | --- |
| High | Indeterminate Gmail send could retry the same approval | `EmailSendLedger` is `InFlight → Sent \| DefinitelyFailed \| Indeterminate`; `Sent` and `Indeterminate` block replay; cancellation after dispatch is indeterminate |
| High | Gmail draft reread dropped Bcc | `GetDraftAsync` uses `format=raw` + MimeKit parse including Bcc; missing raw fails closed; Gmail JSON is parsed unredacted; approval hash and preview bind Bcc |
| High | Manual MIME headers allowed CR/LF injection | MimeKit builder/parser; CR/LF/NUL rejected in header-bearing input |
| High | Gmail send used wrong REST path and draft id only (TOCTOU) | `POST /users/me/drafts/send` with hash-validated `message.raw` in the request body; HTTP contract test asserts path and payload |
| Medium | Approved snapshot could disagree with top-level draft id | `SendDraftAsync` rejects `DraftId` ≠ `ApprovedDraft.DraftId` before token HTTP; contract test asserts zero token/send on mismatch |
| Medium | `web.fetch` gave up after the first permitted address and mapped transport failures to `forbidden_host` | Try every permitted address; connection failure is `transport_error`; textual bodies honor a bounded declared charset |
| Medium | Advertised 10-minute approval wait was bounded by 30 s/120 s tool timers | Pause overall clock during human wait; start a fresh per-tool timer after approve |
| Medium | `email.send` read Gmail before execution policy | Deny/forbid stops with zero integration access; RequireApproval then fetches the draft |
| Medium | Multi-tool historical-image wire order | `MapMessages` emits all `role=tool` messages for a round, then image continuations |
| High | Real GPT-4o mini historical reread failed after `attachments.read`, and the provider body was discarded | Public 400/422 text is `Provider rejected follow-up request (status)`. Logs keep bounded code/type; fake runtime path green; Real probe still red on 2026-09-22 |
| Medium | Workspace tools treated bare filenames as absolute paths and surfaced generic forbidden errors | `WorkspaceLogicalPath` resolves relative paths from `/workspace/working`; v7 reinforces working-directory behavior |
| Medium | `http.request` `Content-Type` passed character checks but failed at transport parse | `MediaTypeHeaderValue.TryParse` at normalization; approval binds canonical value |
| Low | `waitingExternal` completed twice | `WaitForToolApprovalAsync` is the sole completion owner |
| Low | TODO still described P3 as the open step and left Approval policy unchecked | Reconciled |

## Observed scope (P3B–P3D plus correction)

| Slice | Behavior | Evidence |
| --- | --- | --- |
| P3B-1 | `ToolRegistry`, `ToolEffect`, `ToolPolicy` Allow/Deny, execution-time recheck | TDP batches through `fb8c684` |
| P3B-2 | `workspace.list`, `workspace.patch`, `artifacts.create_from_workspace`, richer `artifacts.verify` | TDP batch `0fdeeeb` |
| P3C-1 | `IWebSearchProvider` / `IPublicWebFetcher`, SSRF-safe fetch, Synthetic/Brave search | TDP batch `ee5fbdf` |
| P3C-2 | `web.search` / `web.fetch`, configuration gate, `general-assistant` v2 | TDP batch `e538626` |
| P3D-1 | `RequireApproval`, `agent.approval.requested` / `RespondApproval`, UI modal, `demo.sensitive_action`; human wait isolated from 30 s/120 s clocks | TDP batch `eed3169` plus correction |
| P3D-2 | `email.*` tools, exact-draft hash including Bcc, MimeKit Gmail MIME, Synthetic/Gmail providers, `general-assistant` v4 then v5 full demo allowlist | TDP batch `1aeec60` plus correction |
| P3F | `workspace.search`, `workspace.move`, segment-normalized cwd paths, approval-gated `http.request`, `general-assistant` v7, allowlist maximum 32, provider message logging off by default, `Content-Type` canonicalization, shared text charset decode for `http.request` | `da93489` + final tail |

Deferred outside P3: sandbox networking modes, calendar, GitHub mutations, P4–P6, plugin marketplace, browser OAuth. Authenticated `http.request` credentials stay a later `credentialAlias` design and are not accepted from the model.

## Key-free gate (P3F capability closure)

Hosted evidence on **`3243d58`** — GitHub Actions workflow **`35642864725`** (2026-09-22):

| Stage | Result |
| --- | --- |
| Domain tests | 77 passed |
| Infrastructure tests | 255 passed / 13 skipped |
| Application tests | 556 passed / 1 skipped (opt-in Real SessionRuntime historical-image probe) |
| API tests | 165 passed |
| Web Vitest | 384 passed |
| Web production build | OK |
| `CI=1 pnpm exec playwright test` | 46 passed (3.8m, zero retries) |
| `./scripts/compose-sqlite-volume.sh` | passed |

Final tail (merge commit **`4dbb920`**) reruns the same gate locally before updating the freeze HEAD row above.

Optional Real probes stay skipped in CI: no `AGENTCORE_LIVE_PROVIDER_TESTS=1` on hosted runners. Configure Brave with `dotnet user-secrets set BRAVE_SEARCH_API_KEY <value> --project src/AgentCore.Api` when a Real demo needs `web.search`.

## Optional Real probes (non-substituting)

| Probe | Status |
| --- | --- |
| OpenRouter historical vision / SessionRuntime (`Real_session_historical_image_reread_completes_with_visible_output`) | **Failed** locally 2026-09-22 (turn-1 timeout at 90s) |
| OpenRouter historical vision / adapter (`Gpt4oMini_historical_image_follow_up_is_opt_in_only`) | **Failed** locally 2026-09-22 (no visible completion) |
| Brave `web.search` (Real profile) | **SKIPPED** in CI — key not required for acceptance |
| Gmail live send/search | **SKIPPED** — trusted-local OAuth env not exercised in CI |

## Review gates

- P3A focused_output `review-focused-output-01` closed on P3A HEAD `c0f8a85`.
- Mandatory whole-output review of the original P3 tree found eight integrated families and landed on `27efe17`.
- Post-closure review of `28666c1`–`27efe17` found the email/approval defects above. Correction tail review on `ec4dedc`–`2561167` closed the remaining Gmail send/TOCTOU findings; do not treat `27efe17` or `5e468e3` as the P3 freeze SHA.

## Traceability

- Proposal: `local/tdp-workspace/inputs/proposals/P3-tools-and-external-integrations-proposal-final.md` §§12–14, 22–25.
- Canonical docs: README, docs 03/04/10/12/13/15/16/17/18, [Implementation Plan](../18-implementation-plan.md), `TODO.md`, this report.
- TDP run `run-20260921T043504-af9f80` remains historical orchestration evidence through output revision 29; this report and the correction-tree checks are authoritative for the reopen.
