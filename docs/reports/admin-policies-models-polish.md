# Admin Policies & models polish

The Automation tab now separates Harness management, Execution defaults and Advanced registrations through the existing Admin panel composition. Authoring and unattended-model forms share a 48rem maximum width, visible model/reasoning labels and token spacing. Save authoring policy and Freeze self-management share a content-width wrapping row; advanced registration review belongs in its section header. Evidence and registration tables retain the available width. Mobile actions retain the shared 40px minimum height.

Interactive review also found an unattended-model form retaining an old owner revision after a policy update. It now adopts the refreshed configuration revision without discarding the model draft; a regression test covers that case.

Local verification:

- Initial Harness, model and Schedule suites: 29 passed. After the revision correction, Harness/model suites: 15 passed, including the new regression; the unchanged Schedule suite contributed 15 passes to the initial run.
- Production build passed after the final correction (3.96s), with existing dependency and chunk warnings.
- Playwright MCP checked 1440/768/390px: form widths 768/702/332px, wrapping compact Save/Freeze actions, no document overflow, and 32px desktop/tablet / 40px mobile action heights. Normal policy save and the following model save return 200. An injected model-save 503 remains inline and retry returns 200. Advanced registrations load; cancelling Freeze preserves the enabled policy. Stale policy rejection remains safe and recovers through its existing refresh path. Expected HTTP errors were inspected; no unexpected JavaScript errors were found.
- The existing Synthetic `Freeze blocks durable Chat learning while normal Chat still works` browser journey passed (36.9s).

Logs: `/private/tmp/p910-policies-{unit,unit-final,build-verified,e2e}.log`. Final viewport screenshots: `/private/tmp/p910-policies-final-{1440,768,390}.png`.

Workflow-run checks were omitted at the operator's request. This local UI verification does not close the proposal's hosted acceptance gate.

## Shared field widths and leading messages

The leading Advanced registrations message had 16px panel padding plus a generic 12px secondary-text margin: 28px above versus 16px below. The shared rule now adds that margin only when secondary text follows another direct child. Leading messages use equal 16px insets.

Record-editing forms for policy/model, Schedule, Thought, Experience checkpoint, Event source and application connection share `admin-config-form`, a 48rem maximum with full available width on smaller screens. Selects fill their assigned field column. Draft/instance grids intentionally retain multiple columns; each select fills its column. Short timing and unit controls, Memory scope filters, base-version toolbar choices, pagination and bounded table model editors retain compact widths appropriate to their roles. Event subscription source selection flexes with its action row, caps at the same form measure and wraps on mobile instead of sizing to label content.

Verification: six affected unit files passed 52 tests; production build passed (6.85s). Five Synthetic/P7.6 browser regressions passed (45.3s), covering read recovery/clipboard, local-time schedule save, sticky desktop/mobile draft actions, memory/automation and definition/managed Chat. MCP measured the leading message at equal 16px insets at 1440/768/390px. Model and Thought execution selects fill columns of 768/702/332px; the Thought unit control deliberately stays 128px. Weekly schedule creation succeeded, empty-weekday submission remained disabled, Memory loading worked with a compact 192px scope selector, and a long Event source name saved and remained readable in its subscription selector without document overflow. This fixture's schedule-only policy rejected subscribing with 400 and a clear inline message; Retry subscriptions recovered. No application connection sign-in was attempted. Layout and existing connection behavior are covered by the focused suite; real authentication remains outside this presentation change.

Evidence: `/private/tmp/admin-field-width-{unit,build,e2e}.log` and `/private/tmp/admin-field-width-{policies,schedules,thoughts,connections}-{1440,768,390}.png`. Hosted workflow checks remain omitted as requested.

## Follow-up consistency review

The next review reproduced two remaining defects. Unattended model's Effective source reflected a draft selection before saving, including after rejection. It now uses saved configuration and changes only after a successful save or refreshed configuration; the draft remains editable. A regression test requires unsaved/rejected selection to leave effective source unchanged. Schedule/Thought wrapping rows also combined a 16px Flex gap with 16px Form.Item bottom margins, producing 32px between wrapped fields. The shared `admin-form-row` now owns the 16px row gap and external separation, with zero child bottom margins. Reasoning selectors always expose their visible associated label, including Schedule and Thought consumers.

Playwright MCP reproduced the incorrect source before correction, then verified draft selection and an injected 503 preserve the saved source; retry returned 200 and changed the source. Desktop/tablet/mobile inspections at 1440/768/390px retained bounded field widths without document overflow. The wrapped Thought row changed from a 32px separation to 16px. A real Synthetic Thought with Scripted Alpha and low reasoning saved with 200 and appeared in the table; its reasoning label correctly associates with the control. The only browser error in this flow was the deliberately injected 503. Verification logs: `/private/tmp/admin-consistency-review-{unit,build,e2e}.log`; screenshots: `/private/tmp/admin-consistency-review-thoughts-{1440,768,390}.png`. Hosted workflow checks remain omitted.

Final checks for this review: four focused Admin unit files passed all 47 tests, including the saved-source regression; production build passed (7.85s) with existing warnings; the five Synthetic/P7.6 browser regressions passed (46.7s). Documentation link checks passed with zero problems.
