# Agent Core — Self-Hosted Decision Intelligence

**Final implementation proposal | Initiative 6 | 26 October 2026**  
**Repository:** https://github.com/trannamtrung1st/agent-core  
**Inspected baseline:** `main` — `aab065eaabe8be27d5fd0480e1e8cdccae76b468` (26 October 2026)  
**Status:** Approved-direction implementation brief; **not yet implemented or runtime-verified**.  
**Destination in repository:** `docs/proposals/self-hosted-decision-intelligence.md` (or equivalent canonical proposal location approved by the maintainer).

> **Mandate:** Add one lightweight, optional and self-hostable intelligence capability that makes bounded decisions across existing Agent Core owners. Complete the affected contracts, persistence, API, frontend, deployments, tests and documentation together. Use a **clean cutover**—no legacy decision adapters, deprecated fields, aliases, dual reads/writes, silent model substitution or lingering compatibility routes. Preserve current IAM, exact approvals, immutable execution history and existing execution ownership. Do not expand into a new agent framework, workflow engine or general-purpose orchestration service.

---

## 1. Executive decision

**Implement one shared application-level decision interface, backed initially by a local Kev adapter and an authorized existing-LLM adapter, and connect it to current runtime owners.** Begin with complete Smart Model Routing + Auto Mode, qualify them end-to-end, and then attach only high-value relevance/gating decisions to existing Skills, context, initiative and event paths. Keep future Goals, Evaluation, Delegation and Group Chat integration out of this cutover.

