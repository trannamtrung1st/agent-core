# Tenant workspace direct effects — 2026-10-19

This successor to `a837585e` connects owned workspace mutation primitives to the existing claimed Run side-effect state and immutable operation receipt store. This locally qualified direct-Allowed boundary is a prerequisite for exact mutation Review/exceptional grants, which remain open; no full-proposal acceptance is claimed.

## Contract and qualification

Closed bounded mutation arguments use exact absolute home/working child paths. Write/patch/mkdir/copy/move/delete/ordered batch derive their source reads, destination writes, delete scopes and parent reads from typed arguments. Independent current Agent IAM, requester policy, admitted/current capability, HomeAccess, actual Session/Instance lifecycle and claim fences govern dispatch. Read permissions are never borrowed or delegated by a mutation request. Policy-required operations return no dispatch.

The existing Run external-effect origin retains exact direct operation/call/tool/arguments/hash and Instance/persona witnesses. Current authority is reevaluated at commit callbacks. Start, security revision and dispatch audit are atomic; filesystem I/O runs outside that transaction. Factual settlement writes the existing immutable receipt/Run/audit transaction. Completed receipt reuse returns only body-free acknowledgement. A failed/abandoned dispatch is uncertain unless nonapplication is known; recovery records uncertainty without repeating filesystem I/O. No new scheduler, effect engine, file ledger or permission snapshot is introduced.

Representative cases execute real write → patch → mkdir → same/cross-root copy in both stores and both roots, reuse exact receipts without repeat, inspect actual bytes/empty directories and prove sensitive delete remains undispatched under mandatory Review. The initial build found a missing execution namespace import; that was corrected before functional qualification. Executed outcomes and corrections are recorded below.

No live/user data reset or installed migration, provider call, CI/full suite, UI redesign or deployment is authorized or performed. Exact durable mutation Review/grants, remaining Workspace/Office/publication/autonomous families and final authority/schema cutover remain part of the active goal.

## Qualification corrections

The first representative both-store/home-working write → patch → mkdir → copy/cross-copy run passed all four cases before schema enforcement was added. A test compile issue was corrected by retaining the typed fake clock in expiry scenarios. The initial check-constraint migration exposed an existing cross-table trigger dependency: EF's SQLite AgentRuns rebuild invalidated Artifact ownership triggers. That uncommitted migration was removed and replaced by `TenantWorkspaceDirectEffects` insert/update validation triggers, leaving AgentRuns and its references in place. No installed application database was touched.

The first trigger-enabled run could not start the .NET runner's loopback control socket under the sandbox; the same compiled tests ran with local socket permission. Eleven of thirteen cases passed. The two failures found a recovery query comparing SQLite's uppercase GUID column with lowercase JSON operation identity. The query now normalizes the column with `lower`, so an existing immutable receipt excludes that operation from further recovery passes. This was repeated selection, not repeated filesystem dispatch. Requalification is required below before closure.


The first complete hosted model qualification passed eight of ten cases: both stores/roots executed write → patch → mkdir → copy → ordered batch, returned body-free receipts, refused mandatory delete, and retained completed receipts/bytes across SQLite application-host recreation. Missing write IAM and configured Always policy produced no dispatch. Both lost-acknowledgement cases exposed a missing closed diagnostic token: `workspaceOutcomeUnknown` was rejected while constructing the response failure, leaving the response nonterminal. The bounded token was added to the existing allowlist; the regression requires actual terminal failure, the persisted response reason, retained written bytes and exactly one dispatch/result pair. This result remains pending requalification below.


Both-store effect requalification now passes **15/15** cases (`/private/tmp/agent-core-workspace-effects-final-store.log`). The added receipt-unavailable case reserves and writes actual bytes, forces all four settlement attempts to return unavailable, returns explicit uncertainty with the Core operation ID, blocks a duplicate dispatch, then recovers one uncertain receipt after claim expiry with zero second native calls. Current dispatch facts, not exception text or assumed failure, determine replay safety.

The diagnostic correction made both lost-acknowledgement model responses terminal. A subsequent HTTP run passed **18 existing workspace read/Review regressions** and eight allowed/refused mutation cases, but four new assertions incorrectly expected a safe error message in assistant text. Public history deliberately keeps failure detail separate from assistant output; these assertions now inspect the separate `failure.failureReason`, failed status, conservative Run failure and empty assistant text. No file/receipt assertions were weakened. Final mutation/Artifact requalification is recorded below.


Final mutation/Artifact qualification passed **18/18** cases (`/private/tmp/agent-core-workspace-effects-http-qualified.log`): twelve mutation scenarios and six existing model Artifact publications. Separate current IAM/policy refusals, actual final bytes, audit cardinality, terminal lost acknowledgement/receipt-unavailable outcomes and completed SQLite host recreation all matched their expected observations.

