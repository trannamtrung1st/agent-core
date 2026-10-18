# Tenant Sandbox read Review verification — 2026-10-18

Status: focused local implementation and verification over `9bf90fd967858f3af516711cd9e71eedb7d1ace0`. The full multi-tenant IAM proposal remains active. This closes typed policy-required Sandbox read Review only; it does not claim a milestone or full proposal acceptance.

## Result and owners

The existing authorization tool now accepts exact profile-discovery, stored-status and exact-process inspection intents. Saved/current read capability controls offering independently of Settings management, execution eligibility and SDK availability. Strict arguments exclude caller authority, mutable witnesses and source identities. An ordinary Allowed read does not create an approval; policy-required reads use durable Review without an exceptional grant. No jointly qualified reviewer means denial without a saved wait.

The canonical read service prepares bounded exact profile/revision references and content-free display labels. Executor Session/agent/profile permissions remain mandatory. Review requirements and independence combine across those actual scopes; reviewers need raw scopes, primary agent approval authority and approval authority for additional reviewed scopes. Detail and pre-count inbox filtering require all referenced read scopes. Later permission grants cannot expand a saved discovery set.

Consent binds actual Session/Instance/executor, generation, requested/current execution and selection identity. Factual output/completion can advance while a human reviews; a replacement generation, new current execution or changed selection cannot reuse consent. Decision and effect stores repeat the target, source and current authority checks. Read observations contain no command text, initialization details, provider IDs/paths or protected source data.

Read/control consent now shares the existing `TryCommitLocalEffectAsync` transaction with configuration effects. The redundant control port/store partials were removed. Both adapters retain exact request/claim/root dedupe, receipt, Run effect, security and audit ownership. Read consent changes no Sandbox state and records only `sandbox_read_authorized`; control consent changes only the existing lease/receipt intent. Existing configuration and Credential continuations remain covered by the regression filter.

After consent the existing continuation verifies the actual saved request/immutable receipt, decision and exact pending checkpoint. It invokes the existing projection once under current requester/executor/Run/Session/profile authority, repeats target and security witnesses after I/O and fits the observation to the actual checkpoint. Both UTF-8 streams and discovery lists clip with truncation/count evidence. Revocation withholds data without rewriting consent; completed checkpoints cannot read again. Later reads need current policy again. No SDK, image preparation, command, initialization or cleanup occurs.

Migration `20261018025233_TenantSandboxReadReview` extends only the existing approval CHECK. Typed read kind/Session/reference shape, small-payload bound and disjoint shadow columns are constrained; canonical values and actual multi-profile ownership are checked by the existing transaction. No table, column, index, queue or alternative observation owner was added. Disposable upgrade/reopen preserves prior control JSON byte-for-byte and the retained wait. No user database was migrated or reset.

## Focused execution

| Check | Observed result |
| --- | --- |
| Final Infrastructure regression: new read Review, model controls, Sandbox approvals/reset/Stop/profile-switch and existing local/Credential continuations | **129 passed**, 0 failed, 4m13s |
| Final affected API/hosted model and owned Sandbox HTTP verification | **22 passed**, 0 failed, 54s |
| Focused Application typed authorization, existing inspection and continuation contract filter after updating old read-only offering assertions | **16 passed**, 0 failed, 133ms |
| Approval UI and conversation-update focused tests | **16 passed**, 0 failed |
| API build | Passed, 0 warnings/errors |
| Frontend TypeScript/Vite build | Passed; existing SignalR annotation/chunk-size notices |

The final Infrastructure selection was:

```text
Tenant_Sandbox_read_Review_ | Tenant_Sandbox_model_control | Tenant_Sandbox_approval_ |
Tenant_Sandbox_exact_approval | Tenant_Sandbox_approved_ | Tenant_Sandbox_human_Stop |
Tenant_Sandbox_Stop_transaction | Tenant_Sandbox_reset | Tenant_Sandbox_profile_switch |
Approved_continuation_ | Cancelled_or_expired_continuation_ | Credential_successor_
```

The final API selection was `Tenant_Sandbox_`, `Owned_instance_sandbox_HTTP`, `Tenant_verification_HTTP` and `Owned_instance_sandbox_verification_HTTP`. These are focused filters, not the full CI suite.

