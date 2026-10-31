# Final navy UI and structured settings refinement — 31 October 2026

Status: **complete for the controlled local Synthetic refinement; Ready with measured limits**. All 80 unique browser cases have passing latest results across recorded runs, 77 focused unit cases pass, and the final build/inventory checks pass. This is not one pristine full-suite run or hosted CI. This is the owner-directed follow-up to accepted Order 6, not a new architecture, feature initiative or production qualification. Baseline: `ab6f466a9339a46ae830cd58b7052bf18f692c64` on `main`. The original [Order 6 ledger](product-experience-delivery-2026-10-30.md) and its evergreen captures remain historical evidence.

## Result and design decision

The [experience contract](../product-experience-contract.md) now selects deep navy/slate-blue. `antdTheme.ts` remains the only runtime palette authority; AppShell projects its aliases into CSS and portals. Semantic success green, warning amber and error red remain independent. Native Office/document canvases retain their own presentation. No second UI kit, dependency, backend/API/schema change or Group Chat feature was introduced.

Shared shell changes remove the redundant Organization label/vertical divider, give the organization selector its available width, keep trailing actions grouped, and use readable disclosure labels with an explicit chevron gap. The icon registry intentionally treats every canonical navigation ID; unknown secondary IDs render iconless and warn in development. [Icon mapping](assets/final-ux-refinement/navigation-icon-map.json) and the registry test bind that policy.

AgentConfigurationPanel, existing Collapse sections and AgentSettingRow provide one responsibility panel with divided descriptive rows and bounded trailing controls. Row stacks, panel bodies and draft sections own container queries. Scalar controls stack when their container is narrow; suitable switches/checkboxes remain trailing beside readable descriptions. Native labels, hints, validation, inheritance/source/reset, sparse drafts, disabled dependencies, revision witnesses, dirty navigation and unknown-outcome recovery stay with their existing controllers.

Migrated consumers include Cooperation, Chat visibility, inherited Instance settings, Definition scalar settings, Self-management mode/procedures, shared State & Isolation fields, organization profile, organization model default/routing and saved Chat retention. Evaluation governance and account password changes receive coherent responsibility panels. Instructions/code/JSON, capability/resource lists, class-budget matrices, policy inspectors, credential/provider editors and Space/Environment authoring retain deliberate full-width forms or specialized layouts. Tables remain tables.

The Agents collection has Agent/Role/Lifecycle/Managed/Chat visibility/Actions columns on desktop and a compact identity/status summary on narrow screens. Access remains inline; a labeled native More menu preserves Connections, Activity and Lifecycle. Shared pagination suppresses redundant one-page navigation while keeping totals and usable page-size recovery. Memory uses a compact empty state only when the **scope total is zero**, retaining page recovery for an empty requested page with nonzero total. Organization actions stay together when they fit. Chat groups Agent and retention on wide containers, displays the human Agent name in the selected value, and retains role/system/version metadata in expanded options and the native title.

## Bounded audit and defect closure

Independent design assessment A used current source and existing original captures without detector output. Assessment B independently ran the admin detector (`[]`) and source inventory. The design brief prioritized shared composition ownership over page-specific offsets. The green baseline was reproduced through the real Synthetic app; no screenshot-crop viewport metadata was invented.

The final reviews are [A](assets/final-ux-refinement/independent-a.md) and [B](assets/final-ux-refinement/independent-b.md). A inspected 131 staged captures, 45 frozen-runtime confirmations and 3 narrow array-width corrections; B inspected 62. These overlap and include narrow confirmation captures; they are not unique-page counts. Contact-sheet review establishes geometry/consistency; targeted originals establish finer details. Neither reviewer claims independent interaction execution or every possible overlay/state.

Demonstrated defects corrected during the bounded pass:

