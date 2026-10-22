using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantManagementStore
{
    public async ValueTask<TenantManagementSource> ReadTenantManagementSourceAsync(TenantRunReference reference, Guid? generation, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        var reader = await ManagementReader(db, run, inspectionHuman, ct).ConfigureAwait(false);
        if (inspectionHuman is { } human)
        {
            if (!reader.Security.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approval_unavailable");
            await ValidateTenantActorAsync(db, reader.Security, human, ct).ConfigureAwait(false);
        }
        await TenantManagementReadRules.RawAsync(reader, new IamAuthorizer(this), ct, inspectionHuman).ConfigureAwait(false);
        var snapshot = new TenantAgentConfigurationSnapshot(reader.Agent,
            await SqliteAgentInstanceStore.ReadSkillsAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, run.AgentInstanceId, ct).ConfigureAwait(false),
            await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        return new(reader, snapshot);
    }
    public async ValueTask<TenantManagementReadObservation> ObserveTenantManagementReadAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        PreparedManagementRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_read_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveManagementRead(db, run, tool, arguments, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantManagementReader> ManagementReader(AgentCoreDbContext db, AgentRun run, Guid? human, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("management_read_unavailable");
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("management_read_unavailable");
        var root = await TenantCoreEventProduction.RootAsync(db, run, ct).ConfigureAwait(false);
        var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_read_unavailable"); var agent = Owned(row);
        if (run.Admission.Configuration is null || agent.PrincipalId != run.Owner.ExecutorPrincipalId) throw new AccessDeniedException("management_read_unavailable");
        if (human is null) await TenantExecutionLineage.RequireRequesterAsync(run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        var baseline = await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false);
        return new(run, root, state, agent, baseline, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides));
    }
    private async Task<TenantManagementReadObservation> ObserveManagementRead(AgentCoreDbContext db, AgentRun run, string tool, string arguments, PreparedManagementRead? expected, Guid? human, CancellationToken ct)
    {
        var reader = await ManagementReader(db, run, human, ct).ConfigureAwait(false); var args = TenantManagementReadContract.Parse(tool, arguments); var iam = new IamAuthorizer(this);
        if (tool == ToolCatalog.SkillsLoad) reader = await ManagementSkillReader(db, reader, ct).ConfigureAwait(false);
        else if (TenantManagementInspectionSources.IsInspection(tool)) reader = await ManagementInspectionReader(db, reader, ct).ConfigureAwait(false);
        if (tool == ToolCatalog.SkillsLoad && human is null) TenantSkillLoadRules.RequireSelection(reader, TenantSkillLoadContract.Parse(args.Json));
        var baseline = reader.Baseline; if (human is null && expected is null) TenantManagementReadRules.Eligible(reader, baseline, tool);
        if (human is not null) { if (expected is null || !reader.Security.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approval_unavailable"); await ValidateTenantActorAsync(db, reader.Security, human.Value, ct).ConfigureAwait(false); }
        if (human is null) await TenantManagementReadRules.RawAsync(reader, iam, ct).ConfigureAwait(false);
        if (human is not null) await TenantManagementReadRules.RawAsync(reader, iam, ct, human).ConfigureAwait(false);
        if (human is not null && expected is not null) await TenantManagementInspectionSources.RawAsync(reader, expected.SourcesJson, iam, human.Value, ct).ConfigureAwait(false);
        if (expected is not null)
        {
            if (expected.ToolName != tool || expected.ArgumentsJson != args.Json) throw new AccessDeniedException("management_source_changed");
            var session = run.SessionId.ToString("D"); var root = run.Admission.Activation.RootActivationId.ToString("D");
            var rows = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.SessionId == session && r.RootActivationId == root).Take(1025).ToArrayAsync(ct).ConfigureAwait(false);
            var requestIds = rows.Where(r => r.RequestId != null).Select(r => r.RequestId!).ToArray();
            var requests = await db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.TenantId == reader.Security.Tenant.TenantId && requestIds.Contains(r.RequestId)).ToArrayAsync(ct).ConfigureAwait(false);
            var mapped = requests.Select(r => r.Domain()).ToDictionary(r => r.RequestId);
            TenantManagementSourceRetention? retention = null;
            var receipts = rows.Select(r => r.Domain()).ToArray();
            try {
                TenantManagementReadRules.ValidateCurrent(reader, baseline, expected, receipts, id => mapped.GetValueOrDefault(id));
                retention = TenantManagementReadRules.RetainedSources(reader, expected, receipts, id => mapped.GetValueOrDefault(id));
                await TenantManagementInspectionSources.RequireAsync(reader, retention.CurrentSourcesJson, iam, ct, human, retention.DeletedSources).ConfigureAwait(false);
            }
            catch (AccessDeniedException error) when (human is not null && error.ReasonCode is "management_source_changed" or "management_read_unavailable") { return new(reader, expected, true); }
            return new(reader, expected, Retention: retention);
        }
        if (tool == ToolCatalog.SkillsLoad)
        {
            var value = TenantSkillLoadRules.Build(reader, args);
            await TenantManagementInspectionSources.RequireAsync(reader, value.SourcesJson, iam, ct, human).ConfigureAwait(false);
            return new(reader, value);
        }
        return new(reader, TenantManagementInspectionSources.IsInspection(tool)
            ? await (managementInspection?.Invoke() ?? throw new AccessDeniedException("management_inspection_unavailable")).BuildAsync(reader, args, iam, ct).ConfigureAwait(false)
            : TenantManagementReadRules.Build(reader, baseline, args));
    }
}
