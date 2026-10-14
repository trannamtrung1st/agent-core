# Definition and Instance Sandbox navigation — 2026-10-14

## Finding and change

Definition profile authorization existed under Capabilities, while Instances had a
first-class Sandbox page. Definition drafts now expose Sandbox immediately after
Execution budgets, matching Instance navigation. It owns allowed/default profile
selection, dirty-page attribution, current Global profile facts and inventory
recovery. Capabilities links to Sandbox and retains command authority.

Published-version inspection has a read-only Sandbox tab. It renders the immutable
policy separately from current Global profile facts; no profile verification or
editing is offered. Instances retain bounded overrides, supplement initialization,
saved-setup verification and their existing Save/Discard/Reset/CAS behavior.

`SandboxProfileDetails` shares image, resources, command budget, lifecycle, storage
and initialization presentation. Existing shared configuration panels own outer
insets and spacing; stacked fields own label gaps and settings classes own control
widths. No new CSS, UI kit, API, persistence schema or execution behavior was added.
Search Admin and legacy draft `capabilities#sandboxPolicy` links reach Sandbox.

## Focused local verification

- Vitest: 35 tests across `appRoute`, `DefinitionSandboxPolicy`,
  `InstanceSandboxSummary`, `DefinitionVersionsTable` and
  `AdminInstanceNavigation` passed. Regressions cover grant/default reconciliation,
  removing the final grant, failed inventory preserving policy and disabling both
  selectors, Retry recovery, stale response cancellation, published read-only
  inspection and canonical navigation.
- Version inspection emits its existing jsdom `NaN` height warning; the two tests pass and live browser inspection has no console errors.
- Existing AdminApp `saves typed capabilities through the draft editor`: passed.
  The remaining AdminApp tests were outside this focused run.
- `pnpm --dir web run build`: passed. Existing SignalR annotation and large bundle
  warnings remain.
- Playwright MCP used a disposable Synthetic SQLite/API/Vite host on 6480/6473.
  Created a v30 General Assistant draft, opened Sandbox via its sidebar, removed
  its sole grant, visited Capabilities and observed `Sandbox · Unsaved` with no
  visible profile selectors there. Saved revision 2, reloaded through the legacy
  anchor, restored the grant/default, saved revision 3 and confirmed persistence.
- Injected a 503 inventory response in the test browser. Saved IDs remained
  visible, both selectors were disabled, private error text stayed hidden, and
  Retry profiles recovered the current profile after removing the injection.
- Inspected immutable v30's Sandbox tab: allowed/default profiles and current
  Global details appeared; zero editable profile inputs or Verify buttons.
- Created a disposable Instance and checked its Sandbox page: effective facts and
  ordered Global/Instance initialization remained visible; the existing Verify
  Instance setup action remained available. Setup execution itself was not rerun
  for this presentation-only extraction.
- Inspected Definition layout at 1440, 1024, 768, 390 and 320 CSS pixels; inspected
  Instance layout at 1440 and 390. Controls wrapped, description labels adapted,
  long image references stayed within the page, and body width never exceeded
  viewport width. Default selection uses 320px on wide layouts and shrinks to
  262px at 320px; field label gaps remain 8px. Screenshots are retained under
  ignored `local/definition-sandbox-*` and `local/instance-sandbox-alignment-*`.
- Search Admin's Sandbox environment result opened the exact draft Sandbox anchor.
  Normal-path console had no errors; related API reads/writes succeeded. The
  deliberate 503 was the sole injected error in the recovery scenario.
- Scoped `git diff --check`: passed. Canonical frontend specification and design
  context synchronized.

## Boundaries

Published Definition inventories retain Versions/Drafts navigation; configuration
is authored in exact drafts and inspected per immutable version. This is a frontend
consistency enhancement, not new milestone acceptance. No CI, backend suite,
Docker acceptance, provider call or execution demo was rerun. Earlier real Sandbox
acceptance evidence remains in its own ledger. Normal user data, services, Docker
resources and unrelated staged/unstaged changes were preserved. Only the temporary
Synthetic host is stopped; its evidence/data is retained.
