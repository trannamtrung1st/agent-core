# Accounts, organizations, access and approvals UX

Status: implementation contract, internally reviewed 2026-10-16. This defines the approved target; implementation and verification status live in the [delivery ledger](reports/multitenant-iam-delivery-2026-10-16.md). It does not claim that the previous owner-only application already enforces IAM. The owner-requested proposal at `local/proposals/agent-core-multitenant-iam-approval-final-proposal.md` supersedes the original MVP authentication exclusion.

The owner-prioritized foundation UI now implements sign-in, Account/organization selection, invitation acceptance, member/invitation access, roles, current effective access and audit. [Observed frontend evidence](reports/access-ui-polish-2026-10-16.md) covers these routes only. Agent/resource access, tenant-aware Chat and durable approval journeys below remain requirements for the complete successor.

## Purpose and audience

Operate mode, extending the existing dark Ant Design v6 application. Chat stays primary. A global Account authenticates a person; an organization is a Tenant and a Member is its User. One Account may have different roles in several organizations. An agent executes as its own SystemIdentity Principal. Capability configuration remains separate from IAM.

| Audience | Job | Primary destination |
| --- | --- | --- |
| Platform operator | Create/decommission organizations and manage platform operators | Platform Admin; no implicit tenant-private access |
| Organization Owner | Maintain members, access and governance without losing the last Owner | Organization Admin → Access & Security |
| Member / chat-only employee | Find permitted agents and continue conversations | Chat, scoped to current organization |
| Agent administrator | Configure an agent and inspect its independent authority | Agent Instance → Access |
| Approver | Understand and decide an exact saved action, including from another device | Approvals inbox → detail |
| Invited person | Sign in or register, accept a specific invitation and enter that organization | Invitation acceptance |
| Agent inspector | Explain effective permissions, inheritance and human-only exclusions | Agent Instance → Access → Effective access |

## Routes and navigation ownership

Choose one canonical route scheme: `/sign-in`, `/account`, `/account/organizations`, `/invitations/{id}`, `/system/{page}`, and `/t/{tenantId}/…`. Tenant children retain existing Chat and Admin organization: `/t/{id}/c/{sessionId}`, `/t/{id}/admin/{collection}`, and the existing instance/definition/draft subtrees. No unscoped aliases remain after cutover. The backend mirrors this under `/api/v2/tenants/{tenantId}/…`; `/api/v2/auth`, `/api/v2/account` and `/api/v2/system` remain outside tenant context. The Hub is `/hubs/session`; its handshake and attachment pin the server-validated tenant.

Account controls and organization selection belong to one top-level authenticated shell. Existing `AppShell` retains token propagation and feedback placement. Existing `AdminShell` retains navigation, search, page headings, breadcrumbs, responsive drawer, draft guard and focus restoration. Platform Admin is an explicit destination, never an organization option. My Organizations supports search/pagination rather than rendering hundreds of options in the header. One membership still shows the current organization name.

| Area | Destinations | Visibility / authority |
| --- | --- | --- |
| Account | Profile, My Organizations, invitation acceptance, Sign out | Current Account; no tenant authority implied |
| Platform Admin | Organizations, platform operators, policy ceilings, aggregate health/audit | Explicit system permissions only |
| Organization → Agents | Definitions, Instances | Existing resource-scoped read permissions |
| Organization → Operations | Sessions, Background Runs, Automations, Events | Each collection's read permission |
| Organization → Integrations | Credentials/connections, Resources, Browser/Sandbox configuration | Scoped metadata/configuration permission; secret use separate |
| Organization → Access & Security | Members, Roles & Permissions, Approvals, Audit, Governance | Relevant read permission, mutation separately enforced |
| Agent Instance | Existing pages plus Access | `agents.read`; Access changes require scoped assignment authority |
| Resource detail | Access action with organization/resource breadcrumb | Read effective access for an authorized target; assignment permission to edit |

