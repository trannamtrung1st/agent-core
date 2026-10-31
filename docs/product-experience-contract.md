# Product Experience Contract

Order 6 implements this contract for the currently available product. It supersedes earlier presentation descriptions where explicitly identified here; runtime, IAM and wire contracts retain their existing owners. Qualification is tracked separately in [the delivery ledger](reports/product-experience-delivery-2026-10-30.md) and [coverage manifest](reports/assets/product-experience/coverage.json). This document is a target contract, not a claim of completed implementation.

## Product and people

Agent Core is an organization workspace for talking to persistent agents and completing authorized work with inspectable results. Conversation remains the primary entry. Reusable Definitions, independently configured agent Instances, Spaces, Workflows, Tasks, Goals, evaluations and automation support that work under explicit human and agent authority.

Everyday members start or resume Chat with the General Assistant or another permitted agent. Advanced users follow files, work and approvals from their originating conversation. Creators configure agents and reusable Definitions. Reviewers inspect attributable evidence and decide only permitted approvals. Organization administrators manage resources and access. Platform operators administer the installation in a separate scope. These are jobs, not new roles or permission grants.

## Information architecture

Ordinary sign-in enters My organizations; opening an organization enters Chat. Explicit valid return destinations remain honored. Never choose an organization by guessing an ID or infer authority from a URL. Chat setup guidance reflects available agents/models and server-granted setup permissions.

The organization navigation has these groups:

| Group | Destinations | Purpose |
| --- | --- | --- |
| Work | Chat, Agents, Spaces, Workflows, Approvals | Talk, choose a persistent agent, choose context, follow processes, decide requests. |
| Build | Definitions, Evaluations | Author reusable behavior and independent checks. |
| Connect | AI models, Credentials, Secrets, Events, Sandbox profiles | Configure protected resources and execution readiness. |
| Organization | Members, Roles & permissions, Governance, Audit, Your access, Your organization profile | Manage the organization and inspect current authority. |

Agent pages replace the organization link inventory with agent-local sections: Identity (Profile, Definition version), Behavior (Settings, Skills, Resources), Execution (Sandbox, budgets, unattended model, isolation, self-management), Activity (Sessions, Runs, Automations, Continuity, State), Access (Access, Connections, Lifecycle). The local rail includes an explicit Back to Agents link and a discoverable organization-navigation transition. Definition drafts use the same contextual principle and their existing focused section registry.

Account navigation contains My organizations and Your account. Platform navigation contains only permitted system destinations. A labeled scope menu exposes Account/Platform transitions; it does not concatenate them with tenant or agent navigation. Platform permissions do not imply access to organization content. Existing unavailable destinations remain safely unavailable.

Keep the existing tenant-qualified URLs, parsers, query selectors, browser-history guards and exact saved links. This initiative changes presentation and ordinary entry behavior; it adds no legacy routing fallback. Retired unscoped routes remain retired. Shared navigation metadata belongs to one registry reused by AccessApp and the inventory.

## Page and interaction contracts

The shell shows product identity, current scope/organization and page. Agent identity is shown by the existing authorized owner heading; it must not be inferred from a raw ID. Each page has one task heading, a concise explanation, then actions and content. Collection filters identify their search scope; table overflow is local and labeled. Details use existing protected drawers; edits retain existing save/cancel, per-field inheritance, dirty guards, stale/readback and uncertain-outcome recovery.

Chat keeps identity, honest profile/status and conversation controls. Labeled Work tools groups Workflows, Tasks and Goals without merging their execution or proof. Files, background attention and immediate stop/end controls remain discoverable. Account and organization controls use explicit context disclosure. On narrow screens keep the composer and transcript usable before secondary tools. Text and voice remain modes of the same Session; service ownership, supersession, reconnect, attachment and approval behavior remain unchanged.

Agent selection says Agent; names and roles lead, version/technical identity remain contextual detail. Agent collections say Agents; Instance remains the precise term in configuration explanations. Definitions retain their domain name with a plain description of reusable agent behavior. Sessions are conversations and background contexts; Runs are individual executions; Tasks request accountable work; Goals verify success; Workflows coordinate immutable processes; evaluation evidence does not grant permission. Spaces supply context; Environments optionally supply variables. Credentials hold protected connections/authentication; Secrets are protected named values. No semantic owners are collapsed.

