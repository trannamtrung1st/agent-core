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
