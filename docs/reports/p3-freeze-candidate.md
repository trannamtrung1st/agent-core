# P3 — Tools and external integrations (freeze candidate)

This report records the **P3 freeze candidate** after P3A–P3D implementation and the P3E reconciliation gate on a single final HEAD. It does **not** mark P3 observed/frozen: mandatory **whole-output review** must accept the final HEAD before closure per proposal §25.

P3A remains independently frozen on implementation HEAD `c0f8a85` ([p3a-freeze.md](p3a-freeze.md)). P3B–P3D implementation, whole-output corrective batches 14–21, and the final gate below bind to **final HEAD** `8f0d127d767906ef01fe2b7ed90c52750c102c4e` (`8f0d127`).

## Observed scope (P3B–P3D)

| Slice | Behavior | Evidence |
| --- | --- | --- |
| P3B-1 | `ToolRegistry`, `ToolEffect`, `ToolPolicy` Allow/Deny, execution-time recheck | TDP batches through `fb8c684` |
| P3B-2 | `workspace.list`, `workspace.patch`, `artifacts.create_from_workspace`, richer `artifacts.verify` | TDP batch `0fdeeeb` |
| P3C-1 | `IWebSearchProvider` / `IPublicWebFetcher`, SSRF-safe fetch, Synthetic/Brave search | TDP batch `ee5fbdf` |
| P3C-2 | `web.search` / `web.fetch`, configuration gate, `general-assistant` v2 | TDP batch `e538626` |
| P3D-1 | `RequireApproval`, `agent.approval.requested` / `RespondApproval`, UI modal, `demo.sensitive_action` | TDP batch `eed3169` |
| P3D-2 | `email.*` tools, draft-hash `email.send` approval, Synthetic/Gmail providers, `general-assistant` v4 | TDP batch `1aeec60` |

Deferred unchanged: generic `http.request`, sandbox networking modes, calendar, P4–P6, plugin marketplace, browser OAuth.

## Final key-free gate (exact final HEAD)

Commands match `.github/workflows/synthetic.yml` (2026-09-21).

| Stage | Result |
| --- | --- |
| `tests/realtime-js` `npm ci` | OK |
| Domain tests | 76 passed |
| Infrastructure tests | 235 passed / 12 skipped |
| Application tests (`--blame-hang --blame-hang-timeout 5m`) | 526 passed |
| API tests | 165 passed |
| Web Vitest | 384 passed |
| Web production build | OK |
| `CI=1 pnpm exec playwright test` | 46 passed (includes extended `approval-flow`, `email-harness`, `historical-image-reread`) |
| `./scripts/compose-sqlite-volume.sh` | `compose sqlite volume check passed` |

## Optional Real probes (non-substituting)

| Probe | Status |
| --- | --- |
| OpenRouter historical vision / structured response | **SKIPPED** — no hosted keys in default verification |
| Brave `web.search` (Real profile) | **SKIPPED** — `BRAVE_SEARCH_API_KEY` not required for acceptance |
| Gmail live send/search | **SKIPPED** — trusted-local OAuth env not exercised in CI |

## Review gates

- P3A focused_output `review-focused-output-01` closed on P3A HEAD `c0f8a85`.
- Pre-freeze focused_output on integrated P3B/P3C/P3D boundaries: **recommended** before `submit-completion`; not a substitute for whole-output review.
- **Mandatory whole-output review:** pending after producer `submit-completion`; do not mark P3 frozen until it passes.

## Traceability

- Proposal: `local/tdp-workspace/inputs/proposals/P3-tools-and-external-integrations-proposal-final.md` §§12–14, 22–25.
- Canonical docs reconciled in batches 19 and 22: README, docs 03/04/10/12/13/15/16/17/18, [Implementation Plan](../18-implementation-plan.md), `TODO.md`, this report; gate log `local/tdp-workspace/runs/run-20260921T043504-af9f80/gate-8f0d127.log`.
- TDP run `run-20260921T043504-af9f80` production revisions 1–22 record slice dispositions and whole-output corrective evidence.
