# Accounts, organizations, access and approvals UX

The [Instance execution-controls checkpoint](reports/tenant-instance-controls-ui-verification-2026-10-18.md) supplies canonical budgets, saved management policy/lock and unattended-model preferences using the interaction contract below. It supersedes budgets/management-pending statements only for these supported pages. The [version-review successor](reports/tenant-instance-version-ui-verification-2026-10-18.md) supplies canonical preview/adoption. The [Skills successor](reports/tenant-instance-skills-ui-verification-2026-10-18.md) supplies scoped procedure management. Resources and the remaining full proposal stay open.

The [tenant text Chat UI checkpoint](reports/tenant-chat-ui-verification-2026-10-17.md) now implements the scoped Chat entry, organization switcher, shared rail/transcript/plain composer, public agent selection, saved history, local queue/Stop, reconnect and protected-content retirement described below. Public action hints govern visible management controls and every action independently reauthorizes. The existing dark Ant Design presentation remains; mobile short organization labels remain readable in the wrapping header. Voice/rich-resource Chat, approval links/notifications and complete final cutover remain pending.

The [public Chat discovery prerequisite](reports/tenant-chat-discovery-verification-2026-10-17.md) now provides scoped active-agent search/paging and Session-creation revision hints independently of administrative read access. Backend HTTP/MessagePack evidence covers a Chat-only member's actual conversation and revocation. That prerequisite did not enable the frontend; the text-Chat successor above covers the scoped shell/rail/connection integration.

Status: implementation contract, internally reviewed 2026-10-16. This defines the approved target; implementation and verification status live in the [delivery ledger](reports/multitenant-iam-delivery-2026-10-16.md). It does not claim that the previous owner-only application already enforces IAM. The owner-requested proposal at `local/proposals/agent-core-multitenant-iam-approval-final-proposal.md` supersedes the original MVP authentication exclusion.

The owner-prioritized foundation UI now implements sign-in, Account/organization selection, invitation acceptance, member/invitation access, roles, current effective access and audit. [Observed frontend evidence](reports/access-ui-polish-2026-10-16.md) covers these routes only. Agent/resource access, tenant-aware Chat and durable approval journeys below remain requirements for the complete successor.

The [canonical Approvals UI slice](reports/tenant-approval-ui-verification-2026-10-17.md) now implements the paged inbox and linkable exact persona/preference inspector over the existing durable worker. Qualified approve/authorize-once, reject/cancel, current read-only reasons and stale/read-failure recovery have Synthetic browser evidence. Approval and effect result remain separate. Statements in earlier reports retain their original implementation scope; original Chat integration, notifications and the full successor journeys remain pending.

## Purpose and audience

The [approved persona effect verification](reports/tenant-approved-persona-effect-verification-2026-10-17.md) supplies backend decision attribution and an operation result for the original Run, including precise denied outcomes after an earlier human approval. The inbox and eventual Chat must show consent and execution result separately, retain exact target/witness details and offer fresh planning rather than adapting a failed old grant. That earlier backend report did not claim approval UI/browser acceptance; the bounded UI successor above supplies its own evidence.

The [durable approval verification](reports/tenant-durable-approval-verification-2026-10-17.md) now supplies backend typed persona request/read/list/decision/cancel and durable expiry/wakeup evidence. The bounded inbox/inspector successor above supplies exact local action review. Original Chat integration, safe Chat return navigation and the complete browser/visual journeys below remain required. Approval success must not be presented as a completed operation without an operation receipt; the internal typed persona successor now supplies that result, while the complete runtime/UI cutover remains pending.

The subsequent [tenant Definition UI slice](reports/tenant-definition-ui-verification-2026-10-16.md) implements canonical owned inventories, drafts, resources/evaluations and publication lifecycle. Exact-resource access controls its actions and supports resource-only readers. This adds observed authoring evidence to the foundation above; complete resource/Agent Access panels, Sandbox catalog, tenant-aware Chat/runtime and durable approval journeys remain pending.

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

Definition file permissions now have [focused implementation/browser evidence](reports/tenant-definition-resource-iam-verification-2026-10-17.md). Settings read-only, per-file edit/delete, limited collections and copy-destination authority are separate visible states. Source-template choices and publication forks use current server eligibility; a failed write retains edits until explicit reload/discard. This is resource-authoring progress, not full tenant runtime or approval acceptance.

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

### Original Chat approval status and review

