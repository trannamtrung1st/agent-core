# Self-management creation parity — 2026-10-16

This focused fix closes the legacy New instance harness form gap. It does not reopen a milestone or claim milestone acceptance.

## Change

Definition defaults, New instance and Instance Self-management now share `SelfManagementFields`, its nine management areas, three automatic-change choices, procedure descriptions and responsive spacing. Ant Design controls remain direct product components. The shared field stack owns 16px gaps and removes nested Form.Item bottom margins; checkbox choices use two columns above 768px and one below.

New instance loads defaults from the selected exact publication, stages only explicit field overrides and saves them through the existing Self-management endpoint. Unchanged creation retains Definition inheritance without a policy write. Explicit Off/false choices remain overrides; mode toggles preserve other choices. Changing the selected publication resets management overrides. Failed defaults reads block submission and offer Retry.

Creation and policy save remain two revision-protected writes. A policy-save failure leaves a truthful partial-success notice and the draft open. Retry reads the current revision and saves to the same created Instance; Definition/persona selection is locked after creation. Open instance continues with the persisted policy. No rollback or duplicate creation is implied.

## Focused local checks

All commands run from `web/`:

- `pnpm exec vitest run src/features/admin/NewInstanceButton.test.tsx src/features/admin/DefinitionSelfManagement.test.tsx src/features/admin/SelfManagementSection.test.tsx src/features/admin/HarnessManagementSection.test.tsx` — 19 passed. Creation tests were rerun after test synchronization fixes; existing Instance freeze, inheritance, conflict and archived controls pass.
- `pnpm exec vitest run src/features/admin/AdminApp.test.tsx -t 'creates a managed instance|prefills custom persona|prefill|persona load|deprecated'` — 8 passed, 36 intentionally unselected.
- `pnpm exec vitest run src/features/admin/AdminApp.test.tsx -t 'ignores stale persona|retries failed persona|shows one effective published'` — 3 passed, 41 intentionally unselected. These are distinct from the previous selection: 30 focused tests total.
- `pnpm build` — passed TypeScript and Vite production build. Existing SignalR annotation and bundle-size warnings remain.
- `git diff --check` — passed for this change.

## Synthetic browser evidence

Playwright MCP exercised the live frontend at `127.0.0.1:5186`, proxying to a separately started Synthetic host at `127.0.0.1:5096`. The backend reused the existing compiled `e6d81cab` API because this fix changes no backend code. Data and workspaces were isolated under `/private/tmp/agentcore-harness-parity/`; the existing user application on 5080/5173 was left running. Initial automatic restore made no progress, so the backend used `dotnet run --no-build`. The test host's health confirmed Synthetic and its open SQLite handle was verified before test-only publication.

| Action | Expected and observed |
| --- | --- |
| Create an Instance with Managed mode, all nine areas, automatic conversation changes and disabled harness authoring | Saved policy revision 2 contains exactly those choices; untouched configuration assistance stays inherited. Reopened Instance controls match. |
| On the Instance, enable automatic skill changes and choose Inherit areas, then Save configuration | Policy revision 3 retains automatic choices and resets the area override to null; effective areas equal Definition defaults. |
| Inject one policy PUT 409 after successful creation, then Retry self-management | Partial-success notice and checked Settings survive. Exactly one creation POST occurs. Retry reads the policy revision, succeeds and opens the same Instance. |
| Edit Definition defaults through the shared UI, save and validate | Revision 2 stores Managed, Settings/Skills/Resources, automatic skill changes and disabled harness authoring; server validation has no findings. |
| Publish that disposable test revision and create from it without customizing management | Dialog shows exact inherited choices. Creation makes zero policy PUTs; readback has `source=definition`, no overrides and the same effective policy. |
| Stage a Connections override, switch from that publication to the built-in version | Overrides reset; Off and empty areas are inherited, and Managed-only choices disappear. |
| Inspect shared controls at 1440×900, 768×900 and 390×844 | No document horizontal overflow. Consistent 16px field gaps and zero Form.Item bottom margins. Checkbox grid wraps to one column on narrow screens. Desktop/mobile creation and Instance rendering inspected. |

Console/network inspection found the initial owner-capability request made before the test backend was ready and the deliberately injected 409. Subsequent exercised policy/default reads and writes succeeded. No provider calls were needed. Screenshots remain temporary verification artifacts under `/private/tmp/agentcore-harness-parity/`.

Automatic review initially rejected test publication as a shared persistent change. Read-only process/database checks established that it targeted only the disposable Synthetic host; the same publication was then approved and completed there. No user Definition was published.

Hosted CI, the full local suites and live-provider Chat authoring were not run or requested. Runtime evidence establishes configuration creation, persistence, inheritance, edit/reset and retry behavior; existing server authorization semantics are unchanged.