Errors name a safe problem and an available next action. Loading and stale data never masquerade as current success. Denied content is cleared. Disabled controls explain prerequisites when useful. Focus returns to the opener on dismissal, and keyboard navigation reaches the new task heading. Destructive changes retain exact confirmation; no action is replayed to obtain a nicer state.

## Visual direction and alternatives

The use scene is prolonged conversation and detailed work on a desktop, with mobile continuation. The owner's 31 October 2026 final refinement supersedes the earlier evergreen/field-guide **palette only** with a restrained deep navy/slate-blue workspace. Retain the dark-only profile, native Ant Design v6, contextual identity and all product/IA/runtime decisions. Navy grounds, cool white text and readable blue links establish hierarchy; semantic success green, warning amber and error red remain independent. No neon, glow, ornamental gradients, new font dependency or second UI system.

`web/src/app/antdTheme.ts` is the single runtime palette owner; AppShell projects resolved aliases into CSS and portals. The primary seed and resolved normal/hover/active states must support white action labels at 4.5:1; secondary/placeholder/link text is measured against actual surfaces. Native document canvases remain deliberate exceptions.

Settings use one coherent section panel per responsibility. Extend AgentConfigurationPanel and existing Collapse sections, with compact divided rows: label, source/reset metadata and helper text on the left; bounded scalar controls on the right. Use the container's available width, stacking deliberately when it cannot fit. Long instructions, code, policy matrices, model compound selectors and narrative editors retain full-width forms. Native Form.Item owns validation and association; retain explicit Save/Discard/Reset, sparse overrides, dirty/stale/unknown-outcome and authorization controllers. Section actions stay attached with a divider. Never nest decorative panels or turn tables into settings cards.

Shared navigation uses meaningful icons mapped to the canonical registry, with reviewed iconless treatment for unknown IDs rather than a silent gear. Group labels use body-size semibold text and an explicit chevron gap. The shell shows product → organization/scope → destination without a redundant brand divider or Organization label. Toolbars and related actions wrap only when their container requires it. Dense collections use meaningful columns and local scrolling; one-page pagination hides navigation while preserving a visible result count. Chat groups the short human Agent name and Saved/Temporary selection inline when they fit; option metadata remains in the expanded picker.

The earlier evergreen alternatives and their accepted source-specific evidence remain historical in the original delivery report. This refinement is separately qualified in the final UX refinement ledger; it does not retroactively change Order 6 acceptance.

IA alternatives: keeping a searchable 43-link inventory has low migration cost but retains scope confusion. Four global Use/Build/Operate/Govern modes reduce scanning but make Workflow/Approval ownership ambiguous. Selected: task groups plus contextual agent navigation and explicit scope transitions. This reduces split presentation ownership without inventing subsystems. Risk: advanced destinations become one disclosure deeper; retain their exact URLs, clear group labels and contextual return paths.

Typography uses the existing system sans for operational readability, native AntD scale and a restrained 28px page heading. AntD tokens own colors, controls and spacing. AppShell projects resolved tokens into portals and CSS. Parent owners control 8/12/16px group spacing and larger shell insets; children retain native control padding. Use local overflow, native focus, reduced-motion-aware transitions and color-independent statuses. No ornamental entrance animation on working screens.

## Internal critique and execution gates

Independent Assessment A reviewed source plus real sign-in, organization entry, completed Synthetic Chat, mobile navigation and agent settings. Score 24/40; priorities: Chat-first entry, scope-separated navigation, labeled Chat tools, intent-based settings disclosure and everyday Agent terminology. These are incorporated above. Independent Assessment B returned 16/20, with actual detector output `[]`; its source/DOM/screenshots/CLI limitations and the separate fresh finish review are recorded in the ledger. User authorization explicitly delegates reasonable product/design choices without recurring approval; selection is recorded here rather than awaiting a routine preference interview.

Before broad migration, prove the shared shell on account, organization, agent, system, Chat and Workflow surfaces at 1440/768/390px plus narrow 320px stress. Every discovered route/contextual view receives a disposition and evidence; source discovery alone does not qualify states. Required journeys J01–J15 and AC01–AC22 from the authorized proposal remain mandatory for Order 6 acceptance. Preserve the roadmap order: local self-host readiness, owner manual review, then Group Chat. Hosted CI is opt-in. Real providers, physical devices and customer deployment require separate qualification.