The original conversation shows compact durable action statuses beside its history, without opening a blocking approval modal. Current Session-read and exact agent-Chat access permit only request identity, generic operation/type, status, expiry and a closed execution-result label. They do not expose prepared arguments, resource references, names from protected resources, action hashes or grant witnesses. **Review details** is available only when the existing approval-read plus underlying action checks permit the full saved preview; otherwise explain that a qualified person must review. The shared approval inspector retains its independent current read/decision checks. It may open from Chat without changing the conversation route, with Close/Escape returning focus to the opener; closing it never submits a decision. **Open original conversation** from the inbox rechecks conversation access independently.

A durable wait releases the busy response indicator and local queue without marking the response completed, heard or interrupted. Independent new messages remain usable. Approval status and operation result remain separate: approved with no receipt says execution is pending/unrecorded; a completed check says Check completed, a completed mutation says Applied, definite denial says Not applied and uncertain external outcome says Outcome uncertain. A minimal tenant event announces a saved wait; scoped reads restore current status after reload, disconnect or a decision on another device. Refresh never replays an action. Failed refresh retains last known safe status with explicit stale/retry copy; authority loss clears protected state. The organization header links to the existing filtered inbox, with a current authorized pending count rather than repeated popup notifications. Polling pauses while hidden and on retirement. All of this extends the existing Ant Design shell, compact rows, shared inspector and 8/12/16 spacing owners at the documented widths.

The supported original Chat status/inspector/inbox-attention slice now has [focused Synthetic verification](reports/tenant-chat-approvals-verification-2026-10-17.md). Broader proposal and milestone acceptance remain separate.

### Agent inventory and Access interaction contract

Organization → Agents → Instances uses the existing authorized paged inventory, with search explicitly limited to the loaded page. Each readable agent opens `/t/{tenantId}/admin/agents/{instanceId}/access`. Creation copies an exact available shipped template using fresh security revisions; archive/restore confirms organization and agent, retains identity/history and uses exact Instance/security witnesses. Readers can inspect without lifecycle controls. Chat-only membership does not confer administrative inventory/detail access.

Access has two labeled sections in the same bounded reading column. **Who can chat?** lists applicable direct and inherited chat-bearing role assignments with readable role/principal labels and active/inactive access state; inherited organization roles are identified and changed only by an administrator authorized at their actual source. Assign chat access selects a current eligible tenant principal and a chat-bearing role, confirms this exact agent/organization and writes only an agent-resource assignment. Scoped administrators need no tenant-wide Members inventory. Principal search/paging is server bounded and exposes no login email, credential or unrelated role inventory.

**What can this agent do?** identifies its independent SystemIdentity, lists visible execution-role assignments with their actual scopes and explains that human-only permissions remain unavailable. An explicit scope selector distinguishes **This agent** from **Organization**, available only when the viewer can inspect that scope. Effective permission results and matching assignment references come from Core, never from unioning UI role labels; capability eligibility and approval policy remain independent. Granting organization execution authority requires current organization-scope assignment authority and an agent-eligible role. Resource grants retain their actual scope; this page does not widen them to organization authority. Revoke checks its source scope independently. Other resource access panels remain a separate implementation obligation.

At 1440/1024/768px, readable rows, scoped role controls and permission disclosures reuse the existing admin collection/detail layouts; at 390/320px, rows and controls stack/wrap without document overflow. Technical UUIDs stay collapsed. Use the existing shared confirmations, command guard, scoped-query aborts, native status tags and Ant Design drawers; the group owns 8/12/16px spacing. Loading, no roles/no matching principals, denied, stale revision, transient read failure and acknowledged-write refresh failure have explicit states. Failed writes retain selections; successful writes clear them and reload current data without automatic mutation retry. Closing a creation draft asks before discarding; closing or navigating returns focus through the existing shell/drawer owners. No new theme, tokens or UI kit is introduced.

### Instance Profile and Settings interaction contract

Scoped Instance navigation retains Profile, Settings, Access and Connections without changing organization/Instance ownership. Profile and Settings use `/t/{tenantId}/admin/agents/{instanceId}/profile` and `/settings`; they require exact agent read, with edits independently gated by exact agent update and an active Instance. A read-only manager needs no IAM-inspection grant. A single human/session-validated editor read supplies matching persona, Instance/security revisions, section defaults/overrides/sources and current update affordance. Changed configuration/security during the read rejects the projection rather than attaching fresh revisions to old values.

