# Tenant Instance Definition version UI verification — 2026-10-18

The canonical tenant Instance editor now supports exact-source Definition version paging, explicit non-mutating preview and confirmed adoption. This advances the supported Phase 7 page; it does not accept a milestone or the full multi-tenant proposal. Skills/resources UI, Sandbox and other resource/effect families, voice/rich delivery, autonomous origins and obsolete-authority/schema consolidation remain open. The earlier SQLite direct-preference reliability follow-up is unchanged.

## Implementation and ownership

`/t/{tenantId}/admin/agents/{instanceId}/version` uses the existing original-human catalog/preview reads and atomic adoption command. Ten-item pages contain actual publications from the Instance's exact source. Current and Deprecated versions are disabled; no numeric/free-form target, implicit latest, owner-capability fallback or global administrative lookup is used by this tenant page. Owned Definition read permission remains independent of Instance read/update permission.

`TenantAgentHeading`, `AgentConfigurationPanel` and native Ant Design controls retain the incumbent hierarchy and bounded layout. `InstanceVersionChanges` shares change groups and readable field labels with the existing administrative confirmation. No CSS, palette, typography, theme token, schema or UI kit changed. The enclosing bounded form owns outer section gaps, panels own insets, and native rows own wrapping. Protected child keys are filtered by the backend before presentation; the partial-access notice does not claim hidden names or counts.

Preview must match the catalog's Instance/security witnesses and exact selected target. Selection, paging, reload, attempted mutation and scope retirement invalidate the old review. A stale command retains the target for deliberate Reload and new Preview. A confirmed successful acknowledgement retires the submitted choice even when response parsing or the following read fails; recovery reads cannot replay adoption. Original-human and atomic write fences remain server owned.

The native confirmation identifies the agent, organization and current/target versions. Cancel and Escape return focus to the enabled Adopt opener. A submitted attempt retires that opener and returns focus to Reload. Preview has a stable accessible name while its native loading icon changes. Scoped cancellation and epoch checks prevent a late preview/confirmation from acting in a new Instance. Read-only users can inspect a permitted source and preview; archived Instances block selection/preview, and definitive read revocation removes protected content.

## Executed focused local checks

| Check | Result |
| --- | --- |
| `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantAgentVersion.test.tsx src/features/access/accessRoute.test.ts` | 15 passed: nine component cases and six canonical route cases |
| `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/admin/AdminApp.test.tsx -t 'applies a version while retaining dirty persona JSON'` | One passed; 43 unrelated cases intentionally skipped |
| `pnpm run build` | TypeScript and Vite passed; existing large-chunk advisory |
| `CI=1 TENANT_CHAT_API_PORT=61303 TENANT_CHAT_WEB_PORT=61304 pnpm run test:e2e:tenant-instance-version` | Two passed together on a fresh isolated Synthetic host, final run 14.3 seconds |

These are 18 distinct frontend checks; diagnostic reruns are not additional cases. Commands ran from `web/`. The local runner's `CI=1` prevents server reuse; no hosted CI or full CI suite was requested or run. The preceding [backend catalog/preview report](tenant-version-preview-verification-2026-10-18.md) records 100 focused backend cases separately; they were not rerun for this frontend-only slice.

Component coverage includes exact reviewed payload/witnesses, preview non-mutation, stale retry with fresh witnesses, mixed preview/catalog rejection, malformed successful acknowledgement followed by read failure without replay, read-only/partial access, definitive read loss, transient preview retry, late selection/scope results, abort signals and disabled current/Deprecated targets with paging invalidation. The existing administrative consumer regression verifies that sharing change presentation preserves its dirty persona draft during adoption.

The first browser case publishes twelve owned source versions, pages at 320px, chooses v2, attempts guarded navigation and keeps editing, previews without mutation, cancels with Escape and verifies focus, then changes saved settings from another authenticated context. Adoption with the old preview fails stale; the old review disappears and focus moves to Reload. Reload plus a new explicit preview/adoption saves v2 while retaining the separately saved language `de`. It asserts no document horizontal overflow, uncaught page errors or owner-capability/global-admin requests.