Navigation hides unavailable destinations, but a deep link still renders an honest denied state. API/service authorization remains authoritative. Lists and counts include only visible records. Back to Chat retains the last authorized Session for this organization only. Agent detail separates **Who can chat?** from **What can this agent do?**

## Layout contract

Existing token roles remain unchanged: compact 8, default 12, section 16; control-inner padding 8. Parent shell owns outer inset, list/form stack owns sibling gap, control owns internal padding. Use `theme.useToken()` and existing collection, configuration, confirmation and feedback compositions. No new generic wrappers, fonts, palette or second UI library.

| Surface | 1440 | 1024 / 768 | 390 / 320 |
| --- | --- | --- | --- |
| Sign in / bootstrap status | Bounded centered form, concise product name, explicit error region | Same bounded form | Single column, full-width controls, wrapping identity; no decorative side panel |
| Account / organizations | Search toolbar, organization list with role/status, create action when entitled | Wrapping toolbar; readable list | Stacked rows; name and membership first, Open action below; pager stays reachable |
| Tenant shell | Organization control and account menu; existing Chat rail/Admin sidebar | Existing 1024 sidebar breakpoint; menu drawer below | Compact organization button opens searchable chooser; wrapping header, no squeezed full-width Select |
| Members | Search/status toolbar; Name, Account, Status, Roles, actions table | Hide redundant secondary columns; row detail for roles | Name/status/action summary; drawer for full detail; table-local scroll only where needed |
| Roles | Named roles with permission count and builtin/custom label; permission editor grouped by resource family | Single editor column, permission group disclosure | Searchable permission checklist; action buttons wrap; no wide permission matrix |
| Assignments / effective access | Principal, role, scope, source; target breadcrumb; effective result | Responsive shared detail grid | Stacked labeled fields; inherited source as text; human-only exclusions explained inline |
| Agent Access | Two labeled sections: chat audience, executing principal authority | Sections stack, shared assignment editor | Same reading order; no operational settings mixed into role assignment |
| Approvals | Filtered collection and navigable detail drawer | Full available-width drawer; inline detail if linked directly | Full-width detail route/drawer, sticky decision footer inside scroll container |
| Approval detail | Type/status, exact action and target, reason, identities, changed fields, expiry; technical disclosure collapsed | Same hierarchy; long values wrap | Summary first, labeled before/after blocks; decision buttons wrap and remain reachable |
| Chat pending/rejected | Compact message status opens shared Approval detail | Same transcript composition | No blocking approval-only Chat state; composer remains usable for independent work |
| Audit / governance | Existing collection layout; typed filter controls | Wrapping filters, responsive details | Stacked event summaries and detail drawer |

All screens support 320, 390, 768, 1024 and 1440 widths, long names, localized dates, empty/loading/denied/error states and bounded pagination. Body `scrollWidth` must not exceed viewport width. Drawers and dialogs contain their own scroll and restore focus to a surviving opener or the page heading.

## Journeys and state transitions

