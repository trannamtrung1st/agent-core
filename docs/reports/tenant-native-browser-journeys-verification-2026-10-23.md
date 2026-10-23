# Canonical tenant native Browser journeys — 2026-10-23

Status: this native Browser integration slice is locally qualified. The original IAM proposal remains open.

## Delivered behavior

Direct authenticated tenant Chat admission now uses the existing interactive Browser budget and records the current user's cleanup intent. The prior canonical admission selected the 32-step unattended profile and omitted cleanup intent. Browser-capable Chat now pins the existing 48-step/300-second interactive default; Definition/Instance overrides still resolve normally. Existing Runs retain their pins. This does not grant Browser access. Independent tasks and Automation retain their separate admission profiles.

The cleanup phase projects the already-admitted exact Browser authorization branch alongside permitted observations. It excludes other protected operations, new tabs and download publication. Runtime admission, canonical preparation and fresh Browser dispatch targets recheck the typed cleanup command. Existing IAM, exact review, target witnesses, one-off grants, budgets and effect fences remain required.

Native observations nested inside completed exact Browser receipts now share normal snapshot compaction. Older page data is bounded while consent identifiers, approval status, command completion, effect facts and current source validation remain intact. The same-page excerpt choice is stable under repeated compaction. Failure/uncertainty receipts are retained unchanged. This corrects premature checkpoint exhaustion during long protected Browser work without increasing checkpoint or execution limits.

Budget diagnostics recognize a matching approved/completed canonical close result only when its native status confirms closure. Denied, uncertain or missing observations cannot establish closure. Logout remains independently unverified in generic diagnostics: a model's sign-in-screen report and a confirmed close do not create trusted application sign-out proof. A requested logout plus confirmed close therefore remains Partial.

## Focused evidence

**131 distinct focused cases passed.**

- Application: 68 cleanup-intent, typed admission, schema restriction, malformed-call refusal, budget/finalization, diagnostics and snapshot-compaction cases pass.
- Infrastructure: 29 both-store Browser continuation cases pass, including checkpoint/receipt atomicity, current source authority, original-call correlation, bounded observation fallback, sealed-result admission and recovery without replay.
- API: 34 cases pass: eight complete native Chromium journeys across both stores and 26 incumbent exact Review/reject/cancel, protected Credential fill/revocation/exceptional grant/metadata Review and scoped Browser read/compaction cases. No cases were skipped. The group took 7 minutes 59 seconds.
- Serial solution build: passed, zero warnings and errors.

The new native matrix uses actual General Assistant v32, authenticated tenant HTTP ingress, claimed workers, Runtime tool projection, exact HTTP review/decisions and canonical continuation/effect owners. Both stores complete four journeys: two-origin SSO without Skills, discovery through 131 bound Credential aliases over at least nine pages, long protected-login inspection into reserved logout/close cleanup, and close with hover capability unavailable. The long inspection visits Attribute, Table and Media and uses bounded individual-row observations. It does not repeatedly request an already-approved identical mutation within the same root. The SSO fixture issues its protected record only after a real server-side password login; it is a test website, not enterprise Account federation.

Observed outcomes match the assertions: one server-accepted SSO login and protected record delivery, no Skill load/invocation, no protected password in model/checkpoint output, 48-step admission pins without exceeding them, at least 37 tool steps with cleanup reached within the pinned budget, sign-in screen observed after the actual fixture logout when hover is available, independent confirmed close and zero live native contexts. Without hover the Run still closes the Browser and reports sign-out uncertainty. Generic diagnostics retain `LogoutVerified=false`; logout-plus-close stays Partial even when the fixture UI independently shows Sign in. Close-only obligations become Completed from the native receipt.

The original unused Application SSO server fixture moves to the API test owner that now consumes it. Setup corrections separated the custom SSO origins from the built-in Browser demo, declared model Tools explicitly, and used the canonical typed Browser risk classification and wait schema. Initial failed qualification runs are not passing evidence.

Checks use `dotnet test --no-build --no-restore --nologo`, with these filters:

```text
Application: TenantCleanupAuthorizationTests|RunBudgetDiagnosticsTests|ToolExecutionBudgetTests|BrowserCleanupIntentTests|BrowserSnapshotCompactionTests|RunFinalizationTests
Infrastructure: TenantBrowserApprovalTests.Tenant_Browser_model_|TenantBrowserApprovalTests.Tenant_Browser_sealed_result
API (AGENTCORE_CHROMIUM_ISOLATION_TESTS=1): Tenant_native_Browser_complete_|Tenant_Browser_model_requests_exact_Review|Tenant_Browser_model_protected_fill|Tenant_Browser_model_reads_use_scoped
```

All fixture roots are disposable and use loopback sites, synthetic credentials and a controlled model. No provider credentials, public network calls, user database changes, Real-host restart or CI run are involved. This is backend native Chromium evidence; no new frontend journey is claimed.

## Remaining acceptance boundary

This module does not establish proposal-wide acceptance. Generic Composer Resource-reference scope reconciliation, remaining authority/dependency inventory, safe inert Harness/schema retirement and the final proposal-wide security, isolation, functional and rendered UI pass remain open. Live model/site behavior and optional CI remain unrun. Historical milestones and old verification claims are unchanged.
