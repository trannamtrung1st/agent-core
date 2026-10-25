using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantOperationReceipt?> TryCommitManagementCommandAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantManagementCommandCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        var saved = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RunId == row.AgentRunId && r.SourceCallId == commit.SourceCallId, ct).ConfigureAwait(false);
        if (saved is not null) return TenantLocalEffectRules.MatchDirectReceipt(saved.Domain(), run, commit.SourceCallId, commit.ParametersHash);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
        var reader = await ManagementReader(db, run, null, ct).ConfigureAwait(false); var before = reader.Security;
        var value = commit.Operation.ManagementMutation ?? throw new SecurityConflictException("operation_mismatch");
        await ValidateTenantActorAsync(db, before, reader.Agent.PrincipalId, ct).ConfigureAwait(false);
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human) await ValidateTenantActorAsync(db, before, human, ct).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        TenantManagementDirectRules.Transaction(next, expectedSecurityRevision, reader, commit, audit, at);
        if (value.Skill is not null) await RequireManagementSkillSources(db, reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct).ConfigureAwait(false);
        await TenantManagementDirectRules.AuthorityAsync(reader, value, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var previous = await RootApprovalAsync(db, before.Tenant.TenantId, run.Admission.Activation.RootActivationId.ToString("D"), reader.Agent.PrincipalId, commit.Operation, ct).ConfigureAwait(false);
        if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous.Domain()));
        var source = new TenantAgentConfigurationSnapshot(reader.Agent, await SqliteAgentInstanceStore.ReadSkillsAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false), await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        TenantAgentConfigurationChange change;
        if (value.Resource is { } resource)
        {
            var context = await ManagementResourceContext(db, reader, TenantManagementMutationContract.Parse(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson), null, ct).ConfigureAwait(false);
            if (JsonSerializer.Serialize(context.Material) != JsonSerializer.Serialize(resource.Source)) throw new AccessDeniedException("resource_source_changed");
            if (resource.InstanceResource is { } binding) _ = await AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(definitionContent ?? throw new AccessDeniedException("resource_content_unavailable"),
                binding.ContentSha256, binding.ByteLength, binding.MediaType, ct).ConfigureAwait(false);
            change = TenantManagementMutationRules.Change(reader, value, context.Source.Snapshot, context.Published);
        }
        else change = TenantManagementMutationRules.Change(reader, value, source);
        var candidate = TenantAgentConfigurationRules.Apply(reader.Agent.Instance, change, at);
        next = TenantAgentChildScopes.RetainAvailable(next, run.AgentInstanceId, await ChildCatalogAsync(db, next.Tenant.TenantId, run.AgentInstanceId, change, ct).ConfigureAwait(false));
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        var owned = await OwnedAgents(db, tenant.TenantId).SingleAsync(a => a.Instance.InstanceId == row.AgentInstanceId, ct).ConfigureAwait(false);
        db.Attach(owned.Instance);
        await ReconcileResourceSpaceBindings(db, next.Tenant.TenantId, change, audit, ct).ConfigureAwait(false);
        if (change.Resource is { } resourceChange) await SqliteAgentInstanceStore.StageResourcesAsync(db, resourceChange, null, ct).ConfigureAwait(false);
        if (change.Skill is { } skill) await SqliteAgentInstanceStore.StageSkillsAsync(db, skill, null, ct).ConfigureAwait(false);
        owned.Instance.HarnessManagementJson = candidate.HarnessManagement is null ? null : JsonSerializer.Serialize(candidate.HarnessManagement, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        owned.Instance.Revision = candidate.Revision; owned.Instance.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        foreach (var fact in CoreEventPersistence.Instance(reader.Agent.Instance, candidate,
            AgentCore.Domain.Triggers.TriggerOwner.ForTenant(before.Tenant.TenantId, run.AgentInstanceId, reader.Agent.PrincipalId)))
            await TenantCoreEventProduction.StageAsync(db, fact, ct).ConfigureAwait(false);
        AgentRunStoreMapping.Apply(row, run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.Operation.OperationId, commit.Operation.ActionHash, at));
        var receipt = new TenantOperationReceipt(commit.Operation.OperationId, null, tenant.TenantId, run.AgentRunId, run.SessionId,
            run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, run.AgentInstanceId, commit.Operation.ActionHash,
            TenantOperationOutcome.Completed, TenantManagementMutationRules.Completed, candidate.Revision, candidate.PersonaRevision,
            DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), commit.SourceCallId, commit.ParametersHash, ManagementSkill: value.Skill is null ? null : TenantManagementSkillRules.Evidence(value, reader.Agent.Instance.Revision),
                ManagementResource: value.Resource is null ? null : TenantManagementResourceRules.Evidence(value, reader.Agent.Instance.Revision));
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false);
        db.Add(TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, receipt.OperationId) with { At = at });
        TenantLocalEffectRules.RequireDirectRun(run, commit.Generation, (clock ?? TimeProvider.System).GetUtcNow());
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return null; }
    }
}
