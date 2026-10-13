# Admin information architecture reorganization verification

Date: 2026-10-13. Scope: the approved local Admin UX reorganization proposal, on `main`.

## Baseline and candidate

The proposal inspected `a7a44ebafb52d285696c9e9cb4081f0082ab57e4`. Work began on fetched `main` at `8fc22203dd1666482be38e5b7c9b3f3655771afb`; the intervening commit changed only TODO.md. The implementation preserves the existing backend, APIs, persistence, provider boundaries and security authority. All runtime data used here belongs to disposable Synthetic fixtures.

Candidate source SHA and final check totals are recorded below after the final verification run. This report does not claim hosted CI or a new milestone freeze.

## Result and module ownership

`AdminShell` owns the shared header, breadcrumbs, Search Admin, contextual sidebar and responsive navigation Drawer. `adminNavigation.ts` is the typed source for canonical pages, groups, heading copy, routes and approved search labels. `appRoute.ts` parses canonical/legacy/malformed routes and retains only approved redirect queries. `AppRouter` preserves Chat suspension and guards native history departure.

`AdminApp` composes inventories and exact-owner workspaces. `AdminGlobalPages` owns inventory/create dialogs; `adminPresentation` and `AdminRecordSummaries` own shared inventory formatting and record summaries. `InstanceDetail` retains private editors; `InstanceProfile`, `InstanceOverview` and `EffectiveConfigView` separate editing from saved inspection. `useInstanceSettingsController` owns revisioned sparse patches and independent section drafts; `InstanceSettingsSection` selects presentation groups. `DefinitionDetail` owns the revisioned lifecycle; `DefinitionDraftWorkspace` composes one shared candidate, resources, Skills and evidence. `RetainedAdminPage`, draft-status reporting, anchor focus and unsaved guards provide workspace behavior without a second UI kit.

## Capability and route preservation

`I` below means `/admin/instances/{instanceId}`. `D` means `/admin/definitions/{definitionId}/drafts/{draftId}`.

| Existing editor or workflow | Canonical owner | Preservation evidence |
| --- | --- | --- |
| Default Admin inventory | `/admin/instances` | Bare `/admin` replacement; canonical/global history checks |
| Definitions / immutable inspection | `/admin/definitions`, `/admin/definitions/{id}/versions` | Original read-only drawer, copyable instructions, local inspector tabs, deprecation/fork |
| Credentials / email / OAuth | `/admin/connections/credentials` | Shared inventory independent from Instance bindings; protected input and one-time value handling retained |
| Built-in / Webhook Events | `/admin/connections/events` | Catalog, subscribers, credential issuance, source navigation and Automation editor retained |
| Host Browser privacy | `/admin/platform/browser-privacy` | One global editor; protected/unmasked/disabled, CAS recovery, deployment ceiling and restart copy |
| Persona, lifecycle and Definition adoption | `I/profile` | Persona Form/JSON, archive/unarchive, apply/version, independent retained draft |
| Instructions / conversation / behavior / initiative / voice | `I/behavior` | Sparse section Customize/Save/Discard/Reset; one save preserves another draft |
| Interactive models, providers, execution budgets | `I/models` | One budget editor; model reasoning order, provenance, concurrency and next-Run behavior |
| Skills / resources / Workspace | `I/skills`, `I/resources`, `I/workspace` | Separate inherited/local authority; existing drawers, import, toggles, download, revisioned deletion |
| Capabilities / credential bindings / Browser state | `I/access` | Exact grants and host locks; protected bindings remain distinct from authority |
| Management and reviewed Harness change | `I/self-management` | Off/Assisted/Managed, freeze/unfreeze and reviewed shared promotion |
| Automation registrations / restrictions / unattended defaults | `I/automations` | One collection/editor, separate unattended model, exact `?automation=` source and `?run=` inspection |
| Memory / Experience | `I/continuity/memory`, `I/continuity/experience` | Memory policy, learned records, consolidation/maintenance, checkpoints and source provenance |
| Sessions / independent Runs | `I/activity/sessions`, `I/activity/runs` | Exact Session/Run details and search; continued Chat and original results remain distinct |
| Effective diagnostics | `I/effective` | Saved read-only values; Browser diagnostics link to global privacy |
| Definition candidate | `D/overview`, `profile`, `behavior`, `models`, `skills`, `resources`, `capabilities`, `policies`, `test-publish`, `advanced-json` | Shared candidate and JSON; invalid JSON retained/repairable; explicit save, evaluation, publish and delete |

Legacy migration checklist (unit and browser exercised):

- `/admin` → Instances; `/admin/connections` → Credentials; bare Definition → Versions; bare Instance → Overview.
- `I/identity` and `I/identity/profile` → Profile; `I/identity/settings` → Behavior with relocation links; `I/identity/workspace` → Workspace.
- `I/skills/skills` → Skills; `I/skills/resources` → Resources; `I/continuity` → Memory.
- `I/automation` and `I/automation/automations` → Automations; `I/automation/controls` → Automations `#execution-defaults`; `I/automation/harness` → Self-management.
- `I/activity` → Sessions; `I/runs` → Runs; `I/credentials` → Access `#credentials`.
- Exact draft page URLs use the Definition and draft identities together. Unknown/malformed Admin routes remain in Admin with recovery.
- Redirects replace history and retain `session`, `run`, `automation`, `emailAccount`, `event`, `builtin`, `returnTo`, `q` and relevant fragments; unrelated credential-bearing query fields are omitted.
- Archived Automations recover at Profile with an explanation. Native modified/middle-click keeps the source tab and its unsaved draft unchanged.