| Finding | Owner/fix | Regression or confirmation |
| --- | --- | --- |
| Generic gears/prefixed IDs, cramped disclosure markers | Complete semantic map; native chevron with 12px gap and body-size group labels | Registry test, real organization/agent/draft/platform navigation |
| Repeated scope/back chrome; truncated organization/Agent names | Shared shell hierarchy; remove duplicate body Back to Agents; short selected Agent label | Desktop/mobile captures and Chat draft/scope journeys |
| Tall loose Cooperation checkboxes | Shared responsibility panel and rows; explicit save and dependent wake reset retained | Checkbox enable/wake/disable/save/reload at five widths |
| Draft scalar settings still used legacy grids | Existing Definition field owners and shared State & Isolation fields use the same row composition | Draft route captures; source/functional regressions and MCP confirmations |
| Standalone mobile rows compressed labels into single letters | Put query containment at both panel-body and draft-section owners | Trigger/Voice/Memory/visibility 390/320; measured label widths, readable-measure assertion |
| Wide multi-select lists inherited the old scalar width cap | Remove that cap only inside full-width setting rows | Fresh Synthetic all-settings check asserts >90% row width; inspected 1440/390/320 captures |
| One-page styling removed an active size chooser | Hide only inert page navigation; keep size chooser | MCP selected 50, then returned to 10 on a one-page collection |
| Empty current Memory page hid recovery despite remaining facts | Compact Empty requires total zero | Focused regression traverses page 2 empty → previous page → retained fact |
| Routing/Governance text labels stopped toggling checkboxes | Explicit native IDs/htmlFor | MCP text-label toggle/restore; focused control tests |
| Row group duplicated the input’s accessible label in profile tests | Keep native input label association; group retains description without duplicating its name | Five profile persistence/conflict/retirement tests |

No separately named Standard Implementation Review Checklist file exists in the repository. Independent review covered correctness, scope, IAM/effect ownership, asynchronous/draft behavior, compatibility, verification, operations, documentation and simplicity against canonical invariants. No material unresolved source/presentation finding remains in the reviewed scope.

## Coverage and source provenance

The [coverage matrix](assets/final-ux-refinement/coverage.json) replaces the active source-inventory target; the prior matrix remains untouched. The inventory now includes product configuration panels and explicit shared shell/row/toolbar/icon families, and fingerprints the complete active frontend source tree rather than route owners alone. System route evidence uses canonical IDs consistently.

All 401 discovered entries have an owner, presentation disposition, settings-pattern decision, direct/shared browser evidence scope, tests and limitations. Direct evidence records 72 navigable entries and all 10 sections actually exposed by the managed-agent editor (82 distinct route/section entries). Direct route/section records are distinguished from representative shared native compositions; a suite association is **not** a claim that every role/state/width combination ran. The import graph is a module reachability aid, not proof that every branch rendered.

[Image manifest](assets/final-ux-refinement/image-manifest.json) records raw paths, hashes, viewports, capture method and explicitly identified reviewers. Earlier stage images can contain defects; narrow `array-correction` captures supersede the earlier capability-list width. [Capture source epochs](assets/final-ux-refinement/capture-source-epochs.json) preserve earlier runtime digests rather than assigning the latest source to old images. Other final confirmation captures supersede their named failed states. Curated [images](assets/final-ux-refinement/images) retain representative results. The committed [portable visual evidence package](assets/final-ux-refinement/visual-evidence.md) now preserves all 548 original PNGs and their records independently of temporary storage. Historical `/tmp/agent-core-navy` paths retain capture provenance; archive-relative paths provide portable access. Source fingerprints/digest and exact results are recorded in [verification](assets/final-ux-refinement/verification.json).

Reproduce inventory and evidence binding from the repository root:

```sh
node web/scripts/product-experience-inventory.mjs
EXPERIENCE_EVIDENCE_ROOT=/tmp/agent-core-navy node web/scripts/final-ux-evidence.mjs
node web/scripts/product-experience-inventory.mjs --check
```

The evidence command requires actual capture/review files; it does not fabricate browser results. Active Impeccable briefs and `design.json` are synchronized to the navy contract and refreshed source hashes. DESIGN/PRODUCT remain tooling guidance; `/docs` owns behavior.

## Functional evidence

Office follow-up selected/copied actual CSV/XLSX cells (including formatted `$1,234.50`) and rendered protected Word header content. The organization switcher was opened at all five widths; Escape restored focus to its opener.