| Journey | User actions | Observable contract / recovery |
| --- | --- | --- |
| First operator | Run explicit CLI bootstrap using operator-supplied secret; then sign in | Bootstrap is disabled after success; no public create-admin route, no credentials echoed |
| Sign in | Enter login/password, submit, resolve `/account/me` | HttpOnly same-origin cookie; generic invalid-credentials copy; rate limits; no tokens in browser storage |
| No organization | Open My Organizations; accept valid invite or create if entitled | Empty state explains next step; no empty tenant shell or implicit default membership |
| Invitation | Open invitation, sign in/register, verify matching Account and accept | Invitation bound to tenant/email/expiry; expired/revoked invitation shows contact-owner recovery; never changes another tenant |
| Create organization | Enter name/slug, confirm | Atomic creation with this human as Owner; success opens new organization |
| Switch organization | Pick an authorized organization | Unmount previous tenant UI, close socket/audio, abort requests, clear scoped state, then fetch target; stale responses discarded; durable runs continue |
| Invite member | Pick email and initial role/scope; inspect permission summary, submit | Saved invitation status; explicit copyable invite link; no implied email delivery |
| Suspend/remove member | Inspect identity and tenant, confirm | Last active Owner cannot be removed/suspended; stale revision reloads; no false success |
| Create/edit role | Name, select catalog permissions by family, inspect summary, save | Builtins read-only; human-only labels; unknown keys rejected; drafts retained on failure |
| Assign/revoke role | Select principal, role and tenant/resource scope; preview source/effective changes, confirm | Changes are effective on next protected operation; assignment cannot cross tenants; last Owner preserved atomically |
| Grant chat access | Agent Access → Who can chat → assign scoped role | Read/list access alone does not permit Chat; denied direct send/attach remains denied |
| Inspect agent authority | Agent Access → What can this agent do? → effective access | Shows own SystemIdentity, direct/inherited roles, scope and excluded human-only permissions |
| Ordinary review | Agent prepares exact action; Core requires review; authorized different human opens inbox and approves | Label **Approve action**; approver needs review plus underlying action authority; requester's roles never become agent permissions |
| Exceptional grant | Core classifies eligible typed action; agent explicitly requests once; qualifying human reviews | Label **Authorize this exact action once**; clear statement that no role or future permission is granted; self-authorization only when independent review is not required |
| Asynchronous decision | Close Chat/log out/restart; another authorized device decides | Durable status and decision survive; continuation resumes exact operation once and reports into original Session history |
| Reject/expire/stale | Reject with optional reason, wait for expiry, or race another decision | Stable terminal reason; same root cannot request identical action again; a new independent message may retry |
| Revocation mid-run | Remove membership/role or mutate target before effect | Next protected effect fails fresh authorization/witness check; pending UI updates; no saved-token bypass |
| Sign out | Account menu → Sign out | Revoke server session, close realtime/audio, clear private state before sign-in appears; no previous-tenant flash |

Other independent work remains usable while an approval waits. Pending requests do not hold an active model stream or a live Task as the authority. Linkable Approval detail and Chat use one shared saved-action preview.

## Interaction and copy

Members collection supports explicit status and search filters, bounded pages and one row-details destination. Principal picker searches only this tenant's active users and agent identities; role picker marks builtins and human-only roles. Scope defaults visibly to the current resource, never silently to the organization. Effective access shows **Direct** or **Inherited from {organization}** and the exact scope. Editing roles/assignments requires current permission and a concurrency revision.

Role drafts use the existing unsaved-navigation guard. Destructive lifecycle/access changes use `confirmAction` with organization, principal and consequence. Approval decisions use the same confirmation composition and saved action metadata, never client-supplied replacement arguments. Repeated submit is disabled while pending and protected by CAS on the server; stale decisions reload the final result.

Required copy:

- **Chat access lets people reach this agent. The agent uses its instructions and judgment when responding.**
- **These roles control what the agent can do. They are separate from the requester's roles and the agent's enabled capabilities.**
- Review: **The agent already has permission. Your decision reviews this action only.**
- Exceptional grant: **This agent lacks permission. Authorizing once permits only the action shown, before it expires. It does not assign a role.**
- Human-only exclusion: **Only an authorized person can perform this action. An agent cannot receive this permission through approval.**

Do not show raw JSON, hashes, internal physical paths, secrets or opaque principal IDs by default. A collapsed technical disclosure may show safe operation/revision identifiers for support. Dates use viewer locale with explicit relevant zone/expiry. Risk and state have text plus icon, never color alone.

