# P3 — Tools and external integrations (closed/frozen)

This report records the **P3 closure** after P3A–P3D implementation, P3E reconciliation, whole-output corrections, and final manual verification. P3 is **observed/frozen** as of the commit containing this report. Do not reopen it without a reproducible regression.

P3A remains independently frozen on implementation HEAD `c0f8a85` ([p3a-freeze.md](p3a-freeze.md)). P3B–P3D implementation and whole-output corrective batches 14–24 are included in the closure commit. The earlier complete gate on `bcc3009` remains historical evidence; the closure tree was rechecked after the final corrections described below.

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

## Final key-free gate

Commands match `.github/workflows/synthetic.yml` (2026-09-21).

| Stage | Result |
| --- | --- |
| `tests/realtime-js` `npm ci` | OK |
| Domain tests | 76 passed |
| Infrastructure tests | 235 passed / 12 skipped |
| Application tests (`--blame-hang --blame-hang-timeout 5m`) | 528 passed |
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
- The mandatory whole-output review found eight integrated families: public-web DNS/SSRF, atomic workspace patching, web runtime verification, canonical docs, approval lifecycle, email write safety, exact-head evidence, and completion-claim accuracy. Corrective batches added the missing implementation and tests.
- The TDP run then stalled while repeatedly rebinding already completed owner sweeps to new output digests. Manual closure re-inspected the current correction tree and reran the affected Application and Infrastructure boundaries before the complete final gate.
- **Mandatory whole-output review:** accepted by manual closure on 2026-09-21. P3 is observed/frozen; optional Real probes remain unverified and non-substituting.

## Traceability

- Proposal: `local/tdp-workspace/inputs/proposals/P3-tools-and-external-integrations-proposal-final.md` §§12–14, 22–25.
- Canonical docs reconciled in batches 19 and 22: README, docs 03/04/10/12/13/15/16/17/18, [Implementation Plan](../18-implementation-plan.md), `TODO.md`, this report; gate log `local/tdp-workspace/runs/run-20260921T043504-af9f80/gate-8f0d127.log`.
- TDP run `run-20260921T043504-af9f80` records slice dispositions and whole-output corrective evidence through output revision 29. Its terminal orchestration status is not closure evidence because the digest-rebind loop did not converge; this report and the final repository checks are authoritative for manual closure.