Profile shows current name/role/lifecycle and pinned Definition version, then the existing Persona Form/Advanced JSON workflow for name, role, description and tone. Form and JSON share the existing strict parser/synchronization helper; malformed/unknown fields stay in the draft with actionable errors. Save uses displayed security/Instance/persona witnesses. Persona text guides behavior and grants no execution permission. Version adoption remains a separately authorized configuration flow, not a numeric field or implicit latest-version change.

Settings reuses the existing Instance section editor, field controls, constraints, sparse override/reset semantics and section spacing. Unchanged fields inherit the pinned Definition; saved changes apply to the next new Run. Each section saves its own explicit set/clear patch with displayed Instance/security witnesses, refreshes dependent sections and retains unrelated section drafts. A stale/denied failure never retries or silently clears drafts. Reload refreshes current witnesses while retaining unsaved values; successful writes whose refresh fails are reported as saved, with explicit read recovery and no replay. A confirmed mutation with an unreadable response retires its submitted draft and requires current read recovery before more commands. Archived/read-only controls remain disabled; inherited defaults, override sources and backend ceilings stay inspectable. Capability selection, automation/trigger restrictions and management configuration remain distinct from IAM permissions and approvals.

Canonical tenant Settings supplies its own cookie/CSRF transport and model catalog to the shared editor. No legacy owner, provider-secret, Sandbox profile or state-isolation route is called. The unconverted Sandbox section is explicitly unavailable until its tenant catalog/effects are implemented. Profile and Settings retain their own drafts and scoped request cancellation; dirty navigation offers Keep editing/Discard, while former scopes cannot display later results. Existing native Ant Design forms, tabs, disclosures, shared confirmation/draft feedback and 8/12/16px spacing work through 320px without a new theme, token system or generic wrapper.

### Instance Definition version interaction contract

The version page remains a separate scoped destination from Profile/Settings. Show current agent/organization, pinned source and version, then bounded exact-source publication choices with Active/Deprecated status. Choose from actual eligible versions; no free numeric field or implicit latest adoption. Owned Definition read is separate from Instance access, and a missing source permission explains the restriction without exposing other sources. An exact reader can inspect available review information without update/IAM-inspector authority; archived Instances remain read-only.

Preview is a read, never a mutation. Show the exact current/target versions, changed defaults, preserved Instance choices, new/changed/removed readable inherited items and the next-Run activation notice. Explain partial child visibility without counts or protected names. Compatibility failures block adoption and direct the person to review configuration or ask an authorized administrator. A preview whose Instance/security witnesses differ from the displayed context is stale and requires Reload and a new preview.

Adopt explicitly confirms the exact agent, organization and target version with reviewed witnesses. The command reauthorizes and revalidates independently. Stale/denied/revoked responses keep the target choice for deliberate review where still readable and retire the old preview; a confirmed save followed by a read failure requires read recovery without replay. New selection, scope retirement and changed context invalidate outstanding previews/confirmations. Keyboard, Escape/focus restoration, bounded native panels and 320px wrapping follow the existing form/confirmation owners. The canonical `/t/{tenantId}/admin/agents/{instanceId}/version` page now implements this contract with [focused verification](reports/tenant-instance-version-ui-verification-2026-10-18.md). Other tenant pages and the full proposal remain separately tracked.

## Instance Skills and Resources interaction contract

Skills and Resources remain separate scoped Instance destinations alongside Profile, Definition version, Settings, controls, Access and Connections. The shared Instance heading identifies the organization, persona, lifecycle and pinned source. Configuration eligibility, child content and execution IAM remain distinct: enabling a Skill or binding a file grants no execution authority. Changes apply to the next new Run; no existing Run is rewritten.

The main column reuses bounded configuration panels, collection search/pagination and native Ant Design controls. Search filters only authorized child metadata before totals and paging. Clear search resets the page. Empty/no-match, transient failure, denied, archived and partial-read states are distinct. Partial visibility explains that only readable items are listed, without hidden counts, IDs or dependency names. Direct item grants permit inspection/editing of that item where authorized; they do not permit creation of siblings. An actual tenant read/manage grant is required for new children, Customize and Copy. Item action hints come from the coherent read, independently of IAM-inspector permissions; commands reauthorize at commit.