| Failure | Presentation | Recovery |
| --- | --- | --- |
| 401 `not_authenticated` | Session expired; clear private view | Sign in; retain only safe intended route |
| 403 `not_member` | Organization unavailable; no private name/count leak | My Organizations |
| 403 `permission_denied` | Explain missing operation permission safely | Back / contact an organization Owner; no misleading retry CTA |
| 404 | Not found or unavailable | Return to scoped collection |
| 409 `stale_decision` | Another decision or resource update won | Reload saved current state; do not resubmit silently |
| 409 `resource_changed` | Action no longer matches approved target | Show terminal stale action and start a new independently prepared request |
| 422 `human_only` / ineligible | Action cannot be authorized for an agent | Authorized human performs it through ordinary UI |
| 503 | Could not load/save; no success message | Preserve draft, local Retry with request cancellation |
| Rejected / expired / cancelled | Named terminal status with reason and history | New independent request if still needed; no same-root approval loop |

Loading replaces prior tenant data immediately with a neutral scoped skeleton. Unknown authority never renders editable success controls. Read-only details explain why editing is unavailable instead of leaving unexplained disabled buttons. Hundreds of records use server pagination; no client-side hiding of unauthorized rows.

## Component ownership

| Composition | Existing owner / justified extension |
| --- | --- |
| Dark tokens, global feedback and outer sizing | `AppShell`, `antdTheme.ts`, shared `app.css` |
| Account and organization lifetime | Authenticated application boundary; auth service owns cookie/CSRF and tenant teardown |
| Admin page geometry/search/draft focus | Existing `AdminShell`, `AdminCollectionLayout`, configuration compositions |
| Organization picker | New `TenantSwitcher`, shared by Chat/Admin top-level shell |
| Member/agent assignment | Shared `AccessControlPanel` and `PrincipalPicker`, used by tenant, agent and resource access |
| Exact saved action | Shared `ApprovalDetail`, reusing `AutomationApprovalPreview` and existing safe action summaries |
| Confirmations/errors/empty states | Existing `confirmAction`, form feedback, AntD Alert/Empty/Skeleton |
| Chat/HTTP/SignalR state | Existing service owners extended with trusted tenant context and generation guards; components never own sockets/audio |

Extract a composition only when its second real consumer exists. Use AntD primitives directly, keep field command state with its feature and give each inset/gap one owner.

## Security and self-review gate

Reviewed against current `AppShell`, `AdminShell`, `adminNavigation`, `ApprovalModal`, theme, frontend skill and Impeccable context. Navigation permission, agent capability, requester judgment and execution authority have separate explanations and server checks. System/tenant scopes cannot be confused in organization selection. Ordinary approval requires an independent human; exceptional own-request authorization is explicitly labeled and remains subject to independent-review policy. Binary previews/downloads check recipient access. OAuth callbacks bind initiating account/session/tenant and current authority. Sensitive free-text disclosure by a powerful agent remains a residual risk: restrict its chat audience and external credentials; no universal semantic disclosure filter is claimed.

The design gate is internally coherent and ready for implementation. This self-review is not browser verification. Final acceptance requires actual interactions, computed overflow/focus checks and bounded Impeccable audit → grouped corrections → confirmation.

## UI acceptance matrix

Run each journey above through the real Synthetic UI with disposable data. Required fixtures: one Account owning A and Reader in B; a second authorized approver; a chat-only member; two agent identities with distinct authority; resource-scoped assignment; last Owner; revoked member; no-tenant Account; stale/expired invite; Review and ExceptionalGrant with safe exact action; pending/rejected/expired/stale decisions; long names and paginated collections. Verify tenant switch/logout during in-flight HTTP and connected Chat discard old payloads.

At 320/390/768/1024/1440 check all major screens, document overflow, drawer containment and action reachability. Keyboard-check sign-in, organization selection, invite/assignment forms, approval decision, Escape/focus restoration and read-only recovery. Inject 401/403/404/409/503 and confirm correct copy, retained drafts and no fake success. Inspect console/request failures. Screenshot evidence complements functional assertions; it does not establish authorization or durable continuation.
