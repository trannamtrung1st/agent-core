# Tenant Chat Session Sandbox UI verification — 2026-10-18

Status: focused Phase 7 adapter over `15ab37c377db6c2a0e2be044baa2a031ad78af97`. The full multi-tenant IAM proposal remains active. This adds supported stored inspection and existing-generation reset/Stop/profile-switch UI; it does not claim live SDK qualification or final authority/schema cutover.

## Implemented scope

The current tenant Chat header opens `TenantSessionSandboxDrawer` using the canonical control projection and existing exact commands. Explicit tenant/session paths use cookie/CSRF identity transport; no administrative Instance lookup, global catalog or legacy owner fallback constructs its choices or authority. Raw server hints remain independent from Chat editability and Instance update permission. The [Session interaction contract](../19-multi-tenant-iam-ux.md#session-sandbox-controls-interaction-contract) preceded this adapter; [Frontend](../13-frontend-implementation-spec.md#tenant-chat-session-sandbox-inspector) owns final behavior. The [24-case backend prerequisite](tenant-sandbox-controls-read-verification-2026-10-18.md) retains its separate evidence scope and was not rerun in this frontend-only change.

`SessionSandboxFrame` extracts the incumbent native 640px/full-mobile Drawer, body inset, parent section gap, Escape/close and opener-focus behavior for both consumers. Existing responsive Descriptions, confirmations, diagnostics and bounded escaped `SandboxEvidence` remain shared. Background command output explicitly says combined; the truncation message now names its actual evidence kind. Reusable source availability, stored generation/initialization, latest receipt and exact historical receipt remain separate. No CSS, theme, token, font, raster, UI kit or new backend route/schema/worker was added.

Native confirmations bind the displayed organization/Session/generation and saved security/Session/lease/execution or profile/Instance revisions. Selection and reset explain retained canonical files and fresh Review before future commands. Busy controls prevent duplicate requests. Successful or acknowledged unreadable responses recover by read; stale/uncertain failures disable another control until explicit Reload. Definitive read/command access loss removes protected detail, and scope retirement aborts requests and destroys confirmation. A bounded UUID inspector reads an exact saved receipt without changing current control witnesses. Close restores the Sandbox opener; post-command read restores Reload Sandbox. Reload is explicit and does not probe a provider or repeat a command.

## Focused checks

| Check | Result |
| --- | --- |
| New tenant Session Sandbox interaction cases | 10 passed |
| Existing legacy Drawer/shared-shell cases | 24 passed |
| Existing Sandbox transport/status cases | 14 passed |
| Organization catalog/shared evidence cases | 8 passed |
| Instance Sandbox/shared evidence cases | 8 passed |
| TypeScript/Vite build | Passed |

Commands:

```sh
pnpm --dir web exec vitest run src/features/chat/TenantSessionSandboxDrawer.test.tsx src/features/chat/SessionSandboxDrawer.test.tsx src/services/sandboxes.test.ts
pnpm --dir web exec vitest run src/features/access/TenantSandboxes.test.tsx src/features/access/TenantAgentSandbox.test.tsx
pnpm --dir web run build
```

Final selections total 64 passing frontend cases. The first combined selection passed 46/48: a stale-recovery test clicked Reload before the native confirmation had finished closing, and a title query matched Ant Design's duplicate hidden/visible title nodes in JSDOM. Waiting for the enabled control and selecting the actual confirmation title fixed the fixtures; focused/new and final combined reruns passed. Final shared consumers passed 16/16, and final build passed in 5.88s with existing SignalR annotation/chunk notices. Initial TypeScript no-emit also passed. No source/test edit occurred while a build/test process was live.

Tests cover exact payloads for reset/Stop/selection, independent read-only hints, empty state without provisioning, escaped/truncated combined output, stale read/fresh confirmation, acknowledged unreadable response without replay, denied read/write retirement, exact historical inspection and aborted former scope/confirmation teardown. Shared-shell and evidence consumers retain their own regression scenarios.

## Browser execution and proof boundaries

Playwright MCP used an isolated Synthetic InMemory API on 5098 and Vite on 5188, with files/captures under `/private/tmp/agent-core-session-sandbox-ui-iSeerp`. Health confirmed Synthetic before sign-in. Browser/OpenSandbox execution was disabled; existing user data and Real hosts were preserved.

Actual backend and visible UI:

- Signed in, created an owned tenant Instance through public setup HTTP, entered Chat, sent a message and observed `Hello from synthetic.` through the real tenant conversation path.
- Opened the new header control and read the actual scoped empty Sandbox projection. No environment was provisioned and all target-dependent controls stayed disabled.
- After controlled denied-state checks were removed, reopened the actual empty projection and kept Chat usable.
- For the extracted shell's existing consumer, created one legacy Instance in this same disposable InMemory host through its visible Admin UI, opened a separate Synthetic Chat and inspected its actual empty Sandbox Drawer at desktop/mobile widths. This is shared presentation regression evidence, not acceptance of the remaining legacy authority or final cutover.

Controlled browser GET/POST adapters, separate from backend mutation/SDK proof:

- Reset native confirmation sent one exact security 11 / Session 7 / generation / lease 3/latest-receipt request. The reply/read showed pending cleanup, not confirmed termination, and focus returned to Reload Sandbox.
- A controlled 409 disabled another control. Explicit Reload followed by a new confirmation sent fresh security 22 / lease 9 witnesses; no automatic retry occurred.
- One controlled 202 unreadable acknowledgement recovered via read and retained a single POST after further Reload.
- Stop sent exact execution revision 31 without extra caller authority or the reset-only execution-ID field. Profile switch sent only the deliberately chosen readable target/revision 4 and Instance 5 plus saved witnesses; its confirmation explained retirement and fresh Review. These adapters dispatched no SDK operation.
- Exact historical receipt inspection rendered a different generation/command and expanded inert stored output while current latest evidence stayed separate.
- Read-only hints disabled mutations. Controlled exact control-read 403 removed private detail/controls. Native Close restored focus to the Sandbox header button and the Chat composer remained enabled. Cancel closed its native confirmation and released controls.

The first sign-in locator omitted the required-field marker; the form-label locator completed the journey. An initial browser fixture used a server-global `URL`/state assumption unavailable in the MCP execution context; the corrected scoped handler used disposable browser fixture storage and was removed after use. Neither failure was treated as product evidence or triggered a backend mutation.

Only intentional stale 409 and denied 403 appeared in the browser error audit. Expected initial StrictMode aborts had successful replacement reads. Tenant UI uses canonical tenant routes; the separate existing-consumer check intentionally exercised its incumbent legacy transport. No unexpected application exception or warning was observed in completed flows.

## Visual evidence and fresh finish review

Current valid captures: `ready-top-{1440,1024,768,390,320}`, `controls-{1440,1024,768,390,320}`, `pending-{1440,320}`, `confirmation-320`, `exact-receipt-320`, `read-only-320`, `denied-320`, `empty-{1440,320}`, `chat-header-320`, `shared-legacy-{1440,320}` PNGs under the run root. These 21 captures were opened and validated. Native main/Drawer scrolling means each image shows its named region, not the complete content in one frame.

Initial mid-motion captures were invalid and overwritten. A temporary zero-duration capture stylesheet stalled Ant Design's close callback; removing it restored normal Cancel/Close and released controls. Final affected captures settled normal native motion, and current Drawer bounds were checked explicitly. This was evidence setup, not an application defect or source workaround. The final combined-output correction was recaptured before review.

Document width equaled viewport at 1440/1024/768/390/320px. Drawer body client/scroll widths were 640/640/640/390/320px, with x 800/384/128/0/0 and 16px parent gaps. Native body inset is 24px; controls retain the existing narrow 40px minimum. Long images, IDs, names, copy and confirmation actions wrap without horizontal overflow.

A fresh Impeccable reviewer inspected all 21 captures and relevant source, returning **SHIP** for the bounded presentation with no material findings or required fixes. Loading/hover, measured contrast, other control confirmations and dedicated stale/malformed recovery visuals remain outside that visual acceptance; interactions/focus rely on separately reported runtime evidence. Controlled permission presentation is not backend authorization proof. The design-context documenter records this ordinary extension while preserving normative tokens and historical sidecar fingerprints.

## Remaining work

This supersedes Session-Sandbox-UI-pending notes only for the supported adapter and existing-generation controls. Other resource Access/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final obsolete-authority/schema consolidation and the earlier SQLite preference reliability follow-up remain open. The full proposal remains active without milestone acceptance.

No hosted/full local CI, live provider/registry/SDK/Docker qualification, deployment, user-data reset/migration or Real-host restart was performed. Successful live initialization, interruption and retirement through this UI remain unverified. Current exact backend command/store proof remains in the prior report. No application worker or authority was bypassed by the production adapter.

The disposable API and Vite processes were stopped and the browser closed after verification. Final changed-Markdown link/fragment/fence checks and `git diff --check` passed; normative design frontmatter, theme/CSS and the historical sidecar remained unchanged.
