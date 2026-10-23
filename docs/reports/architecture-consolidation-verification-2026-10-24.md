# Current architecture documentation consolidation — 2026-10-24

This is documentation-only work against implementation `1685bfe6`. It consolidates the architecture/entry documents and reconciles eight obsolete interface-status clauses with existing current owners. No architecture decision, application behavior, protocol, schema, migration, UI/design source, agent instruction or skill was changed. It does not accept the complete IAM proposal.

## Result

| Document | Before | After | Ownership decision |
| --- | ---: | ---: | --- |
| [System Architecture](../03-system-architecture.md) | 1023 lines / 27467 words | 241 lines / 3616 words | Stable responsibilities, boundaries and invariants; detailed contracts remain in their owning documents |
| [README](../../README.md) | 181 lines / 4345 words | 104 lines / 996 words | Current navigation and working instructions; historical freeze detail stays in Implementation Plan/reports |
| [Backend Interfaces](../04-backend-interfaces.md) | Existing detailed owner contracts | Eight status clauses corrected | Current implementation is no longer described as inspection/persona-only or missing already-converted Voice/native/background/state families |

Architecture's 125 top-level topic sections are consolidated into 15 stable sections with focused subsections. Existing dated reports retain their recorded claims, failures, limits and historical CI evidence. The README no longer repeats selected old model/version/route/authentication examples as the current implementation baseline. Its original Working with Codex and Cursor block is preserved exactly.

## Contract ownership retained

| Concern formerly repeated in architecture | Current canonical detail owner |
| --- | --- |
| Runtime, ownership, dependencies and invariants | Architecture responsibility model, per-Session execution and invariants |
| Tenant principal/permission/approval semantics | [Application contracts](../04-backend-interfaces.md#account-and-scoped-iam-ports-identity-foundation), exact current permission audit/inventory |
| Context, terminal Agent Step and Skill activation | [Context/step/Skill contracts](../04-backend-interfaces.md#typed-context-projection) |
| Definition/Instance/Resource/Harness configuration and source semantics | [Configuration owners](../04-backend-interfaces.md#unified-self-management-application-owners), persistence and protocol |
| Automation, events, accepted source lineage, destinations, children, waits and reports | [Automation/delivery contracts](../04-backend-interfaces.md#automation-destination-and-delivery-ports), event model and protocol |
| Workspace, Attachment, Office and Artifact read/mutate/export/recipient boundaries | Dedicated typed source/effect contracts in Backend Interfaces and persistence |
| Browser/Sandbox/account/HTTP effects, readiness and uncertainty | Dedicated effect/read/receipt owners, [Operations](../17-observability-and-operations.md) and scoped family reports |
| Profile, Memory, continuity, Experience and request isolation | [State/isolation contracts](../04-backend-interfaces.md#state-observation-and-coordinated-isolation-configuration) |
| Voice, provider replacement and transport-specific limits | [Voice](../06-realtime-voice.md), provider ports and protocol |
| Milestone/freeze/acceptance history | [Implementation Plan](../18-implementation-plan.md), dated reports and [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md) |

This preserves current contracts while removing duplicate progress narrative. In particular, it retains mailbox ownership; raw-audio separation; independent STT/LLM/TTS; cancellation plus identity retirement; received/heard context; actual tenant/executor/requester ownership; frozen capability limits; current source and effect checks; narrow durable grants/root suppression; quiescent cleanup and uncertainty; no new Task/Environment/RBAC/orchestration engine; and separation of Core, Runtime and Evidence.

## Corrected stale statements

The interface corrections cover registry/state/Experience status, queued-input Voice status, later background creation/effect/Activity/inbox/wait conversions, independent protected Browser integration, Artifact-backed Browser drop, Sandbox editor status, Sandbox SDK/control/cleanup status, and the old inspection-only common executor description. These now point to the current typed owners and scoped evidence rather than presenting an earlier checkpoint as the latest state.

Coordinate/geolocation remain unsupported in the tenant adapter: this was checked in `TenantBrowserAuthorizationContract.Supported` and `TenantApprovalRules.Browser`. Artifact-backed drop support is current in `TenantBrowserUploadRules`. No availability was broadened from a descriptor name. Browser speech remains explicitly half-duplex while the canonical serverAudio path keeps microphone input during output.

## Verification

- All **24 existing incoming architecture references**, across **17 unique fragments**, resolve. Descriptive headings are preserved; six historical section IDs are navigation anchors without resurrecting dated contracts. Read-only `.agents` context and existing design-brief links need no edits.
- Local files and Markdown fragments in the affected entry documents resolve; code fences are balanced. The final check also covers this report and the delivery-ledger update.
- All **29 named C# symbols** in the new architecture exist in current source. Program registration resolves tenant dispatch/control/state facets to the same `TenantSessionRuntimeHost`; current tenant owner declarations and the native eligibility restriction were inspected. Declaration/search checks establish documentation references, not functional acceptance.
- README's working-instruction block matches the previous content byte-for-byte after trailing-newline normalization.
- `git diff --check` and the staged check pass. Only Markdown files changed. No UI/brief/design sidecar or source/schema file changed; the Impeccable launcher was not rerun.

The link/fragment/source inventory is saved at `/private/tmp/architecture-consolidation-checks-20261024.json`; originals used for comparison are `/private/tmp/agent-core-architecture-before-20261024.md`, `/private/tmp/agent-core-interfaces-before-20261024.md` and `/private/tmp/agent-core-readme-before-20261024.md`. No generated implementation artifact or permanent duplicate architecture was added.

Application builds, runtime/browser tests, Mermaid rendering, CI and live providers were not run for this documentation-only change. Existing qualification reports remain scoped evidence. Remaining interface/detail cleanup, concrete store/effect inventory, safe consolidation and final security/isolation/UI acceptance stay on the original active goal.
