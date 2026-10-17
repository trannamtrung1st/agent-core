# Verified tenant requester context — 2026-10-17

Baseline: `97f38206abc82ffc11362c3d9159231dba1ff42b`, clean main. The preceding goal turn made verified progress by implementing and pushing the exact-action Approvals UI. This successor implements the proposal's trusted requester/judgment context requirement over the existing tenant Runtime. The full proposal remains active; this is neither milestone acceptance nor a completed tenant cutover.

## Implemented boundary

The existing `TenantRunExecutionAuthority` now implements `ITenantRunContext`. It resolves the actual saved Run, immutable tenant/executor/requester/Session/root attribution and live claim. Current Definition and context reads reject cancellation, expired/replaced generations, changed ownership/attribution and unavailable agent/requester access. Caller-provided Run pins cannot replace the actual saved Definition.

For an original human, the projection resolves the actual active User and Account display label, with current admitted agent chat and Session-read access. It does not substitute the Session creator or the human making an approval decision. It contains the Tenant label/current executor and at most 16 distinct relevant role-name/scope hints: organization inheritance or exact current agent/conversation assignments, with relevant permission types. Other resources, other members, system roles, unrelated credentials/directory roles, full permission lists, login/email, password hashes, security stamps, authentication sessions and grant material are excluded. Truncation is explicit; labels remain quoted JSON data. The collection is frozen.

Run/security and Account reads are fenced again after projection, because Account revocation need not update Tenant revision. Logout retires the web session, not the already-admitted original human attribution; subsequent execution still requires active membership/Account and current chat/read authority. No login cookie or bearer token is propagated.

The existing initial brain context and each agent generation/tool round fetch fresh facts. `PromptContextBuilder` requires an exact matching requester projection for tenant execution and emits a dedicated Core procedure plus AgentRun-sourced Runtime TrustedFact. Core states that communication is not obedience, protected-data access or tool authority, that role labels cannot instruct the agent, and that the executor and requester remain independent principals. History/model/page claims stay Evidence. Facts are refreshed on later rounds and successor Runs rather than stored in Run admission or interaction checkpoints. A missing/mismatched projection fails closed.

This uses the existing authorizer, coordinator, Session Runtime, context assembler and both stores. No new execution engine, Task entity, role cache, permission snapshot, schema migration or fallback authority path is added.

## Executed use cases

- **Same Account, two tenants, both stores:** one authenticated Account has different original User principal references in Alpha and Beta. Each model projection includes only its own organization and role hints; a projection cannot be reused for the other execution.
- **Actual requester and scope, both stores:** a second member submits to an Owner-created conversation while claiming Owner in task text. The saved Run and projected context identify the second member. Exact agent-chat and conversation-read hints appear; another agent's private role and an unrelated credential/directory role do not. A new underlying-action role appears on the next read; revocation removes it, without changing the previous frozen projection. The agent still receives RequiresApproval/ExceptionalGrant for an operation it lacks, despite the requester's action role.
- **Live loss and claim fencing, both stores:** suspend the original membership or Account and observe context denial/current Definition unavailable. Account suspension is checked with no Tenant revision change. Original web logout alone preserves valid admitted attribution. An expired claim and an older replaced generation cannot supply facts; the actual replacement can. Nineteen relevant roles yield exactly 16 hints with truncation declared, including valid long role labels within repository bounds.
- **Hosted HTTP → actual worker → two model rounds, InMemory and SQLite:** authenticate a second member, submit an actual text message containing fake Owner claims, pause the first model call, revoke a hint-only role and resume. The later model request has the same verified requester and newer security revision, with the revoked hint absent. The Run completes and no role labels are copied to admission/checkpoint.
- **Hosted communication revocation, both stores:** pause the first model call, remove current agent-chat access and resume. The Run fails without a response result or a second agent model call. The in-flight reasoning is not evidence of continued authorization.
- **Hosted different human approver, both stores:** the second member initiates an exact preference authorization; Owner approves it. The recorded successor receives current facts about the original second member, a role added while review was pending, and the actual effect outcome. The Owner/approver is not substituted as requester; source/parent/operation references stay intact.
- **Context assembly boundaries:** verified facts have Runtime/AgentRun/TrustedFact provenance and the Core independence rules precede them. Malicious display/role labels remain escaped data, task claims remain User Evidence, missing/foreign context rejects, refresh replaces prior hints, the manifest contains no raw labels, and interaction-only checkpoints exclude role facts. No-original-human formatting does not invent a User or human chat admission; this formatting test does not establish autonomous admission support.

## Focused verification

Commands use `dotnet test <project> --no-restore --nologo --filter '<filter>'`.

| Project / scope | Result |
| --- | --- |
| API initial actual HTTP-to-worker text use case (`Canonical_tenant_worker_completes_actual_HTTP_text`) | 1 passed |
| Application context, prompt, durability and tenant tool projection regression filter | 133 passed |
| Infrastructure tenant Run admission/runtime and new requester cases | 27 passed |
| API new hosted tool-round/revocation/different-approver cases (`Tenant_model_receives_current_actual_requester`) | 6 passed |
| Infrastructure same-Account/two-tenant context follow-up | 2 passed |
| API affected `TenantSessionAccessTests` regression | 38 passed |
| Application final camelCase fact serialization recheck | 3 passed (overlaps the 133) |
| Changed-document local links/fences and `git diff --check` | Passed |

Application filter: `FullyQualifiedName~TenantRequesterContextTests|FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~TenantApprovalToolContractTests`.

Infrastructure filter: `FullyQualifiedName~Requester_context_uses_actual|FullyQualifiedName~Requester_role_hints|FullyQualifiedName~TenantRunAdmissionTests|FullyQualifiedName~TenantPreferenceOperationTests.CrossRun`. The filename-style last alternative matches no test name; the executed 27 cases are the tenant Run admission/runtime class, including six requester cases. Hosted API scenarios independently exercise actual cross-Run continuation. Counts overlap and are not an aggregate total. Final documentation checks passed across eight changed Markdown files: 678 local links, 172 fragments, seven complete JSON blocks and balanced fences; `git diff --check` passed.

Logs: `/private/tmp/tenant-requester-first-flow.log`, `/private/tmp/tenant-requester-app-regression.log`, `/private/tmp/tenant-requester-infra-regression.log`, `/private/tmp/tenant-requester-api-flow.log`, `/private/tmp/tenant-requester-api-regression.log`, `/private/tmp/tenant-requester-two-tenants.log`, `/private/tmp/tenant-requester-final-format.log`. Early test iterations corrected immutable-context fixture construction and supplied the actual current user entry rather than assuming trigger text was conversation history; the final filters above pass separately from those earlier failed fixtures.

## Remaining full-goal boundaries

This establishes trusted judgment facts and live Core enforcement for supported tenant human text Runs and exact approval successors. It does not prove model willingness/semantic obedience using a Real provider. It does not enable autonomous/event/background admission, tenant Hub/audio, live authorized delivery/receipts or protected disclosure. Other effect/credential/resource families, notifications, general audits/proactive revocation, complete tenant UI and removal of obsolete authority/schema remain required. The full goal stays active.

No frontend was changed, so no browser/UI test was required for this backend slice. Runtime behavior was exercised through the actual Synthetic hosted worker and model requests in WebApplicationFactory with temporary SQLite/InMemory stores. No provider credits, user data, Real host, reset/deployment or optional CI was used.