The native disposable SQLite launcher used isolated data under `/tmp/agent-core-navy-runtime-approved`, API `127.0.0.1:5088` and Vite `127.0.0.1:5179`. `/health` confirmed Synthetic before use. Browser/OpenSandbox execution stayed disabled; speech and model execution used Synthetic adapters. Existing user installations/data were not reused or reset. Task-owned API/Vite processes have stopped, both ports are closed, and all three disposable data roots were removed. Raw review/capture evidence is retained; [cleanup evidence](assets/final-ux-refinement/cleanup.json) records the exact scope.

Expected versus observed representative actions:

- **Chat:** selected the short Agent label, sent text, observed exactly one completed “Hello from synthetic.” response, retained the unsent draft across same-organization administration, uploaded/previewed a Markdown file, and restored opener focus after inspectors. Temporary retention, reload, archive and cross-scope retirement use their existing feature suites.
- **Settings:** changed Language, saved, reloaded and observed the persisted override; reset restored inherited value. Category changes retained drafts. Profile navigation prompted before discarding. A reader saw disabled controls and a denied mutation remained denied.
- **Cooperation:** enabled direct messages and wake, disabled direct messages, observed wake cleared/disabled, explicitly saved and reloaded, and observed the saved dependency state. All five widths retained usable controls.
- **Collection actions/recovery:** opened Connections through More, changed page size 50→10 without losing the selector, and recovered an empty nonzero-total Memory page through Previous.
- **Workflow/Space:** authored/validated/published/started an immutable Synthetic Workflow and independently recorded process sign-off to completion. Space conflict retained its draft, explicit reload recovered current revision, and Environment variables survived reload. Evaluation proof and approval authority stayed separate.
- **Control boundaries:** feature journeys exercised independent requester/reviewer identities, scoped read revocation, stale/unknown command recovery, model default/route pins, delegated work with human guidance, scheduled work and native reset confirmations. Synthetic voice kept capture active during output and required a fresh click after reconnect at desktop/mobile widths.

Exact commands, counts, per-case results, source fingerprints and repaired attempts belong to [verification.json](assets/final-ux-refinement/verification.json). The code/test digest is `efcf42ada18c868e75e54eddb311724526ff786a50dfe7217dbefc34f2de0d19`. The UI family suite is local Synthetic verification, not hosted CI or the full repository CI suite. No backend tests were needed for an unchanged backend contract.

## Contrast, accessibility and limits

[Measured contrast](assets/final-ux-refinement/contrast.json): enabled primary normal/hover/active-token white labels are 6.0774/5.3815/8.1621:1; body text 14.7204:1; tertiary/placeholder on elevated panels 6.09:1; blue links on elevated panels 7.4292:1. Native keyboard outline was visibly solid 3px, `#657d9c`, with 3.9605:1 against the container and 3.3769:1 against elevated surfaces. Normal/hover/disabled/keyboard styles were sampled from the actual browser; active contrast was measured from the resolved AntD token.

Automatic approval review rejected pressing Publish solely to sample active styling. No publication occurred in that inspection; read-only hover/keyboard inspection and resolved-token tests supplied the safe alternative. Disabled controls retain native alpha styling and text/property distinctions; enabled-text thresholds are not applied to exempt disabled states. Native document canvases, source-content colors and semantic green status are intentional exceptions, not remaining evergreen branding.

Routine direct captures use 1440/390, with 1024/768/320 sampling of changed families, label measure checks and no document overflow. Keyboard/focus/confirmation/read-only/error/stale/unknown states were sampled through actual interactions. This is not universal WCAG certification, a complete combinatorial role×state×width proof, real browser-menu zoom certification or a physical assistive-device study. Native browser/sandbox remote frames, live providers, audible voice quality, GPU/native services and customer deployment require their own ready environments. Synthetic readiness/unavailable states remain honest; no operational control was removed to conceal these limits.

The build retains the existing large lazy Office/application chunk warning; no unrelated bundle/performance redesign was introduced. Hosted CI was neither requested nor run. The next roadmap activity remains Self-Hosting & Local Development Readiness and owner manual validation; this refinement does not start either milestone.