The second browser case grants only minimal membership and exact Instance read. Source read initially fails. An independent exact Definition grant allows read-only preview with disabled adoption and a partial-details notice, without browser IAM-inspector collection/check requests. Revoking that source grant and requesting another preview removes selection and adoption controls. Fixture setup uses separate authenticated public API contexts; their necessary role-management calls are distinct from the inspected browser traffic.

Initial test corrections are retained as diagnosis, not passing evidence: the shared JSDOM setup stubs `useId` to a constant, so the component test now locates the actual confirmation from its action rather than a duplicate accessible-name ID. An early combined browser run timed out on the second case and also produced a secondary trace archive error. Action-level diagnostics identified the Preview button's changing accessible name while loading. The stable `aria-label` correction passed both scenarios together, including the final strengthened request-boundary assertions. Actual browser Cancel focus initially fell to the document body because the opener was disabled during confirmation; retaining the guarded enabled opener corrected that behavior.

## Playwright MCP observed journeys

A task-owned fresh InMemory Synthetic API on 61301 and Vite on 61302 used disposable roots, a separate organization and an owned Definition. Visible browser interactions exercised:

1. Choose v2, Preview, inspect changed instructions/management and preserved persona/settings, and confirm that preview did not apply a version.
2. Change saved settings independently, attempt the reviewed adoption and observe stale rejection with the target retained and old preview retired.
3. Reload, explicitly preview again and confirm exact v2 adoption; read back v2 and the retained persona/language `de`.
4. Preview v3 containing a long inherited Skill key; inspect readable wrapping and the shared change groups.
5. Cancel the native confirmation and verify opener focus after the correction. Inspect an archived Instance, then restore it through the canonical API.
6. Sign in as an exact read-only member, inspect permitted source/preview with disabled adoption and filtered child details, revoke source read from an independently authenticated owner context, then observe denied content after a fresh read.
7. Inspect the existing administrative v21→v22 confirmation at wide/narrow widths and cancel it, verifying the shared presentation without adopting through that legacy fixture.

The canonical journey used no provider calls or execution Run. The separate legacy-consumer fixture used its existing local owner-capability setup only to inspect that pre-existing surface; it is not a fallback in canonical tenant code. Removal of that obsolete authority remains a full-proposal obligation. Console/network inspection included the expected denied-source 403 and normal request-abort entries; it does not claim a zero-error console. The automated owner case separately asserts zero uncaught page errors.

## Bounded visual review and design persistence

One batched visual round produced 19 local PNGs under `.playwright-mcp/tenant-version-ui-20261018/`, all opened by the parent and fresh finish reviewer:

- `version-preview-{1440,1024,768,390,320}-{top,lower}.png` (ten images).
- `adoption-confirm-{1440,320}.png` and `legacy-preview-{1440,320}.png` (four images).
- `version-archived-320.png`, `version-readonly-1440-top.png`, `version-readonly-320-top.png`, `version-readonly-320-lower.png` and `version-source-denied-320.png` (five images).

Measured section gap is 16px. The shared 48rem bound yields panel widths 768/744/736/366/296px at the five respective viewport widths. Document horizontal containment holds at all five. Long inherited keys wrap through the existing owning class, actions wrap, and the 320px confirmation/legacy preview remain reachable. Top/lower images document the intentional internal main scroll; they are not full-page capture claims.

The fresh review found no material presentation correction and reached the incumbent Operate craft ceiling. Its persistence finding is resolved by the bounded DESIGN/PRODUCT/sidecar sync and canonical implementation-status update. Existing tokens, visual identity and historical provenance are preserved. Loading, transient-error, malformed-acknowledgement and stale presentations were not visually accepted in this batch; their functional tests do not substitute for visual acceptance. No real-device, audible voice, Real-provider, full-proposal or milestone acceptance is claimed.