**Recommended model:** [Kev-4B](https://huggingface.co/jaredpalmer/kev-4b), open weights under Apache-2.0, trained to answer System One-style typed `choice`, `noul` (yes/no), and `score` questions. It is **not a conversational LLM**, does **not generate plans**, and must **not** be registered as a tenant inference offering. Recommend a **24 GB GPU** for practical deployment margin (upstream indicates ~14 GB-plus GPU residence and that some ~16 GB GPUs may work), or ~32 GB Apple Silicon. [Kev-0.8B](https://huggingface.co/jaredpalmer/kev-0.8b) is a separately selected low-resource evaluation profile; do not automatically downgrade to it. These are candidates subject to Agent Core-specific evaluation, not predetermined winners. See [Kev repository](https://github.com/jaredpalmer/kev), [Kev-4B model card](https://huggingface.co/jaredpalmer/kev-4b), and its [API specification](https://github.com/jaredpalmer/kev#api).

**Business/product invariant:** Normal Agent Core remains deployable and useful with Decision Intelligence disabled, unconfigured or offline. This means **explicit deterministic operation based on current settings**, *not* continued maintenance of the old decision implementation as a parallel legacy stack. For an Auto request when the engine is unavailable, the documented policy may use an eligible, explicitly configured default and disclose it; otherwise refuse safely. A fixed user/model selection is never silently replaced.

### Definition of done

The initiative is complete when an owner can configure the optional local service; a tenant can opt into Auto; a new foreground or unattended Run is routed **once before admission** among authorized models; the chosen model/reasoning effort, basis and configuration revisions are captured durably; existing execution and approval paths work without diversion; disabling the capability leaves deterministic fixed/default work functioning; the UI honestly describes all states; old mutable selection shapes and obsolete runtime paths are gone; and the evidence/qualification gates in §15 pass. No external GPU/provider acceptance is claimed until executed.

---

## 2. Actual repository baseline and integration map

The repository's [TODO.md](https://github.com/trannamtrung1st/agent-core/blob/main/TODO.md) still lists **Self-Hosted Decision Intelligence as initiative 6**; initiatives 1–5 are locally delivered with separate external/UAT limits. The implementation plan explicitly states that the latest clean-baseline stabilization did **not** start Decision Intelligence. This proposal does not reopen completed prior initiatives.

| Existing owner / path | Verified present behavior | Required decision integration |
| --- | --- | --- |
| `src/AgentCore.Domain/Models/ModelManagement.cs` | Tenant connections, offerings, protected credential references, capability declarations, explicit tenant default and exact `ModelRoutePin` | Reuse registrations and route identity; add only justified routing policy/metadata, not a second model registry |
| `src/AgentCore.Application/Ports/ModelCatalog.cs` and `Models/TenantModelCatalogReader.cs` | Authorized tenant-scoped catalog, `ModelDescriptor`, eligible offering materialization | Candidate sourcing and hard filtering before intelligence; retain same current-security checks |
| `src/AgentCore.Application/Models/TenantModelRouteAuthority.cs` and `ModelRouteRules.cs` | Execution-time route/credential/security-revision checks | Non-bypassable final authorization after recommendation and at dispatch |
| `src/AgentCore.Application/Models/SessionModelBinder.cs` | Inherited model precedence, explicit fixed user/host selection, reasoning effort validation | Separate **saved intent** (`inherit`/`fixed`/`auto`) from **effective Run binding** |
| `src/AgentCore.Application/Models/ExecutionModelPolicy.cs` | Automation override → Instance unattended override → default; Tool/vision checks | Extend same policy owner to resolve an explicit Auto request for new unattended Runs |
| `src/AgentCore.Domain/Conversation/SessionModelSelection.cs`, `Domain/Triggers/ExecutionModelPin.cs`, `Application/Execution/AgentRunAdmissionFactory.cs` | Captured selections and immutable per-Run model pins | Persist effective Auto outcome and provenance without rebinding historical/active Runs |
| `src/AgentCore.Application/Security/TenantSessionService.Models.cs`, `Api/Realtime/TenantSessionRuntimeHost.Models.cs` | IAM + revision-checked quiescent Session selection changes | Save Auto intent through the **same** owner and no-active-Run reservation |
| `src/AgentCore.Application/Models/UserTurnCapabilityValidator.cs` | Image/attachment capability checks against a resolved model | For Auto, enforce image/tool/capability requirements before immutable Run admission |
| `src/AgentCore.Application/Agents/DefaultInitiativeEvaluator.cs`, `InitiativeEvaluator.cs` | Existing LLM makes initiative choice **and creates a speaking plan** | Optional decision gate may choose *whether*; preserve LLM planning for *what* to say, through the same initiative owner |
| `src/AgentCore.Infrastructure/Events/RestrictedEventFilter.cs`, `Application/Events/EventFilterRecovery.cs`, `TenantEventProcessor.cs` | Deterministic restricted JS expression filter, durable bounded recovery/delivery | Keep expression filter authoritative; optionally apply **post-filter semantic relevance** without replacing permission or delivery logic |
| `src/AgentCore.Application/Agents/EffectiveSkillCatalogResolver.cs`, `SkillLoadAdmission.cs`, `PromptContextBuilder.cs`; `Context/ContextAssembler.cs` | Effective Skills, fixed procedure budgets, Core / Runtime / Evidence context layers | Rank only eligible items; do not grant capabilities, hide mandatory tools, or promote evidence into instructions |
| `src/AgentCore.Application/Continuity/ContinuityRelevance.cs` | Lexical relevance over canonical projections | Reuse as deterministic ordering baseline; optional intelligent ranking after current scoped/consent reads |
| `src/AgentCore.Infrastructure/InfrastructureServiceCollectionExtensions.cs`, `Api/Program.cs` | Synthetic/Real composition, current adapters and runtime owners | Single decision-engine registration and host options, plus an optional local HTTP adapter |
| `web/src/features/chat/ModelPicker.tsx`, `web/src/features/admin/ExecutionModelFields.tsx` | Model selection and reasoning effort; “default” currently means inherit | Distinct Auto control and effective choice display; shared interaction semantics |
| `web/src/features/access/TenantModels.tsx`, `ModelDefaultEditor.tsx`, `accessRoute.ts` | Organization AI Models Connections / Models / Defaults, explicit default and existing admin UI patterns | Add Routing in the same feature area; no new settings application or UI kit |
| `docker-compose*.yml`, `scripts/compose.py`, `scripts/dev-real.sh`, `.env.example` | One application container and separately managed optional Sandbox; canonical config preflight | Optional, internal-only decision Compose overlay; native launcher integration and canonical config reference |

**Important real limitation:** `ModelDescriptor` has optional `ContextCategory`/`CostCategory` members, but the current `FromBinding` path does not populate verifiable prices, latency statistics or context capacity. Routing must **not** claim cost-optimal or context-fit selection from absent data. Add bounded metadata/telemetry only where it demonstrably improves decisions; otherwise expose “unknown” and prefer capability/reliability signals.

Architecture rules remain as written in `AGENTS.md`: Domain BCL-only; Application owns decision contracts and policy; Infrastructure owns HTTP/model transports; Contracts owns wire DTOs; API owns composition and authorization mapping; Ant Design v6 is the only frontend component baseline. No MediatR, broker, Redis, parallel Run engine or general-purpose policy framework. The optional Kev inference sidecar is a **specific exception to the microservice non-goal** and must be reflected in canonical architecture/technology decisions *before* adding the service.

---

## 3. Scope and rollout commitments

| Scope | Outcome | Implementation expectation |
| --- | --- | --- |
| **S1 — Mandatory: foundation** | Typed decision interface, validated local backend, authorized LLM adapter, Synthetic fixture, config and diagnostics | Fully delivered in this initiative |
| **S2 — Mandatory: routing + Auto** | Foreground/chat and unattended model choice + reasoning effort, immutable Run pins, explicit UI | Fully delivered end-to-end before treating v1 as complete |
| **S3 — Current-owner integrations** | Optional Skill/tool relevance and scoped context/memory relevance | Implement only where already-produced eligible candidate sets and bounded request budgets exist; otherwise document the owning blocker rather than invent parallel data stores |
| **S4 — Current-owner integrations** | Initiative relevance gate and optional event semantic relevance | Integrate behind current initiative/event policies; retain generative initiative plan and durable event recovery |
| **S5 — Advisory only** | Contextual configuration recommendations | Use existing configuration assistance/approval channels; intelligence never changes settings itself |
| **Deferred until owning work exists** | Work priority over Goals, Eval outcomes, Delegation, Group Chat | Define typed score contract now; do **not** create speculative Work entities, task queues, policy APIs or UI |

The first shipping milestone is **S1 + S2** with production-quality behavior and complete cutover for those affected surfaces. S3–S5 can be landed as **bounded subsequent vertical slices** using that same interface, with independently verified acceptance; do not present future features as implemented. The final Initiative 6 delivery ledger must mark each scoped slice completed/deferred explicitly. Later initiatives 7–10 consume this service through existing owner boundaries, not pre-built hooks in this change.

### Exclusions

No autonomous new Agent Instance, new execution engine, model training/fine-tuning pipeline, public arbitrary-decision API, cross-tenant embeddings store, unrestricted event classification, dynamic provider registration via decision output, vector DB, universal router for every tool call, new cost billing subsystem, general evaluation framework, new distributed scheduler, human-review role, or speculative goals/delegation UI.

---

## 4. Authoritative architecture and ownership

```text
Existing authorized Agent Core owner
   ├─ acquire current tenant/Agent/Session/Run scope
   ├─ apply deterministic hard filters and explicit precedence
   ├─ if user/policy selected Auto and capability is enabled:
   │    └─ IDecisionEngine.DecideAsync(typed, bounded, scoped request)
   │          ├─ LocalKevDecisionAdapter  → optional internal sidecar
   │          ├─ AuthorizedLlmDecisionAdapter → existing pinned Run invocation only
   │          └─ SyntheticDecisionAdapter → key-free deterministic testing
   ├─ validate typed probabilities, quality policy and eligible ID
   ├─ recheck current IAM / model route / revision fences
   ├─ freeze resolved decision + provenance into existing Run admission
   └─ existing Session → Activation → AgentRun execution / effect / approval path
```

`IDecisionEngine` is **an advisory scorer, not an authorization boundary**. Its output is untrusted. All final assignments, state mutations, effects, permissions and dispatch remain with the existing authoritative owner. There is **one** interface for all decisions and **one** caller policy per use case, not a string-based global classifier or multiple independent wrappers.

**Application contract sketch** (conceptual names may match project conventions during implementation):

```csharp
public enum DecisionKind { Choice, YesNo, Score }
public enum DecisionPurpose {
    ModelRouting, ReasoningEffort, SkillRelevance, ContextRelevance,
    InitiativeRelevance, EventRelevance, ConfigurationAdvice, WorkPriority
}

public sealed record DecisionCandidate(string Id, string Label, string? SafeDescription);
public sealed record DecisionRequest(
    DecisionPurpose Purpose,
    DecisionKind Kind,
    IReadOnlyList<DecisionCandidate> Candidates,
    IReadOnlyDictionary<string, string> Signals,
    DecisionExecutionContext Scope, // current authenticated execution/admission facts, not user-supplied
    int InputBudget, string PolicyRevision);
public sealed record DecisionResult(
    DecisionStatus Status, string? SelectedCandidateId,
    IReadOnlyDictionary<string, double> Probabilities,
    double? Score, string EngineId, string EngineVersion,
    string? DiagnosticCode);

public interface IDecisionEngine {
    ValueTask<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct);
}
```

This sketch is **not an additional public API or a mandate to add all enum values before use**. Use small type-safe records and closed purpose enumerations, not flexible arbitrary prompts from callers. Do not serialize `Scope` identities/secrets to Kev. Transport DTOs live in Infrastructure; do not leak vendor types into Domain/Application.

### Backend selection and authority

1. **Local**: permitted for pre-admission model routing and in-Run relevance because Kev is a host-owned advisory service, not a tenant inference connection. It receives only the approved, minimal decision projection.
2. **Authorized LLM**: permitted **only** if an existing, current `ModelInvocation` / Run route authorizes the call. Reuse `ILanguageModelResolver`, `ModelPurpose` and existing credential/IAM/route fences. It cannot be used to select an unadmitted Run's model by invoking some ambient/admin/provider key. Do not mint an implicit decision Run or bypass tenant authority to make it “interchangeable.”
3. **Synthetic**: no credentials, no network, deterministic per test scenario through the *same* interface and policy. Synthetic is an accepted test mode, not a deployed competing legacy algorithm.
4. **Disabled/unavailable**: callers use their **defined current deterministic behavior**. No automatic LLM-to-local, local-to-paid-LLM, or Kev-4B-to-0.8B failover. For an Auto selection, use the configured fallback policy described in §5, never silently override a fixed selection.

Some adapters cannot legally serve every purpose: declare supported scopes/capabilities explicitly, validate configuration at activation, and produce an actionable unsupported outcome. Do not claim LLM/local operational interchangeability for **pre-Run routing** before an independently authorized inference contract is designed.

---

## 5. Smart Model Routing and Auto Mode — exact behavior

### 5.1 Persist intent separately from the frozen answer

Introduce a canonical **model preference** in the current owning configuration objects:

```json
{
  "mode": "inherit | fixed | auto",
  "offeringId": "canonical-guid-only-when-fixed",
  "reasoning": { "mode": "modelDefault | fixed | auto", "effort": "only-when-fixed" }
}
```

These are symbolic schema values, not literal pipe-delimited input. The saved preference is mutable through current IAM/revision-checked Session, Definition, Instance and Automation owners. The existing **effective selection** (`SessionModelSelection` or equivalent projection) and `AgentRunModelPin` always contain one concrete eligible offering and effort; **Auto is never a provider ID** and is never passed to a provider transport.

Store the intent and effective/captured value as **different meanings with a single canonical owner each**:

- **Session:** persisted user preference (`inherit`, fixed or Auto). Resolve Auto at *each newly admitted* foreground Run, **after** seeing the accepted input and attachments. The existing live/quiescent Session reservation remains authoritative for preference edits. Display the active intent and latest effective model separately.
- **Definition / Instance:** existing sparse inheritance; a higher explicit fixed or Auto preference wins over inherited lower levels. Do not re-interpret null/default as Auto.
- **Automation / unattended:** existing precedence remains explicit Automation override → Instance unattended preference → effective Definition preference → tenant explicit default. At whichever layer wins, `fixed` stays fixed; `auto` invokes routing once for that new occurrence.
- **Reasoning:** an explicit fixed effort wins and must be supported, otherwise refuse; Auto effort may choose only from the finally selected offering's declared efforts. `modelDefault` uses that offering's declared default; no made-up effort levels.
- **Session changes:** future Runs only. Current Run, approval continuation, voice playback and historical model provenance retain their captured identity.

Do **not** remove existing fixed/default semantics. Instead migrate their stored representation to this one canonical shape and remove obsolete fields/endpoint shapes that compete with it.

### 5.2 Resolve at the existing admission boundary

1. Obtain current tenant + executor authorization, active Space/Membership and Agent policy, exact accepted task/attachment metadata, eligible model catalog and current setting revisions.
2. Determine explicit preference and its source using existing foreground/unattended precedence. If fixed: run the existing validation/pin path; do **not** consult Decision Intelligence.
3. If Auto: pre-filter candidates **deterministically** by tenant, enabled/not retired/not unavailable connection, current credential/route eligibility, supported trusted instruction hierarchy/semantic mode, tools, required vision, required requested modalities, fixed user constraints and permitted reasoning. Cap options and preserve stable ordering.
4. If no eligible candidates: reject with a specific recoverable model configuration error. If one: select it without inference (record `singleEligible`). If multiple and engine enabled: score bounded, sanitized metadata/task signals. Do not transmit credentials, raw private sources, unbounded conversation, user secrets or arbitrary URLs.
5. Validate decision response strictly: exact candidate ID, finite `[0,1]` probabilities, expected option set/normalization tolerance, nonempty model ID/revision, bounded response size, and a **task-calibrated** acceptance threshold. Discard low-confidence/invalid/unavailable decisions.
6. Apply **explicit deterministic selection policy** on absent/low-confidence result: use the **already-configured eligible default** only if the tenant's Auto policy allows it and its capability matches; otherwise return `auto-unavailable`/`no-eligible-default`. Never choose the cheapest/first/random or another user model silently.
7. **Re-read/revalidate authoritative state** at commit/admission, including policy/tenant/security revision and route/credential epoch; if changed, refuse/recompute only before admission under a bounded retry and never execute with stale authority.
8. Persist resolved offering, connection, external model, effort, `ModelBinding`/route witness, source `auto` or `fixed`, policy revision, decision engine/checkpoint identity, outcome reason (`ranked`, `singleEligible`, `configuredDefault`, etc.), **not** raw scores/prompts. Bind once, then use existing AgentRun/ModelInvocation/dispatch fences.
9. Retries/resumes of the **same** AgentRun use its exact original model pin. A new Run after completion/cancellation may re-evaluate Auto against current authorized state. Never re-route after a provider failure inside an already admitted Run.

**Critical admission edge cases:** image attachments, tools, native/provider capability mismatches, unavailable tenant default, disabled credential, revocation between scoring and admission, generation races, user-initiated model change while voice is live, newly disabled model, and a process restart after an admitted Run **must not** cause a silent different model to be used.

### 5.3 Objectives and truthful metrics

The initial router may offer **Balanced / Quality / Speed / Cost-aware** objectives, but *only* if the corresponding inputs are trustworthy. The current model declarations do **not** provide reliable per-offering price, latency or context-window values. Therefore:

- Default **Balanced** uses capability/reliability and any explicitly maintained relative preference tiers; never invent token cost, latency or capacity.
- Collect bounded per-offering **observed** latency/error summaries through existing telemetry; identify sample count, time window and uncertainty. Do not expose cross-tenant data or treat provider-wide samples as tenant secrets. Cold starts, retries and voice timing should not corrupt model quality estimates.
- Cost-aware mode needs an explicit current unit/rate provenance or owner-maintained cost tier; until then **disable it with an explanatory UI state** rather than falsely optimize.
- Context-fit filtering requires validated per-offering limits; otherwise use existing safe maximum/budget policies, not fabricated context sizes.
- Provider decision estimates never relax maximum spend, token, execution or tool ceilings. No billing subsystem here.

### 5.4 Example

User selects **Auto** in Chat. A new message includes an image. The policy filters out offerings that lack vision or required tool semantics. Kev ranks only the eligible remaining options. Agent Core checks the winning offering still belongs to this tenant and has the same authorized connection and credential epoch, captures the exact concrete `ModelBinding` into the new Run, then executes through the existing model resolver. The chat chip reads **Auto · resolved to [model/connection]**; disabling Kev before the *next* Run changes only the next decision policy, not the admitted Run.

---

## 6. Kev model and service contract

### Model selection

- **Default candidate:** `jaredpalmer/kev-4b` at a **pinned version/revision** (the published Kev-4B model card identifies checkpoint `139fdd94`, 24 September 2026; verify and pin the corresponding immutable artifact/weight hashes before packaging).
- **Alternative:** `jaredpalmer/kev-0.8b` only as an **explicitly configured lower-resource backend** after independent quality assessment; never an automatic failover path. Consider alternatives like [Laya](https://github.com/LeonaDavinci/laya-system-one) only after confirming source, license, contract and benchmark suitability (link is an evaluation lead, not a verified integration dependency).
- **No official Jev binary assumption:** Kev is an independently hosted Jev-like model implementing the public System One shape; TypeSafe's hosted Jev is not the same checkpoint.
- **Benchmark first:** upstream accuracy (Kev-4B reports ~0.838 on one locked transfer test) is **not** Agent Core routing accuracy. Upstream “confidence” for Choice is a transformation of option probability, **not empirical correctness**. Calibrate acceptance thresholds against Agent Core data with off-domain and adversarial examples.

### Service packaging

Create a **small optional** `deploy/decision/` build, using the pinned Kev source, locked Python dependencies and pinned model revision, plus a `docker-compose.decision.yml` profile/overlay. The documented upstream bootstrap uses Python 3.12/3.13, `uv sync --extra serve`, and `uv run --extra serve python -m kev.serve --run jaredpalmer/kev-4b@v1.0 --port 8009`; **`pip install kev` is a different package**. Adapt to container network binding and immutable artifacts; never fetch mutable `latest` weights implicitly on each restart.

**Transport:** `POST /v1/systemone` with `state`, `model` and named `questions`; `choice`, `noul`, and `score` are supported. `GET /v1/models` can report the loaded checkpoint; use real readiness + an optional short inference probe rather than inventing an upstream `/health` endpoint. See [Kev README API](https://github.com/jaredpalmer/kev#api). Validate the exact upstream API with integration tests at the pinned commit before finalizing transport DTOs.

**Security:** Only the app's internal Compose network / explicit loopback trusted endpoint may reach the service. No published public port by default; no Docker socket, no shared Workspace volumes, no tenant credential database, no ad hoc remote endpoints. Configure `KEV_API_KEY` (bearer authentication), pass the key as a private secret, reject redirects/proxies/metadata hostnames/unapproved DNS/address changes and apply fixed request/response caps. Model I/O is a potentially sensitive processing boundary even when self-hosted—send only least-privilege projected data; never assume a local container is a substitute for authorization.

**Operability:** Memory/GPU reservations, bounded queue/concurrency, warm-up/ready states, cancellation and timeouts; bounded admission when saturated; app starts with engine disabled/unavailable; shutdown drains in-flight decision requests; sidecar restart never resurrects an AgentRun or duplicates effects. First-time model download is a **separate explicit operator step**, with actionable status and cache volume policy. The service is not started by the Synthetic default and is not a hard dependency for default native/Real startup.

---

## 7. Relevance and initiative integration (bounded S3–S5)

### Tool / Skill relevance

Hook **after** `EffectiveSkillCatalogResolver`, current Agent/Membership access, and tool configuration gates. Rank only eligible tool/Skill descriptions for context budgeting/discovery. Preserve mandatory Core tools, explicit user selections, always-projected Skills, and existing on-demand load/activation rules. A low score cannot revoke an offered authorized tool or authorize a new one; do not pre-load a Skill procedure beyond existing budget or consent. Keep exact Skill IDs/revisions pinned. The existing lexical/ordered path is the baseline when intelligence is disabled.

### Context / Memory relevance

Hook where `PromptContextBuilder`, continuity and memory owners already produce currently readable candidates. Rank scoped, already-authorized **Evidence**; never fetch a private source merely to score it, mix tenants/Spaces, bypass read Review, or turn Evidence into Runtime/Core instructions. Respect Context Architecture's `Core / Runtime / Evidence` separation, provenance, item/character/token budgets, retention and source freshness. On failure, use current bounded deterministic selection without widening source access.

### Initiative intelligence

Keep `DefaultInitiativeEvaluator`, `IInitiativeEvaluator`, `SessionRuntime.Initiative` and their speech/voice caps as the one lifecycle. A local `noul` gate may evaluate whether an unsolicited action is relevant **after deterministic cooldown/eligibility**. For `staySilent`, follow existing silence/inactivity counters and allowed waits; for `speak`, the **existing LLM path still produces and validates the `InitiativePlan`** because Kev does not generate content or plans. A decision result must not itself cause a Run, speak or deactivate outside the current state machine. If the gate is unknown, apply documented current policy without treating absence of a probability as permission to speak.

### Automatic trigger relevance

Never replace `RestrictedEventFilter` (`js-expression-v1`), `EventFilterRecovery`, `TenantEventProcessor`, registration IAM or owner-bound delivery. Apply optional semantic relevance only **after** the deterministic filter, with explicit per-subscription opt-in and bounded, **durably recorded** verdict/engine/policy revision. Existing event deliveries keep idempotency and at-most-once/retry rules. An unavailable model must be an explicit `undetermined`/retry/exhaustion outcome under current durable recovery—not quietly dropped or replayed endlessly. Avoid automatically suppressing important actionable events unless an evaluated tenant policy explicitly permits it; ship shadow mode first.

### Configuration assistance

Only return recommended values, rationale and candidate IDs to the **existing** instance/definition/tenant configuration UI and self-management tools, subject to same visibility. Actual changes pass current IAM, revision compare-and-swap, validation, approval and current-instance self-management restrictions. No automatic cross-Agent administration and no special Admin Assistant.

### Work priority

Support a generic typed `score` over **already existing authorized pending activities** only when a real owner supplies a bounded list. Until initiatives 7–9 define evaluation results, Session Goals and delegated work, **no new Work table, second scheduler, priority dashboard or orchestration logic** is authorized here. Mark the deferred consumer in TODO, not an unfinished runtime branch.

---

## 8. Policy, persistence and authoritative cutover

### Policy owners

1. **Host settings:** one strongly typed `DecisionIntelligence` options section owns enablement, backend (`Disabled`, `LocalKev`, or `AuthorizedLlm` where permitted), exact local endpoint, internal secret reference, timeout/queue/input budgets, model identity, optional tracing and shadow/evaluate policy. Reject unknown/retired option spellings at startup using current configuration preflight.
2. **Tenant routing settings:** extend the existing `TenantModelSettings` revision-checked owner with a small typed Auto routing policy (enabled, objective when supported, authorized candidate allowlist if needed, minimum calibrated acceptance policy, and named deterministic outcome on failure). Do not add a second model/connection table or global primary model setting. Reuse existing organization model-management authorization unless a demonstrably distinct new permission is required; if added, migrate roles deliberately.
3. **Definition/Instance/Automation/Session:** store the canonical preference at the existing owner where model defaults/overrides live; inherit through established sparse resolution. Do not put these settings in **Space Environments**—they are typed variables/metadata, not execution authority.
4. **Run/event evidence:** record immutable effective decision fingerprint, engine/checkpoint and basis in existing Run admission/provenance or existing event-decision records. Prefer an additive, bounded field in the existing JSON/records over new generic decision-history tables. Retain safe historical display without saving decision input text, logits or secrets.

### One clean migration, no running legacy path

- **Preflight:** inspect all active mutable preference fields, existing Session and Automation selections, Definition publications/snapshots, Instance overrides, enrolled Models, old Run pins, approvals and triggered occurrences. Generate a migration inventory with count and refusal reasons. Verify currently supported schema versions and backup availability.
- **Offline cutover:** stop writers/dispatch, back up SQLite and private configuration; add the new normalized preference schema and required indexes/constraints only; transactionally convert the *current supported* fixed/default choices to `fixed`/`inherit` according to recorded origin. **Never infer Auto from a blank field.** Validate referential/tenant ownership and nonterminal Run pins.
- **Immutable history:** do not rewrite historical AgentRun, approval, credential epoch or completed event provenance. Where current immutable Definition versions contain old source shape, preserve their historical evidence and perform a versioned canonical projection or exact audited conversion **without silently changing their hashes/contents**. If the old stored shape cannot be represented without a runtime alias, **abort and require an operator-approved fresh-state reset** rather than shipping dual readers.
- **Atomic ownership:** introduce final Domain/Application/Contracts/API/frontend representations together. Remove retired fields, hard-coded model values, stale DTOs, old UI “default means Auto” assumptions, duplicated selection helpers and obsolete endpoints/feature flags. No long-lived dual write, ignored unknown fields, “compatibility parsing,” or migration-on-request logic.
- **Data reset (only if necessary):** allowed with explicit operator opt-in and backup. Never reset silently at app startup, test setup or container recreation. Preserve separately required external data and tenant credential protection keys as prescribed by existing runbooks.
- **Release:** validate migration on fresh InMemory/Synthetic, fresh SQLite, backed-up predecessor snapshot and SQLite reopen; verify exact Run/approval/Session/Automation references. Fail startup with actionable remediation if an unsupported old database/config remains, rather than guessing a default.
- **Rollback:** rollback means stop app, restore the matched code/image and **its matched backup/config**. No promise that schema downgrade safely converts forward Auto data.

**No backwards compatibility** means no alternate live implementation or alias protocol. It does **not** authorize silently discarding security evidence or changing immutable work that has already been admitted.

---

## 9. Runtime trust, security and failure policy

**Hard boundaries (must hold in every test):**

- Current tenant, principal, Agent, Space/Membership, eligible Model Offering, credential epoch and `ModelRoutePin` checks happen at their authoritative owners. The decision service never chooses outside the supplied post-filter set and cannot broaden IAM.
- No secrets, connection tokens, private browser data, arbitrary Workspace bytes, hidden other-tenant identifiers or full transcripts leave for ranking by default. Use named sanitized features; introduce explicit consent/policy review for any richer input, especially remote LLM processing.
- The LLM decision adapter calls only through a legitimately admitted current `ModelInvocation`, never through a hidden global model connection, bootstrap key or unresolved provider name. `LocalKev` uses host-owned transport, not the tenant's protected model CRUD.
- Treat candidate descriptions, task snippets, retrieved text and decision results as untrusted. A text directive in an option can never change system instructions, evaluation schema or permissions.
- Independently bound input, candidate count, output bytes, concurrency, cancellation and request deadlines. No infinite retries, cascading models, unbounded GPU queuing, abandoned requests, response cache sharing between tenants, or stale late responses changing admitted results.
- Report `disabled`, `notConfigured`, `starting`, `ready`, `degraded`, `unavailable`, `timeout`, `invalidResult`, `lowConfidence`, `staleAuthority` and `noEligibleModel` distinctly in diagnostics/UI. Log only IDs, timings, engine/policy versions, sanitized code, count and selection basis—never sensitive decision state.
- Use one error/telemetry vocabulary with existing `RuntimeTelemetry`/`OperationalDiagnostics`; no second logging platform or global dashboard. Never retain private text in a benchmark fixture without permission.

**Availability matrix:**

| Situation | Required behavior |
| --- | --- |
| Decision feature disabled; explicit fixed/default policy | Existing deterministic admitted execution works; no sidecar call |
| Auto selected; Kev healthy | Filter → rank → validate → re-authorize → pin new Run |
| Auto selected; Kev unavailable/timeout/low confidence | Use *only* explicitly eligible configured default when fallback policy allows and visibly record reason; else refuse new Run safely |
| Explicit fixed selected but model unavailable | Preserve selection and show repair; **never** Auto-replace |
| Route/credential/IAM changed after score | Reject stale outcome and retry **only pre-admission**, bounded; no effect dispatch |
| AgentRun already admitted; Kev restarts or changes model | Existing immutable Run binding and its retries/continuations unchanged |
| Initiative gate unavailable | Use documented initiative policy, not an unsolicited speak caused by a missing score |
| Event semantic gate unavailable | Retain durable pending/indeterminate outcome; never silently mark delivery matched or filtered |
| Authorized LLM adapter requested before Run | Return `unsupportedScope` (or reject config), never create an ambient privileged provider call |

---

## 10. Public API, Admin UI and UX quality

### API ownership

Reuse `ModelManagementEndpoints`, `ModelManagementMapping` and the same tenant IAM/revision semantics. Add narrowly typed routes for **routing settings and diagnostics**, e.g. a read/update tenant routing-policy endpoint and an authenticated, operator-only engine-status read. Avoid `/decide` public inference endpoints or accepting arbitrary candidate text from a browser. Any preview simulation must be explicitly authorized, bounded, rate-limited and contain no private data by default. API contracts are closed DTOs in Contracts, mapping in Api, validation/permission in Application, storage in existing persistence. Reconcile stale revisions before save; display errors without leaking endpoint secrets.

### Where controls live

- **Organization → AI models → Routing** beside Connections / Models / Defaults. Show Auto enablement, supported objective, candidate eligibility summary, deterministic fallback policy, current backend readiness, sampled decision quality/latency when measured, and explanations of unknown cost data. No duplicate tenant defaults UI.
- **Chat `ModelPicker`**: visible, distinct **Auto** choice; separate **Use inherited default** and explicit named fixed offerings. Effort control distinguishes `Auto` and `Model default` if model capability allows; when Auto is active, show **“Auto · last Run used [model, connection, effort]”** as observational fact, not a binding promise for future Runs.
- **Definition / Instance / Automation**: extend shared `ExecutionModelFields` (or justified shared owner) with the same inherit/fixed/auto semantics, preview of priority and constraints. If a higher-level fixed selection wins, tell the user why Auto is not in effect.
- **System administration**: compact operator-only backend configuration/status information consistent with existing System surfaces; host-owned endpoint/key remain in host configuration, not editable as tenant provider records. Offer actionable “service not configured” / “model loading” instructions without exposing secrets.
- **Run details / diagnostics**: show `Fixed`, `Inherited`, `Auto ranked`, `Auto default used`, engine/model version, policy revision and safe reason. Do not create a new decision Activity feed; integrate with current Agent Run detail and audit where justified.

### UI states and design-system pass

Use Ant Design v6, existing `AdminShell`, `AdminCollectionToolbar`, `useTenantCommand`, access feedback, forms and responsive drawer patterns. Specify and verify first-run, disabled, unconfigured, loading, model warming, ready, degraded, no candidates, stale settings, revoked permission, unavailable fixed selection, failed save, unknown operation outcome, concurrent change, and recovery. No misleading confidence bars, fake prices or automatic invisible reassignment. Preserve keyboard/focus return, screen-reader status text, input labels, error associations, 200% zoom, light/dark compatibility, long labels/URLs and reduced motion.

**Visual quality workflow:** prepare a concise IA + interaction brief **before UI code**, then implement once, inspect desktop 1440/768 and mobile 390/320 CSS px, fix a batched set of issues, confirm once. Reuse `.agents/context/DESIGN.md`, `.agents/context/PRODUCT.md`, `.agents/skills/impeccable` and existing `.impeccable/surfaces`. Update the documented design system only where shared components/patterns actually change. Polish entire route transitions and user journeys, not one isolated screenshot.

---

## 11. Configuration and deployment contract

Preserve the 26 October canonical configuration rule: **PascalCase `__` segments for .NET options**, **UPPER_SNAKE_CASE for launcher/Compose/external process variables**; no duplicate synonyms, aliases or hidden key precedence.

**Illustrative .NET-hosted settings (exact names to be frozen in the implementation):**

```dotenv
DecisionIntelligence__Enabled=false
DecisionIntelligence__Backend=LocalKev
DecisionIntelligence__Endpoint=http://127.0.0.1:8009
DecisionIntelligence__ModelId=jaredpalmer/kev-4b@v1.0
DecisionIntelligence__RequestTimeoutMs=500
DecisionIntelligence__MaxConcurrentRequests=4
DecisionIntelligence__MaxStateCharacters=8000
DecisionIntelligence__ShadowMode=true
```

`Backend=LocalKev` while `Enabled=false` is inert. These are **proposed examples, not measured safe production defaults**; settle bounds through the warm-model benchmarks and current startup validators. Do not publish the internal API key in the repository or Vite env; the app receives it through its existing host/private secret mechanism (e.g. a reserved `DecisionIntelligence__ApiKey` option populated only from private operator input), while the Kev process receives `KEV_API_KEY`. Do not treat the Kev credential as a tenant inference Credential or Environment variable.

**Compose:** add a separate, opt-in `docker-compose.decision.yml` exposing **no public ports by default**; app-to-service internal name is resolved by the Compose overlay, native Real dev uses an explicitly allowed local URL. `scripts/compose.py` validates its env contract. Extend `scripts/dev-real.sh start|restart|stop|status` with an explicit `--with-decision` or equivalent and include it in an intentionally comprehensive `--full` preset **only when resources are available and enabled**, with transparent GPU/cold-start preflight. Existing OpenSandbox launcher, nopCommerce and native development paths continue to operate without Kev. No sidecar installation in the main .NET `Dockerfile`, no Python runtime in the app image, no model downloads at default startup.

Record operator setup, GPU/CPU prerequisites, offline download, readiness, restarts, expected first-run latency, resource limits, backup behavior (none for stateless inference beyond configured model cache), and recovery in `docs/17-observability-and-operations.md` and `docs/host-configuration-reference.md`.

---

## 12. Implementation plan — complete vertical slices

### Phase A — Freeze contracts and migration inventory

1. Re-read `AGENTS.md`, current canonical docs and latest `main` before touching files. Lock actual code SHA and identify current selection/Run/automation/UI API call sites, including startup, Synthetic and host profile handling.
2. Approve the new decision purpose/shape, backend scope restrictions, definition of Auto, explicit deterministic unavailable policy, cost-metadata unknown behavior, and current ownership. Record exception for **one optional inference sidecar** in architecture/technology decisions.
3. Inventory live model preference/selection JSON, current/latest agent definitions, Instance sparse fields, Automations, Session snapshots, existing and interrupted Run pins, and migration preflight/backup commands.
4. Write focused acceptance scenarios first; create a migration refusal fixture and Synthetic decision fixture. Do **not** design a full task-priority system at this stage.

### Phase B — One decision boundary and real local adapter

5. Add the smallest typed request/result contracts, result validator, cancellation/concurrency policy and caller-facing `IDecisionEngine` in Application; backend registration belongs in Infrastructure.
6. Build secure `LocalKevDecisionAdapter` with a pinned schema and build, strict HTTP/auth/endpoint policy, model-version witness, health/readiness, explicit bounded failure status and no internal automatic LLM re-try.
7. Add the **authorized Run-only** LLM adapter by reusing current `ILanguageModelResolver` and a trusted invocation. Where no authorized Run exists, refuse, not bypass.
8. Add deterministic Synthetic fixtures and metrics; optional Compose overlay, native launcher, docs and Synthetic-off startup pass.

### Phase C — Auto selection / full preference cutover

9. Implement canonical `inherit`/`fixed`/`auto` preference, reasoning intent, source precedence and safe configuration migration. Keep active/historical Run bindings immutable.
10. Wire foreground accepted input/attachment admission and unattended occurrence admission into the **same** candidate filtering/routing policy without duplicate execution dispatch. Ensure reasoning efforts, vision, Tools, connection and IAM fences still execute.
11. Capture outcome/provenance at the existing immutable Run boundary, including repetition, cancellation, provider retries, approval successors and host restart. Remove old mutable preference shapes and all superseded endpoint/UI branching in the same cutover.
12. Expose tenant Routing settings + effective Chat/Definition/Instance/Automation controls and Run inspection. Ship entire authenticated tenant end-to-end flow and user-visible unavailable states.
13. Benchmark shadow versus effective Auto, reject unsafe decisions, measure added latency/quality/cost, qualify default policy and then enable live Auto by deliberate tenant opt-in. **Only now** declare S1 + S2 implemented.

### Phase D — Other current owners, without speculative systems

14. Add scoped Skill/tool ranking and context/continuity relevance where candidate sources are already authorized; preserve hard budgets and precedence. Delete any displaced ad hoc intelligent caller logic while retaining legitimate deterministic baselines.
15. Add optional initiative gate; preserve incumbent initiative planner, voice/UI caps, lifecycle and cancellation. No parallel proactivity workers.
16. Add explicit opt-in semantic event relevance **after** deterministic event expressions, using existing durable decision/recovery/dispatch. No second event scheduler or transient invisible verdicts.
17. Add configuration advice projection if it measurably improves existing assisted authoring; it is advisory only. Record Work-priority integration as deferred until the relevant owning initiative exists.

### Phase E — Migration closure and polish

18. Run exact-source backend, API, persistence, front-end and Synthetic host journeys; perform optional actual Kev GPU integration smoke **only when infrastructure is provisioned**. Qualify error/cancel/restart/revocation/race cases, not just happy paths.
19. Apply one bounded full UX polish pass with responsive and keyboard checks. Delete dead flags, old config values, stale DTOs/tests, duplicate instruction/decision code, placeholders and obsolete routes.
20. Synchronize canonical docs and local verification ledger; audit removed/retained inventory; clearly separate local Synthetic proof, actual Kev inference proof, real hosted-provider UAT, deployment and optional CI. Stop once acceptance passes—avoid speculative refactoring.

---

## 13. Testing and verification matrix

All tests should assert **observable semantics** and prove the current code paths, not just mocked interfaces.

| Level | Required proof |
| --- | --- |
| Domain / Application | Typed probability validation (NaN, infinite, out-of-range, missing/duplicate/unknown options, low confidence); stable choice; no random candidate; explicit defaults and effort precedence; capability and budget hard gates; explicit fixed cannot be displaced |
| Model security | Foreign tenant/Agent/Space choices never appear; stale offering/credential/security revision refuses; revoked permission between ranking and admission refuses; unsafe rich data never crosses to Kev; LLM adapter cannot use ambient provider credentials before Run |
| Session / Run | Text, voice, image attachment, unattended Automation, manual signal and continuation paths; Auto binds **once per new Run**; same-Run retries and approvals retain exact route; cancel/stale evaluation is ignored; model preference changes cannot mutate active work |
| Infrastructure / SQLite | Fresh/offline migration; backed-up predecessor snapshot; invalid old schema refusal; foreign key/reference integrity; restart reopen; InMemory parity; no alias readers/writers; clean initialized profile with no Kev or external provider keys |
| Local service | Pinned Kev HTTP payload/output; real checkpoint identity; missing/invalid bearer, network denial, redirect, response oversize, GPU warmup, saturation, timeout, crash/restart, bounded cancellation; no public port or accidental Docker.sock mount |
| Relevance | Tool/Skill access and required context remain; ranked Evidence never accesses unrelated sources; Core instructions unchanged; initiative gate never directly speaks without plan; event expression remains authoritative and indeterminate events follow durable recovery |
| API / UX | Tenant/role/permission variations, no cross-tenant reads, CAS stale conflict, saved preferences, loading/empty/error/recovery, keyboard/zoom/responsive/long text, actual Chat Auto → resolved Run, cancellation and honest fallback explanation |
| Metrics / decision quality | Labelled representative tasks including multilingual requests, long/ambiguous prompts, code, tool-heavy, small talk, vision and missing-evidence cases; deterministic baseline comparison, calibration/reliability, per-case false positives, option-order sensitivity, p50/p95 incremental warm latency and overhead |

**Go/no-go gates** (proposed, to be frozen from baseline measurements):

- **Zero** permissions/route/credential bypass, silent fixed-selection replacement, duplicate Run/effect creation, unbounded model requests, unreported loss of an event, and cross-tenant leaks.
- **100%** of accepted Auto decisions have an eligible exact pinned offering and valid effort, both in new Runs and across restart/retries. An invalid/adverse decision either takes the **documented safe default** or explicitly refuses.
- A real Kev checkpoint passes **a separately labelled, opt-in** System One integration smoke; no pretend GPU acceptance from mocks.
- Live Auto is only enabled where it is **at least non-inferior to existing deterministic policy** on the selected held-out distribution and delivers a stated tradeoff (quality, latency, cost or context saving), with confidence/error analysis. Where data is too weak, keep Auto opt-in/shadow; do not disguise it as ready.
- Measured warm p95 added decision latency and infrastructure footprint meet a **recorded operator-approved budget**. A 500 ms example timeout is not a performance claim.
- Frontend typecheck/lint/unit, focused relevant .NET test projects, Synthetic WebApplicationFactory/integration, Playwright real actions, Compose configuration and startup validations pass. CI/hosted/customer UAT are separately opt-in under `AGENTS.md`.

### Suggested existing tests to extend

`tests/AgentCore.Application.Tests/ExecutionModelPolicyTests.cs`, `InitiativeEvaluatorTests.cs`, `ContextAssemblerTests.cs`, `DynamicSkillActivationTests.cs`, current Run/admission/capability tests; appropriate Infra/Api IAM/model tests; `web/e2e/tenant-model-management.spec.ts`, `web/e2e/session-model.spec.ts`, `web/src/features/chat/ModelPicker.test.tsx`, `web/src/features/admin/ExecutionModelFields.test.tsx`, and `scripts/tests/test_compose.py`. New tests should be organized beside actual owning components, not in a parallel test harness.

---

## 14. Clean cutover inventory: remove versus retain

**Remove/replace in affected scope**:

- Direct *independent* intelligent relevance/routing calls that bypass `IDecisionEngine`; any obsolete ad hoc decision request/response schema once its consumer is migrated.
- Mutable model-selection alias fields and UI inference that treats `default`, missing ID or blank effort as Auto. Retire superseded Contracts DTOs, source parsers, persistence mappings, API shapes and migration-time adapters together.
- Duplicate fallback cascades (local → arbitrary hosted LLM, hidden system model, smallest model, first catalog item), keyword-only “Auto” inference, extra global primary-model configuration, mock production results and deprecated `DecisionIntelligence` setting names.
- Any new temporary controller/service introduced during implementation that would duplicate Session/Run, model catalog, event delivery, configuration approval or tool/Skill ownership.

**Must remain**:

- `Session → Activation → AgentRun`, mailbox owner, current execution and response supersession, immutable Run/route/approval/checkpoint provenance, and live tool/voice cancellation behavior.
- Deterministic IAM, current exact model-route authority and credential store; official provider adapters, tenant model offerings, authoring and explicit tenant default.
- Existing deterministic expression Event filter + durable Event recovery, Tool authorization, on-demand Skill admission, memory/continuity/source access and their proven budgets.
- Current Synthetic profile without GPU/provider keys, OpenSandbox as a separate capability, canonical Compose/native configuration validators, and immutable historical evidence (or explicit operator-approved reset where source conversion is impossible).

**No stale references allowed:** scan source, tests, docs, JSON definitions, UI labels, `.env.example`, host config reference, launch scripts, Compose overlays, OpenAPI/DTOs and legacy aliases after cutover. Historical migration files and verification reports can remain **as immutable records**, not active execution mechanisms.

---

## 15. Documentation ownership and completion evidence

Update canonical owner documents **only for actual shipped behavior**:

- `docs/03-system-architecture.md`: shared decision boundary, one optional inference sidecar, authorization ownership, Run admission point.
- `docs/04-backend-interfaces.md`: request/result, capability, provider boundaries, failure semantics.
- `docs/10-technology-decisions.md`: permitted optional inference-service exception, Kev version/model decision, why no parallel engine.
- `docs/14-api-and-realtime-protocol.md`: updated model preference/Auto/diagnostic DTO semantics, permission and revision rules.
- `docs/15-persistence-and-configuration.md`: new settings and migration, existing Run/event provenance rules, no alias fields.
- `docs/16-testing-strategy.md`: isolated Synthetic + opt-in real model qualification and decision benchmark gates.
- `docs/17-observability-and-operations.md` and `docs/host-configuration-reference.md`: optional service, runbook, security, failure/health, env naming and rollback.
- `.agents/context/DESIGN.md` and relevant `.impeccable/surfaces` for the final UI integration patterns; `docs/18-implementation-plan.md`, `TODO.md`, `README.md` only for necessary status/architecture sync.
- A single `docs/reports/decision-intelligence-delivery-2026-10-26.md` (date of actual delivery, not fabricated today) recording **exact code SHA**, change inventory, migration outcome, full focused acceptance, measured latency/quality, real-GPU status and remaining deployment/UAT limits.

Implementation report format should use the project review checklist: **Overall verdict**, high-value findings with concrete file/line/effect, implementation assessment distinguishing tested from assumed, and smallest final recommendation. Once no material issues remain, stop polishing and proceed to initiative 7.

---

## 16. Decisions to hold constant for unattended implementation

These are the final defaults for the implementation agent; do not interrupt for minor design clarifications:

1. One decision abstraction in Application; no new Agent Instance/engine/database/queue. Optional Kev is a host-operated inference adapter only.
2. Kev-4B pinned; default disabled; explicit operator choice of smaller alternative; no hidden backend failover.
3. Initial complete product: Smart Routing + Auto reasoning for both foreground and unattended, with honest UI and immutable Run pin. Subsequent relevance/gates are owned bounded slices, not placeholders.
4. Fixed selections and policy limits take precedence over Auto; models must pass hard authorized candidate checks; absent cost/latency metadata is **unknown**, not zero.
5. Auto on unavailable/uncertain engine may use **only** a known eligible tenant/definition default when that option has been explicitly enabled; otherwise reject with a helpful repair path.
6. Saved selection **intent** differs from concrete effective Run selection. Never reroute a running/retried/resumed Run; explicit per-Run model route witness remains canonical.
7. Engine outputs never authorize an effect, account, secret, read, tool, event delivery or permission. Explicit IAM and approval owners cannot be bypassed.
8. LLM adapter is admitted-Run-only unless a future separate approved authority contract exists; do not improvise a privileged pre-Run LLM call.
9. Event expression filter remains deterministic; initiative generative plan remains with the existing agent/LLM path. Relevant legacy intelligent evaluator code is migrated where appropriate, not retained beside a new independent intelligent stack.
10. Fully replace affected persistence/API/frontend/config representation in one clean cutover with preflight/backup/refusal/reset, no runtime aliases, no legacy compatibility, no duplicate state owners.
11. Synthetic remains GPU/key-free; local model is opt-in with bounded operational impact. Genuine inference and deployment acceptance must be measured explicitly.
12. Favor the smallest complete implementation satisfying these rules. No framework building for unimplemented Goals/Delegation/Group Chat.

**Implementation-readiness verdict: Ready for implementation as a staged, owner-preserving cutover.** The current code base has the necessary tenant/model/Run foundation; the important work is correct admission timing, source-of-truth migration, secure local inference, and demonstrable runtime/UI acceptance—not another platform layer.

---

## 17. Source references

### Repository (inspected at baseline)

- [Repository `main` commit](https://github.com/trannamtrung1st/agent-core/commit/aab065eaabe8be27d5fd0480e1e8cdccae76b468)
- [Roadmap / TODO](https://github.com/trannamtrung1st/agent-core/blob/main/TODO.md)
- [Agent guidelines](https://github.com/trannamtrung1st/agent-core/blob/main/AGENTS.md)
- [Architecture](https://github.com/trannamtrung1st/agent-core/blob/main/docs/03-system-architecture.md)
- [Model route authority](https://github.com/trannamtrung1st/agent-core/blob/main/src/AgentCore.Application/Models/TenantModelRouteAuthority.cs)
- [Session model binder](https://github.com/trannamtrung1st/agent-core/blob/main/src/AgentCore.Application/Models/SessionModelBinder.cs)
- [Unattended model policy](https://github.com/trannamtrung1st/agent-core/blob/main/src/AgentCore.Application/Models/ExecutionModelPolicy.cs)
- [Current model contracts](https://github.com/trannamtrung1st/agent-core/blob/main/src/AgentCore.Domain/Models/ModelManagement.cs)
- [Current initiative evaluator](https://github.com/trannamtrung1st/agent-core/blob/main/src/AgentCore.Application/Agents/InitiativeEvaluator.cs)
- [Current deterministic event filtering](https://github.com/trannamtrung1st/agent-core/blob/main/src/AgentCore.Infrastructure/Events/RestrictedEventFilter.cs)
- [Current model-management delivery report](https://github.com/trannamtrung1st/agent-core/blob/main/docs/reports/dynamic-model-management-delivery-2026-10-25.md)

### Decision-model upstream

- [Kev repository and pinned serving/API guidance](https://github.com/jaredpalmer/kev)
- [Kev-4B model card, license, performance and limitations](https://huggingface.co/jaredpalmer/kev-4b)
- [Kev-0.8B alternative](https://huggingface.co/jaredpalmer/kev-0.8b)

**Evidence note:** This document is based on **static inspection of GitHub files** and upstream model documentation. No local Agent Core tests, running native/Compose host, customer data migration or real Kev inference were executed while authoring it. Preserve that distinction in the implementation report.
