# Organization Sandbox catalog UI verification — 2026-10-18

Status: focused Phase 7 implementation over `ec791278a132965e279ec928813fa0e0d610f46c`. The full multi-tenant IAM proposal remains active. This supplies organization profile catalog UI; Instance Sandbox settings/verification, Session controls UI and other proposal families remain open.

## Implemented scope

Organization Integrations adds `/t/{tenantId}/integrations/sandboxes`, with addressable `profileId` inspection. The existing protected tenant endpoints own filtered search/paging/totals, create/manage hints, original-human authority, exact immutable revisions, source resolution, verification and cleanup. This change adds no backend route, migration, capability, grant or executor. The [interaction contract](../19-multi-tenant-iam-ux.md#organization-sandbox-catalog-interaction-contract) was written and internally checked before components; [Frontend](../13-frontend-implementation-spec.md#organization-sandbox-profile-catalog) owns final behavior.

The collection shows full readable names, revision, enabled intent and named availability. Current/historical inspection reuses shared Sandbox facts and bounded escaped evidence. Exact revision input avoids allocating an option per retained revision. Historical configuration is read-only. Native guarded authoring uses `SandboxProfileFields`, also extracted into the existing platform catalog. Each consumer retains its own transport, identity and security/source bounds. The shared profile label now says Profile initialization rather than incorrectly claiming Global ownership.

Tenant credentials are readable same-organization metadata with explicit paging/reload. An unavailable saved binding is preserved through unrelated edits; save independently enforces Credential read/bind/use. Host settings and protected values are absent. Profile authoring shows domain bounds honestly with possible lower host ceilings. Saved configuration, image-only readiness, full verification, pending cleanup and execution permission remain distinct.

Save uses the exact security/profile witnesses from the read. Stale responses preserve touched draft fields, block another submission and require explicit Reload latest profile; untouched values follow the fresh read. An acknowledged save retires the draft even when its response cannot be parsed; subsequent recovery reads cannot replay it. Exact read loss removes protected detail/editor. Native draft/navigation guards and mounted tenant command cancellation remain the owners. An observed editor-close focus defect was fixed; close now returns focus to Edit profile.

## Execution and results

| Focused check | Result |
| --- | --- |
| Tenant catalog interaction regressions | 8 passed |
| Existing platform catalog/shared authoring regressions | 15 passed |
| Access route/deep-link regressions | 7 passed |
| Existing Sandbox transport regressions | 14 passed |
| Tenant catalog/verification HTTP integration | 4 passed |
| Frontend TypeScript/Vite build | Passed; existing SignalR annotation/chunk-size notices |

Commands:

```sh
pnpm --dir web exec vitest run src/features/access/TenantSandboxes.test.tsx src/features/access/accessRoute.test.ts src/features/admin/SandboxProfilesSection.test.tsx src/services/sandboxes.test.ts
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Sandbox_catalog_HTTP|FullyQualifiedName~Tenant_verification_HTTP' -v minimal
pnpm --dir web run build
```

The initial combined frontend selection passed 42/43, then the new exact-history case and focused reruns completed all 44 cases across these selections. A new native-modal test initially used a singular title query; Ant Design renders duplicate hidden title nodes in JSDOM. It now uses the existing Approvals test pattern (`findAllByText` then closest dialog), while real browser confirmation was independently exercised. A build caught Playwright-only `exact` options copied into Testing Library role queries; those options were removed. These fixture/type failures were not treated as passing checks. The inherited JSDOM textarea-height warning remains; no browser layout warning was observed.

The four real HTTP cases exercise saved revisions/history, anonymous and legacy rejection on tenant routes, foreign/global masking, profile-scoped grants without sibling enumeration/create authority, revocation before stale witnesses, and disabled-host verification without compatibility promotion or internal proof disclosure.

## Browser proof and boundaries

Playwright MCP used a new isolated Synthetic API on 5098 and Vite on 5188, with files/keys under `/private/tmp/agent-core-catalog-ui-JnGswL`. Health was checked for Synthetic before sign-in. No user database, existing host or live provider was used.

Actual visible UI against the real tenant backend:

- Signed in, opened the empty organization catalog, added a long-named prebuilt image and inspected server-assigned profile/revision 1 and Needs verification.
- Edited a description, then independently disabled the same profile before submitting the stale draft. The real PUT409 blocked saving. Explicit Reload preserved the deliberate description, adopted untouched Disabled state and fresh witnesses; explicit save committed revision 3.
- Read immutable revision 1 and returned to Current. Historical inspection exposed no edit action. The final exact revision input was also exercised after replacing an unbounded history option list.
- Routed one real successful PUT through a malformed response adapter. The actual backend saved revision 4; the editor retired and the inspector recovered the saved description. Explicit Reload produced no second PUT.
- Real server search reached a matching-empty state and Clear search restored the catalog. A controlled503 list response showed local recovery; explicit Reload recovered the actual inventory. Detail close from a deep link focused the collection fallback.
- Re-enabled through the real editor, producing revision 5. Keyboard Escape opened the dirty guard, Keep editing retained the draft, Discard adopted saved values, and Cancel editing restored focus to Edit profile after the fix.

Controlled browser adapters, separate from backend execution evidence:

- Verification POST asserted exact saved security/profile revisions and returned retained CleanupPending evidence. The native confirmation named organization/revision and disposable setup consequences. Explicit reload left one POST and disabled another attempt. Inert script-like output rendered as text. No SDK command or initialization was dispatched by this browser fixture.
- Read-only projection removed Edit/Verify actions while preserving inspection. Exact GET403 removed protected detail and retained recovery/close controls; subsequent permitted read recovered explicitly.
- The existing platform catalog used a controlled GET to render the same shared field owner at desktop/mobile widths. This was presentation reuse evidence, not acceptance of the remaining legacy owner authority or execution path.

Only intentional stale409, exact-read403 and transient-list503 appeared as console errors. Expected aborted initial StrictMode GETs had successful replacement reads. The first browser history script incorrectly searched Ant Design's virtual options by role; the visible option selection completed, and the final native revision input was exercised separately. No application exception or unexpected request failure was observed in the completed scenarios.

## Design and finish review

The incumbent dark Ant Design v6 system was retained. `AdminShell` owns page inset/context, the collection owns 16px section gaps, and native Drawers own the 24px body inset and viewport-capped 736px width. Shared fields, details, evidence, footer, confirmation, feedback and draft guards retain their owners. No CSS, token, raster or second UI kit was added.

At 320/390/768/1024/1440px the collection document width matched the viewport. Inspector/editor client and scroll widths matched `min(viewport,736px)` at all five widths. Long profile names, immutable images, descriptions and footers wrapped without horizontal overflow. Captures are local disposable evidence under the run root: `catalog-*`, `inspector-*`, `editor-*`, `editor-limits-*`, `editor-middle-320`, `verification-*`, `verification-final-320`, `read-only-320`, `read-revoked-320`, and `shared-platform-*` PNGs.

A fresh Impeccable finish reviewer inspected 29 captures and returned **ship** for the inspected scope, with no required fixes or further recapture. The final mobile verification capture exposed saved Cleanup pending, blocked repeat actions and expanded inert evidence. Corrected focus return relies on the separately reported runtime interaction. Dedicated visual acceptance does not cover loading, empty/search-empty, transient, stale, malformed acknowledgement, history or confirmation states. Shared platform lower fields and mobile CPU/memory rows remain visually unverified: the attempted upper-limit capture repeated registry/Enabled content. Functional tests cover the shared limits; screenshots do not establish those omitted visual states.

The ordinary extension preserves existing theme/tokens and historical `.impeccable/design.json` fingerprints. The focused tooling-context merge records current patterns/evidence without claiming a regenerated sidecar or complete design-system/full-proposal acceptance.

## Remaining work

Instance Sandbox configuration/verification and Session profile switching/reset/process UI are not supplied by this catalog. Other resource Access/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final obsolete-authority/schema removal and the earlier SQLite preference reliability follow-up remain open. No full proposal/milestone acceptance is claimed.

No hosted CI/full local CI suite, live OAuth/provider or Docker qualification, deployment, user-data migration/reset or Real-host restart was performed. Live registry authentication and actual successful SDK setup from this new UI remain unverified; deterministic HTTP/store/SDK backend evidence retains its existing report scope.

Changed Markdown passed added local link/fragment checks and fence balance; whitespace validation passed. The disposable API/Vite sessions were stopped and the browser tab closed. Captures and focused test/build logs remain under the temporary run paths.
