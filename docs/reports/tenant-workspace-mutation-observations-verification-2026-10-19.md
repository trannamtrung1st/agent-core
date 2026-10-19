# Tenant workspace mutation source observations — 2026-10-19

This continues the full proposal from direct mutation checkpoint `20ee390f`. The current step supplies the private exact source/target boundary required by durable mutation Review and one-time grants. The approval flow itself remains in progress; this report does not redefine the full goal or claim protected mutations are available.

## Ownership and contract

`ITenantWorkspaceMutationObservationStore` is a private facet of the existing `FileAgentInstanceWorkspaceStore` and canonical filesystem. Core derives closed probes from actual tool arguments: write/patch follow their resolved logical file route; structural sources/targets keep unlinked identity, with full recursive source coverage where needed. Existing no-follow descriptors and hard-link ownership checks remain authoritative. Framed SHA-256 observations include whole binary file bytes, metadata, exact link-route/identity, bounded directory coverage and missing destinations. No raw file body, entry list or link target crosses this port; its result contains only digest/node/byte counts.

Read-only observation never creates a root/parent. Absent destinations remain equivalent after ordinary parent staging; substituted links and changed existing contents invalidate their hashes. All observations require complete coverage within 32 probes, 4096 nodes, depth 64, native per-file limits (at most 50 MiB) and 250 MiB aggregate bytes. Limits reject instead of publishing truncated witnesses. These are ordinary current filesystem observations, not durable file identity, CAS or a database/filesystem transaction.

`TenantWorkspaceMutationTarget` derives actual Run/Session/Instance scopes and current admitted/configuration ceilings before observation, then repeats requester/owner/current permission/lifecycle/Run/security fences afterward. A possible primary `workspaces.write` or `workspaces.delete` exception is preparation only. Parent reads, patch/copy source reads and secondary mutation permissions remain raw independent authority. Cross-root copy has a source read and destination write; a possible delete grant cannot supply a mixed batch's missing write. No approval, role assignment, grant, dispatch reservation or native mutation is created by preparation.

## Executed development evidence

The first representative four cases passed: both stores and roots prepare private current overwrite observations for a parent-readable Agent without workspace read/write, return stable source hashes, detect later current-byte changes, reject patch without source read, and leave actual bytes, approvals and dispatch audit unchanged. `/private/tmp/agent-core-workspace-mutation-target-first.log` records 4/4.

The first complete observation matrix passed 14/14: private binary changes, missing roots and parent staging, external/loop link handling, resolved route substitution, empty directory coverage, stat/open mutation/link swaps, per-file/aggregate/probe/depth bounds, cancellation and post-observation role revocation. `/private/tmp/agent-core-workspace-mutation-observations.log` records this run.

An expanded focused run passed 60/60 cases, including nineteen new observation/target cases plus existing canonical filesystem, structural mutation and direct Run/effect recovery coverage. Added cases freeze probe arrays, reject wide directory truncation, independently qualify cross-root source reads, reject foreign scopes before I/O and prohibit borrowing a possible delete grant for a secondary write. `/private/tmp/agent-core-workspace-mutation-observation-regression.log` records this run.

Static review then found the shared tenant mutation HomeAccess guard used a generic `AgentCoreException` before dispatch, bypassing normal tenant tool error projection. It now emits safe `home_access_disabled`/`home_read_only` denials. Additional actual-host cases narrow HomeAccess after Run admission and require a normal completed model continuation with no reservation/file effect. Private observation I/O/bounds errors are also mapped to a bounded unavailable reason without raw native details. The first hosted recheck passed fourteen existing mutation cases; two new HomeAccess cases hit a fixture null assertion because the admitted Definition legitimately omits the default policy. That assertion now resolves `StateIsolationPolicy.Default`, matching production and the existing read journey. Final requalification follows below.

## Final focused checks

The actual hosted Synthetic mutation matrix passed **16/16** cases in `/private/tmp/agent-core-workspace-mutation-home-policy-http-qualified.log`. For both stores, the new cases gate the first model request, prove the admitted default HomeAccess is ReadWrite, patch current Instance HomeAccess to Disabled over canonical HTTP, release the model, and observe a bounded forbidden `home_access_disabled` tool result. The Run completes normally with no dispatch/result mutation audit and no destination file. Existing known/uncertain mutation, missing/policy refusal, malformed argument, batch/copy and completed SQLite host recreation scenarios remain passing. Provider HTTP attempts are zero.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_model_mutations_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~WorkspaceMutationObservationTests|FullyQualifiedName~Tenant_workspace_mutation_target_|FullyQualifiedName~CanonicalWorkspaceFilesystemTests|FullyQualifiedName~WorkspaceStructureTests|FullyQualifiedName~Tenant_workspace_effects' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The expanded final store check passed **70/70** cases (`/private/tmp/agent-core-workspace-mutation-observations-qualified.log`): twenty-nine source-observation/target cases and forty-one existing canonical filesystem/structure/direct-effect cases. Eight new both-store cases narrow HomeAccess to Disabled or ReadOnly before/after observation and require precise denial; two additional cases prove private native exceptions are redacted.

The directory encoding was then hardened to frame each complete child list explicitly, avoiding an ambiguous flattened tree encoding independently of metadata. **29/29 affected observation/target cases passed again** against that final encoding (`/private/tmp/agent-core-workspace-mutation-source-final.log`). No observation is stored in an existing request, and no database schema/format changed. Distinct focused qualification is **86 passing cases** (70 store/native + 16 hosted), excluding repeated runs. All active test handles returned terminal success.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~WorkspaceMutationObservationTests|FullyQualifiedName~Tenant_workspace_mutation_target_' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

## Remaining full-proposal work

The existing direct Allowed path remains the only model mutation execution path. Exact durable mutation Review/grants still require a typed prepared operation/schema, deciding-human compound authority and independent-review checks, current source comparison at decision/effect gates, semantic root refusal suppression, approved Run/grant dispatch settlement/recovery and the shared model/inspector experience. No new model authorization intent or approval-payload schema is exposed by this prerequisite.

Other Workspace/Office/source-publication and autonomous families, other proposal families and the final authority/schema clean cutover remain open. No live/user database reset/migration, Real host restart/deployment, paid provider, CI or full suite was used. No frontend source changed and no new visual acceptance is claimed. The goal remains active.
