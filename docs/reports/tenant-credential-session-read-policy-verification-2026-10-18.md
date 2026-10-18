# Tenant Credential discovery Session-policy verification — 2026-10-18

This review follow-up closes an independent Session-read policy gap in `credentials.list` and its retained metadata. It preserves the [exact protected-fill grant path](tenant-browser-credential-grant-verification-2026-10-18.md) and the existing read/Runtime owners. Typed Credential metadata-read Review remains open; this correction does not implement consent for metadata or claim completion of the full [proposal](multitenant-iam-delivery-2026-10-16.md).

## Correction and observed behavior

Previously the metadata reader required raw Session-read permission but evaluated review policy only on each Credential. With `sessions.read=Always`, an agent could still receive an alias page and reuse saved metadata in a later model call. Four actual in-memory/SQLite reproduction cases demonstrated that missing gate. Two raced-policy cases already withheld output through the existing stale-security audit fence; their initial test expectation was corrected to recognize that behavior.

`TenantCredentialRuntimeReadService.RequireAsync` now evaluates the independent Session-read decision after raw authority and before reading bindings. Initial tool evaluation uses the existing operation audit; post-I/O and historical checks use the same current policy engine. A review requirement returns `credential_read_review_required`. No binding snapshot, metadata or approval is created by that refusal. A security change after a snapshot still discards the read as `stale_revision`; a fresh call is refused before another binding read. There is no automatic tool or model replay.

Six both-store integration cases cover policy required before discovery, actual policy mutation after the first binding snapshot, and policy required after a real metadata result is saved in a tool checkpoint. The policy gate blocks the first binding read, the raced call returns no data, and the historical path prevents another binding read and makes `TenantRunExecutionAuthority.CurrentDefinitionAsync` unavailable before model generation. Existing raw permission, current binding/capability, tiny-budget, scoped filtering and private provenance checks remain in force.

Ten hosted Synthetic cases use actual Runtime/model orchestration, authenticated HTTP approval and native Chromium on a disposable local password page. The two new cases enforce Session-read Review before `credentials.list`: the model receives only the named refusal, with no Credential display metadata/provenance. It can separately propose the exact known bound password fill, obtain current human consent covering the compound scopes and consume its exact Credential-use grant once. That Browser consent does not retroactively authorize the earlier metadata read. The other eight retain the prior raw/exceptional fill and final authority-revocation checks.

Review also found stale model guidance saying protected Credential filling was unavailable even when its typed schema was offered. The description now permits explicitly offered protected field filling, states that Core resolves the bound General Password internally and preserves the no-sign-in guarantee. The hosted cases assert that the actual offered schema and description agree. No frontend component, UI kit or public endpoint changes.

## Verification

Final selections passed **88 Application, 16 Infrastructure and 10 API cases: 114 passing, no failures or skips**. The six-case reproduced boundary selection overlaps the final Infrastructure run and is not additive. `git diff --check` passed; all 16 added/changed local links/anchors and balanced fences passed across ten Markdown documents.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Tenant_Credential_discovery|FullyQualifiedName~Tenant_password_lineage|FullyQualifiedName~Tenant_Browser_password_consent' --logger 'console;verbosity=normal'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Browser_model_protected_fill' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~BrowserToolTests' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
```

Evidence logs: `/private/tmp/agent-core-credential-read-session-policy-before-20261018.log`, `/private/tmp/agent-core-credential-read-session-policy-after-20261018.log`, `/private/tmp/agent-core-credential-session-policy-model-20261018.log`, `/private/tmp/agent-core-credential-session-policy-final-infrastructure-20261018.log` and `/private/tmp/agent-core-credential-session-policy-final-application-20261018.log`. Failed reproduction/test-expectation runs are not passing evidence. No source/test edits occurred while build/test/EF processes were live.

## Remaining scope

No installed data/schema/profile reset or migration, Real-host restart, external provider/account spending, deployment, hosted CI or full CI suite occurred. A scripted Synthetic model and local login page prove orchestration/field filling, not live-model or external login semantics. No fresh rendered UI verification was performed for this backend/model-only correction.

Typed metadata-read Review still needs exact page/scope preparation, durable consent, scoped inbox/inspection, one-read delivery, historical provenance and restart evidence through the existing approval owner. Browser rich resources/lifecycle/autonomous admission, remaining resource/effect families, Governance/platform, voice/rich Chat/live input arbitration, final obsolete-authority/schema consolidation and the historical SQLite preference reliability cause remain open. The full goal remains active.