New read cases exercise both InMemory and SQLite: exact once-only status; process progress; two large streams near the 205 KiB retained checkpoint boundary; reject/cancel; stale selection; executor and delivery-profile revocation; mismatched pending calls; discovery grants that cannot expand the saved set; revocation during profile I/O and during the final target snapshot; permission-filtered inbox totals; a reviewer needing two exact profile approval scopes; reviewer loss before consumption; secondary Session independence; no unnecessary/unapprovable wait; direct decision-store target recheck with stale cancellation recovery; malformed CHECK values; and prior-control migration/reopen preservation.

The real hosted Synthetic Runtime fixture uses production admission/coordinator/mailbox/checkpoint/approval/effect/read owners with a controlled model. Profile, status and process Review release capacity while waiting, then place the bounded observation into the next actual model input. Process rejection returns no observation. The controlled Sandbox peer is stopped before the human decides in these read cases: consent and continuation perform no SDK request. Existing command, control, background and indeterminate-no-replay scenarios remain in the same exercised matrix. This is actual backend/runtime proof, distinct from the browser projection adapter below.

Initial focused failures were corrected and rerun: output-sentinel assertions collided with display summaries; checkpoint fixtures tried to restore spent budget; a profile-switch fixture retained active execution; and old read-only tool-offering assertions expected no authorization schema. Corrected fixtures preserve actual timing/budget and factual completion, use a payload-only sentinel and expect only the exact read operations. The final selections above pass.

## Browser interaction and design

Playwright MCP used a new isolated Synthetic API on port 5098 and Vite on 5188 with disposable files under `/private/tmp/agent-core-read-review-ui-4jFEj3`. Sign-in, current tenant and access came from the actual app. Approval list/detail/decision responses used a controlled same-origin adapter to exercise the production inspector and command recovery; those responses are not evidence of a real backend human decision.

Expected and observed:

- Independent review explained why the requester could not Approve/Reject; both were disabled while permitted Cancel remained enabled.
- Exact saved read showed long profile labels/revisions, wrapped generation/receipt IDs, disclosure and one-read boundary without before/after rows or output.
- After a controlled policy change, Refresh details enabled a qualified decision. Native confirmation explained exact one-time consent and the current-access check before delivery.
- The first intentional decision returned `409 stale_revision`. Submission stayed disabled until explicit Refresh details and fresh confirmation. Both POSTs were checked for the exact action hash, request revision and security revision; there was no automatic retry.
- The second POST succeeded. The inspector showed **Read authorized**, separated consent from delivery, removed pending decision controls and displayed no **Applied** label.
- At settled 1440×900 and 390×844 layouts the Drawer measured 736px and 390px respectively, with matching scroll/client widths and no horizontal document overflow. The initial viewport-resize capture caught the native transition; settled geometry and screenshots were then rechecked.

The existing Ant Design Drawer/Descriptions, responsive labels, section gaps, footer, confirmation and shared Chat result-label owner were reused. No CSS/token or UI-kit change was required. Settled captures were visually inspected: `/private/tmp/agent-core-read-review-ui-4jFEj3/read-review-settled-390.png` and `read-review-settled-1440.png`. These are disposable local evidence, not repository assets.

The only browser console error was the intentional decision409. Network evidence also includes expected aborted initial StrictMode GETs with successful replacement reads, plus an unfinished GET caused by a first adapter script error; the corrected adapter/reload completed the full recovery scenario. No unexpected application request failure remained in that scenario.

## Remaining scope

Tenant Sandbox configuration/catalog/Session UI, other resource Access/effect families, Governance/platform, voice/rich Chat delivery, live input arbitration, autonomous origins and final obsolete-authority/schema consolidation remain open. The earlier SQLite preference reliability follow-up remains unresolved. Existing shared inspector verification does not accept the future Sandbox workspace UI.

No hosted CI/full local CI suite, live provider spending, live Docker qualification, deployment, Real-host restart or user-data conversion/reset was performed. Verification used deterministic local Synthetic, controlled SDK peers and disposable stores. The broader proposal goal remains active.

Changed Markdown passed local link/fragment checks (14 added links), fence balance and parsing of all three added complete JSON examples. Whitespace validation passed. The disposable API/Vite processes were stopped and the verification browser tab closed; local screenshots remain available.