Skills distinguish Built-in, inherited Definition and local Instance origins with full readable names, projection, capability requirements, effective enabled state and stored enabled choice. The existing shared Skill Drawer supplies labeled authoring/inspection fields. Built-in and inherited content is read-only; authorized inherited enable/reset choices remain independent. Customize explicitly confirms that it creates a local procedure and disables the inherited source atomically. Local create/edit retains the stable Skill ID, validates required fields and preserves a rejected draft. Delete names the exact Skill, Instance and organization. Missing capabilities and Off/Frozen suppression remain visible facts, never invitations to grant IAM implicitly.

Resources show exact logical path, origin, kind, size, effective enabled state, stored choice and only readable dependencies. Native upload/import fields keep selected bytes staged until explicit Save; metadata-only local edits preserve bytes and enabled state unless deliberately changed. Inherited bytes remain read-only. Copy confirms a distinct local destination/provenance; delete is a separate permission from manage. Download and any rich/Office preview use exact authorized tenant endpoints and safe rendered content, with no global route fallback. Dependency validation failures do not silently disable Skills or mutate another resource. Rich preview and bulk-import adapters require their own tenant cutover before frontend exposure.

Open editors own independent drafts. Reload refreshes current witnesses while retaining a rejected draft; an explicit discard adopts saved values. Paging/search cannot silently transfer a draft to another item. Unsaved navigation offers Keep editing/Discard; modal/Drawer Escape, visible focus, return to the surviving opener and a stable fallback follow the shared owners. Acknowledged writes retire submitted edits before read recovery; Reload never replays a command. A stale action requires current facts and an explicit new attempt. Definitive read loss removes protected fields and closes retired detail; tenant/Instance changes abort old reads and confirmations. Native wrapping and responsive row/detail composition must retain full content and reachable actions at 1440, 1024/768 and 390/320px without document horizontal overflow.

This contract preceded both tenant pages. The Skills page now has [focused browser verification](reports/tenant-instance-skills-ui-verification-2026-10-18.md); Resources remains pending. Backend editor projections alone do not establish browser acceptance. Verify actual create/inspect/edit/enable/reset/customize/copy/delete/download and relevant stale/revoked-access recovery through Synthetic browser journeys, then perform bounded visual review and design persistence. The full proposal and other resource/effect cutover remain open.

## Instance execution controls interaction contract

Scoped Instance navigation adds `/execution-budgets`, `/self-management` and `/unattended-model` under `/t/{tenantId}/admin/agents/{instanceId}`. They retain exact organization/Instance ownership and use the same live read/update and active lifecycle gates as Profile/Settings. One original-human fenced read supplies matching safe agent identity, budget defaults/overrides/effective values/host limits, management defaults/effective policy/overrides and current Instance/security witnesses. A changed witness rejects the whole view. No global owner, execution-limit, policy or model fallback is used.

Execution budgets reuse the existing per-class native fields and backend host ceilings. Each class inherits independently or uses a bounded preset/custom override; reset stages Definition inheritance, and only explicit Save commits it. Self-management reuses Off/Assisted/Managed, independent allowed areas, automatic-change choices and inherited built-in procedures. Configuration eligibility stays distinct from the agent's own IAM and exact approvals. Reset stages all inherited fields. Freeze/unfreeze uses an exact agent/organization confirmation and the saved policy rather than unsaved widening; it preserves unrelated unsaved policy edits. Frozen status describes the saved lock and remains visible during editing.

Unattended model reuses the existing catalog/effort controls and retains saved unknown choices until the user deliberately repairs them. The empty selection means the existing execution-default selection, not a new role or authority grant. Model catalog failures remain visible with read retry; saving waits for the authoritative catalog and valid selection. Configured provider aliases and secrets remain outside these pages.

All three pages reuse shared Instance headings, panels, fields and feedback. Save commits one explicit draft with displayed security/Instance witnesses. Confirmed writes retire submitted edits; a failed refresh is reported as saved and recovered by a read without replay. Stale writes preserve drafts. Reload keeps drafts while refreshing current witnesses; denied reads remove protected fields. Dirty navigation offers Keep editing/Discard, retired scopes cannot display late results, and read-only/archived controls explain the restriction. Native form controls and existing spacing wrap through 320px. Definition version adoption, Skills/resources and broader tenant surfaces remain separate obligations.

### Instance Connections interaction contract

Instances retain focused scoped navigation for Access and Connections, ahead of the organization collections. Inventory provides explicit links to both pages. Connections lives at `/t/{tenantId}/admin/agents/{instanceId}/connections`; deep links and switching sections preserve the same Instance/organization. Access remains independently protected by IAM-inspection authority; a configuration manager can use Connections without that authority.

