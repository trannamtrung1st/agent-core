# P3 — Tools and external integrations (correction freeze)

This report records the **P3 correction freeze** after the `27efe17` closure was reopened. Hosted Synthetic workflow run `35612462847` completed successfully on that earlier HEAD; post-closure review then found email/approval boundary defects that undermine exact-draft send approval. This tree implements that correction pass (including Gmail `drafts.send` with approved `message.raw` on `ec4dedc`).

P3A remains independently frozen on implementation HEAD `c0f8a85` ([p3a-freeze.md](p3a-freeze.md)).

## Freeze status

**P3 correction freeze** (2026-09-22) after the email/approval tail and a final usability correction (`general-assistant` v5 full demo tools; `web.fetch` multi-address fallback, `transport_error`, declared charset).

| Gate | Status |
| --- | --- |
| Email/approval implementation review (`ec4dedc`–`2561167`) | **Accepted** |
| Hosted offline Synthetic + Compose on `06198a9` (workflow `35627313751`) | **Green** (backend, frontend, Playwright, Compose). This SHA is docs-only relative to `2561167`. |
| Hosted offline Synthetic on `2561167` (workflow `35626513959`) | **Failed** at Playwright (`Queued messages` resolved twice). Backend stayed green. Treated as the existing queue flake, not a P3 regression. |
| Hosted Application flake on `ec4dedc` (`35624825677`, `ResponseProgressRuntimeTests` empty telemetry timeline) | Unrelated to Gmail; not a reopen. |
| Local key-free gate after the usability correction | Recorded in the table below on the commit that lands this report. |
| P3 freeze SHA | The commit that lands this report (usability correction + this status). Hosted confirmation is the Synthetic workflow for that commit. `27efe17`, `5e468e3`, and `2561167` are not the freeze SHA. |

Historical image reread still requires a model with **Tools and Vision**. The Real default DeepSeek V4.1 Flash is tools-capable and vision-incapable, so `attachments.read` returns `vision_required`. Synthetic coverage remains `scripted-vision` / `historical-image-reread`. A non-vision session failing to inspect an old image is expected.

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
| Low | `waitingExternal` completed twice | `WaitForToolApprovalAsync` is the sole completion owner |
| Low | TODO still described P3 as the open step and left Approval policy unchecked | Reconciled in this pass |

## Observed scope (P3B–P3D plus correction)

| Slice | Behavior | Evidence |
| --- | --- | --- |
| P3B-1 | `ToolRegistry`, `ToolEffect`, `ToolPolicy` Allow/Deny, execution-time recheck | TDP batches through `fb8c684` |
| P3B-2 | `workspace.list`, `workspace.patch`, `artifacts.create_from_workspace`, richer `artifacts.verify` | TDP batch `0fdeeeb` |
| P3C-1 | `IWebSearchProvider` / `IPublicWebFetcher`, SSRF-safe fetch, Synthetic/Brave search | TDP batch `ee5fbdf` |
| P3C-2 | `web.search` / `web.fetch`, configuration gate, `general-assistant` v2 | TDP batch `e538626` |
| P3D-1 | `RequireApproval`, `agent.approval.requested` / `RespondApproval`, UI modal, `demo.sensitive_action`; human wait isolated from 30 s/120 s clocks | TDP batch `eed3169` plus this correction |
| P3D-2 | `email.*` tools, exact-draft hash including Bcc, MimeKit Gmail MIME, Synthetic/Gmail providers, `general-assistant` v4 then v5 full demo allowlist | TDP batch `1aeec60` plus this correction |

Deferred unchanged: generic `http.request`, sandbox networking modes, calendar, P4–P6, plugin marketplace, browser OAuth.

## Final key-free gate (correction tree)

Commands match `.github/workflows/synthetic.yml` (2026-09-22). Local rerun after `general-assistant` v5 and the `web.fetch` transport correction:

| Stage | Result |
| --- | --- |
| `tests/realtime-js` `npm ci` | OK |
| Domain tests | 76 passed |
| Infrastructure tests | 250 passed / 12 skipped |
| Application tests (`--blame-hang --blame-hang-timeout 5m`) | 535 passed |
| API tests | 165 passed |
| Web Vitest | 384 passed |
| Web production build | OK |
| `CI=1 pnpm exec playwright test` | 46 passed on ports 5090/5183 (5080 was already in use). Includes `historical-image-reread` and `scripted-vision` (3/3) |
| `./scripts/compose-sqlite-volume.sh` | **Not rerun** — 127.0.0.1:5080 already bound. Last hosted Compose green is workflow `35627313751` on `06198a9` |

Earlier hosted evidence on `27efe17` (workflow run `35612462847`) remains historical; it is not this correction HEAD.

## Optional Real probes (non-substituting)

| Probe | Status |
| --- | --- |
| OpenRouter historical vision / structured response | **SKIPPED** — no hosted keys in default verification |
| Brave `web.search` (Real profile) | **SKIPPED** — `BRAVE_SEARCH_API_KEY` not required for acceptance |
| Gmail live send/search | **SKIPPED** — trusted-local OAuth env not exercised in CI |

## Review gates

- P3A focused_output `review-focused-output-01` closed on P3A HEAD `c0f8a85`.
- Mandatory whole-output review of the original P3 tree found eight integrated families and landed on `27efe17`.
- Post-closure review of `28666c1`–`27efe17` found the email/approval defects above. Correction tail review on `ec4dedc`–`2561167` closed the remaining Gmail send/TOCTOU findings; do not treat `27efe17` or `5e468e3` as the P3 freeze SHA.

## Traceability

- Proposal: `local/tdp-workspace/inputs/proposals/P3-tools-and-external-integrations-proposal-final.md` §§12–14, 22–25.
- Canonical docs: README, docs 03/04/10/12/13/15/16/17/18, [Implementation Plan](../18-implementation-plan.md), `TODO.md`, this report.
- TDP run `run-20260921T043504-af9f80` remains historical orchestration evidence through output revision 29; this report and the correction-tree checks are authoritative for the reopen.