Focused Application checks then passed 128/130 cases and exposed inconsistent validation exceptions for protected roots/traversal. The incumbent filesystem path/structure validator throws `AgentCoreException`; allowing that to escape the new parser would bypass the tenant tool's normal bounded `invalid_arguments` receipt. The new parser normalizes only its local validation exceptions to safe `ArgumentException`. Hosted malformed-call cases require no native effect and ordinary model continuation, rather than a failed response. The same closed diagnostic review also found the existing sibling `sandboxOutcomeUnknown` token missing from the allowlist; both uncertainty tokens now have safe serialization coverage. No raw provider/file exception text is admitted.


## Final local qualification

- **130/130 Application cases passed:** closed/canonical mutation arguments and UTF-8 bounds; independent source/destination/delete requirements; nested schema closure and batch limits; tenant capability/origin projection; existing tool policy/structure, safe Sandbox summaries and durable Run execution. Log: `/private/tmp/agent-core-workspace-effects-application-qualified.log`.
- **14/14 final hosted mutation journeys passed:** actual cookie-authenticated HTTP intake, production Run worker and model tool loop, canonical home/working writes/patches/directories/copies/batches and exact final downloaded bytes. Both stores refuse mandatory delete, missing IAM and configured Always policy without a dispatch/request. Lost acknowledgement and unavailable receipt settlement persist terminal uncertainty with body-free failure detail, retain actual written bytes and do not continue mutation. Protected-root malformed input returns bounded `invalid_arguments` and normal model continuation with no file effect. Completed receipts and bytes survive actual SQLite application-host recreation, with no new dispatch. Outbound provider HTTP attempts remain zero. Log: `/private/tmp/agent-core-workspace-effects-http-final.log`.
- **24 existing hosted regressions passed:** eighteen workspace read/Review journeys in the earlier mixed 30-case run (whose four new history assertions were corrected and requalified above), plus six model Artifact publication cases in the fully passing 18-case run. Logs: `/private/tmp/agent-core-workspace-effects-final-http.log`, `/private/tmp/agent-core-workspace-effects-http-qualified.log`.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_effects' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_model_mutations_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Application.Tests --no-restore --filter 'FullyQualifiedName~TenantWorkspaceMutationContractTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~WorkspaceStructureToolContractTests|FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~SandboxFailureSummaryTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```



The final shared effect regression passed **59/59** cases (`/private/tmp/agent-core-workspace-effects-shared-regression.log`): the fifteen workspace cases plus existing Browser dispatch/revocation/uncertainty/claim/cancellation/concurrency/retained-owner recovery, Artifact direct publication authority/rollback and SQLite Artifact owner integrity. This confirms the optional direct origin metadata and non-rebuilding guards preserve the shared effect/receipt owners.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_effects|FullyQualifiedName~Tenant_Browser_effect_|FullyQualifiedName~Artifact_publication_|FullyQualifiedName~Owned_Artifact_SQLite_reopens' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```


A final retained-owner check passed **2/2** cases (`/private/tmp/agent-core-workspace-effects-deleted-owner.log`): commit a dispatch reservation, cancel its Run, delete the actual Session, confirm the ordinary live Run reader masks it, then recover exactly one uncertain receipt through the retained owner. No native file call occurs and the second recovery pass returns zero. No source adjustment was needed.

**64/64 Domain cases passed** (`/private/tmp/agent-core-workspace-effects-domain.log`): shared Run claims, cancellation, retry/uncertainty/terminal fencing, effect preservation and diagnostic reference serialization, including both bounded uncertainty reasons. Final distinct focused coverage is **293 passing cases**: 64 Domain, 130 Application, 61 Infrastructure (59 shared + two retained-owner) and 38 HTTP (fourteen mutation + eighteen workspace read/Review + six Artifact publication). This count excludes repeated runs and corrected failed assertions.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_effects_deleted_Session' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Domain.Tests --no-restore --filter 'FullyQualifiedName~AgentRunContractTests|FullyQualifiedName~FailureReferenceTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

Changed-document links/fragments and balanced fences passed across eleven Markdown files; `git diff --check` passed. Current architecture, ports, protocol, persistence, testing and implementation status are synchronized. Earlier reports remain historical evidence for their own versions; top-level successor notes identify precisely what this checkpoint supersedes.

## Limits and remaining scope

No live/paid provider, user/installed database, Real host, application deployment, CI or full suite was used. No frontend source changed; this checkpoint does not claim new browser/visual acceptance. Crash-boundary evidence uses controlled saved abandoned reservations and native lost acknowledgement injection; completed SQLite host recreation is actual. A process killed inside an unfinished native tree copy was not exercised. Ordinary concurrent filesystem writes retain incumbent semantics; batches/copies can leave preparation/partial effects and must not auto-replay.

Exact mutation Review/exceptional grants remain required, including decision-time source witnesses, compound deciding-human authority, same-root refusal suppression and durable approved dispatch/result recovery. Policy-required mutation tools currently return no dispatch; this checkpoint does not claim those protected effects work. Remaining virtual resource/Office/source publication, autonomous origins, other proposal families and final authority/schema consolidation stay open. This is a bounded implementation checkpoint, not full proposal or milestone acceptance.