Connections uses one bounded reading column with the agent name/lifecycle, Reload connections, safe paged bindings and Add binding. Each row shows its stable alias, Credential display name/purpose, active/disabled and applicable connection status. It shows only readable Credential metadata, with counts/paging after filtering; partial access is explained without revealing hidden names or counts. Remove appears only for independently allowed parent-update and exact Credential-bind hints on an active Instance. Archived Instances remain readable and explain that restoration is required before edits. Binding is configuration: it grants no execution permission or capability, connects no provider and reveals no protected value.

Add binding is a protected Drawer for the actual unsaved draft. Select an authorized visible active non-registry Credential from a paged inventory, with search explicitly limited to the loaded page, then enter a normalized alias (1–64 letters, digits or hyphens, starting with a letter or digit; surrounding spaces are trimmed and letters are saved lowercase). The selected Credential has its own fresh read/bind hint and safe details; changing selection clears obsolete details immediately. Connection status is informative: disconnected OAuth may be associated, with a link to authorized Credential management for setup. Configuration managers need no organization-wide Credential permission or IAM inventory; exact scoped read/bind grants remain sufficient. Credential creation/consent remains in Integrations → Credentials.

Save submits the exact displayed Instance/security/Credential revisions to the existing canonical binding API. Removal confirms agent, alias, Credential and organization, preserves the shared Credential/independent grants and uses exact displayed Instance/security/binding revisions. Writes never retry automatically. Stale/denied/transient failures retain the draft and give explicit Reload latest recovery; reload refreshes witnesses/selection authority without silently changing the alias or resubmitting. Acknowledged-write refresh failure requires reload before another command. Lost parent read hides protected content. Closing/navigating a dirty draft asks before discarding; cancel/close restores focus to Add binding. Native Ant Design Form/Drawer/Select/Pagination, shared query/command/draft/confirmation owners and 8/12/16px spacing provide keyboard and responsive behavior through 320px. No new visual system or generic wrapper is introduced.

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

Governance approval policy must show Core minimum and effective tenant policy separately. None never grants a missing permission. Use plain labels for None, Review when the requester lacks permission, and Always review; explain that independent review excludes the requester. Disable choices below the Core minimum and keep human-only/nondelegatable restrictions explicit. Authorized governors may return a stricter tenant setting to its Core minimum with current witnesses; use the existing confirmation composition when relaxing the tenant setting. An inspection result that says review is required is not a pending approval. Show a pending state only for an actual saved ApprovalRequest, and show missing qualified reviewers as an unavailable operation rather than a repeated approval loop. The backend policy/inspection prerequisite has [separate evidence](reports/tenant-operation-authorization-verification-2026-10-17.md); Governance UI and durable Approval detail implementation remain in the successor scope.

Members collection supports explicit status and search filters, bounded pages and one row-details destination. Principal picker searches only this tenant's active users and agent identities; role picker marks builtins and human-only roles. Scope defaults visibly to the current resource, never silently to the organization. Effective access shows **Direct** or **Inherited from {organization}** and the exact scope. Editing roles/assignments requires current permission and a concurrency revision.

Role drafts use the existing unsaved-navigation guard. Destructive lifecycle/access changes use `confirmAction` with organization, principal and consequence. Approval decisions use the same confirmation composition and saved action metadata, never client-supplied replacement arguments. Repeated submit is disabled while pending and protected by CAS on the server; stale decisions reload the final result.

Required copy:

- **Chat access lets people reach this agent. The agent uses its instructions and judgment when responding.**
- **These roles control what the agent can do. They are separate from the requester's roles and the agent's enabled capabilities.**
- Review: **The agent already has permission. Your decision reviews this action only.**
- Exceptional grant: **This agent lacks permission. Authorizing once permits only the action shown, before it expires. It does not assign a role.**
- Human-only exclusion: **Only an authorized person can perform this action. An agent cannot receive this permission through approval.**

Do not show raw JSON, hashes, internal physical paths, secrets or opaque principal IDs by default. A collapsed technical disclosure may show safe operation/revision identifiers for support. For saved or terminal approvals, explain Review/ExceptionalGrant as the reason at preparation time; do not assert that historical permission remains current. Current server qualification hints and POST/effect checks govern decisions. Dates use viewer locale with explicit relevant zone/expiry. Risk and state have text plus icon, never color alone.

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