## Runtime actions and observations

Playwright MCP ran against disposable SQLite Synthetic API on port 5280 and Vite on 5373. It exercised two independent settings drafts, navigation away/back, Conversation-only Save, retained Instructions, owner-departure Stay, draft navigation/save/validation, no-result Search, Escape/opener restoration two sequential setting anchors on the same Models page, keyboard-only mobile destination focus, and a retained pending resource path whose publish blocker returned to Resources. Clearing that pending input re-enabled validation. Expected retained values, separate saved state and exact anchor focus were observed. No provider credentials or live OAuth/mail delivery were used.

Repeatable browser coverage includes every Instance/draft route at 1440px and 390px with reload, Back/Forward; invalid JSON retained through Skills and repaired before Save; native owner-departure Back with Stay/Discard; immutable version inspection and required Synthetic evaluation before publication; explicit dirty-draft deletion without a second discard prompt, and recovery when reopening the deleted URL. Existing workflow tests retain their original API/persisted-state assertions.

## Rendered Impeccable pass

Context was loaded once. One initial batched rendered inspection identified mouse/reload focus outlining and undersized narrow header/navigation controls. Fixes belong to shared shell/token owners: keyboard-only heading focus, 40px narrow navigation/header/close targets, and Search dismissal/destination focus. Drawer screenshots wait for settled bounds. One final confirmation batch was inspected; no additional discretionary polish round was performed.

| Viewport width × 900 | Main width | Document horizontal overflow | Navigation evidence |
| --- | ---: | ---: | --- |
| 1440 | 1192 | 0px | 248px permanent contextual rail |
| 1024 | 776 | 0px | 248px permanent contextual rail |
| 768 | 768 | 0px | Drawer trap and Escape/opener return |
| 767 | 767 | 0px | Drawer trap and Escape/opener return; 40px actions |
| 390 | 390 | 0px | Drawer trap and Escape/opener return; wrapped names |
| 320 | 320 | 0px | Drawer trap and Escape/opener return; wrapped IDs/actions |

Final captures live in [assets/admin-reorganization/final](assets/admin-reorganization/final/behavior-1440.png):

- Behavior: [1440](assets/admin-reorganization/final/behavior-1440.png), [1024](assets/admin-reorganization/final/behavior-1024.png), [768](assets/admin-reorganization/final/behavior-768.png), [767](assets/admin-reorganization/final/behavior-767.png), [390](assets/admin-reorganization/final/behavior-390.png), [320](assets/admin-reorganization/final/behavior-320.png).
- Open navigation: [768](assets/admin-reorganization/final/navigation-768.png), [767](assets/admin-reorganization/final/navigation-767.png), [390](assets/admin-reorganization/final/navigation-390.png), [320](assets/admin-reorganization/final/navigation-320.png).
- Long-name/resource stress (21 uploaded local resources plus two inherited): [1440](assets/admin-reorganization/final/resources-1440.png), [390](assets/admin-reorganization/final/resources-390.png).
- Global privacy: [1440](assets/admin-reorganization/final/privacy-1440.png), [390](assets/admin-reorganization/final/privacy-390.png).
- Failure states: [invalid JSON at 320](assets/admin-reorganization/final/draft-invalid-json-320.png), [malformed Admin ID at 320](assets/admin-reorganization/final/not-found-320.png).

These captures show this implementation, not historical golden images. Initial captures remain separately labeled under `assets/admin-reorganization/initial`. Table overflow is contained locally. Active links use `aria-current`, dirty links retain a stable accessible label with unsaved description, modal/drawer focus stays inside while open, and complete long IDs/names wrap or remain available through existing titles/details. Automated focus tests do not establish screen-reader narration quality or human headset/audible quality.

## Checks and acceptance boundary

Final results and commands are updated after all local runs complete. The initial all-file frontend run passed 116 files / 955 tests with Node 22 and two workers. The subsequent run passed 116 files / 956 tests including the added Session migration regression. A later focused run under concurrent browser load failed eight timeout cases and one creation-button readiness race; the race was corrected and a fresh complete single-worker run is the delivery gate. Node 26's default jsdom storage environment failed setup; Node 22 matches repository CI. A prior over-parallelized local run was stopped after an Events timeout; no passing claim relies on it.

The five Browser privacy tests passed on a separate disposable deployment allowing only `https://example.test` for synthetic unmasked/trusted captures. The six `browser-stt` / `browser-browser` fake-device tests passed. Expected fixture 4xx/5xx recovery and native cancellation are asserted in tests; existing AntD List/Alert migration warnings are not introduced UI failures.

Documentation is synchronized in docs/13 (behavior), DESIGN.md (presentation), schemaVersion 2 design.json, the Admin surface brief, Admin README, docs/11, docs/16, docs/18 and the current demo instructions. Historical reports remain unchanged. Relative links/anchors, balanced Markdown fences, YAML frontmatter, JSON structure/preview parity and `git diff --check` are checked at delivery.

Hosted `synthetic.yml` acceptance is distinct from local build/unit/browser results. No new release milestone, P10/P11 implementation or hosted freeze is claimed. Backend source is unchanged; live-provider, real OAuth delivery and manual microphone/speaker checks are outside this navigation change.
