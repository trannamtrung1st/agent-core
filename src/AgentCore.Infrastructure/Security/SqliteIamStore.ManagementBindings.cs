using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantManagementBindingContext> ReadTenantManagementBindingContextAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        var reader = await ManagementReader(db, run, inspectionHuman, ct).ConfigureAwait(false);
        if (inspectionHuman is { } human) await ValidateTenantActorAsync(db, reader.Security, human, ct).ConfigureAwait(false);
        return await ManagementBindingContext(db, reader, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantManagementBindingContext> ManagementBindingContext(AgentCoreDbContext db, TenantManagementReader reader,
        TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var instance = reader.Agent.Instance.InstanceId;
        TenantManagementMutationRules.Eligible(reader, args.Tool);
        await TenantManagementReadRules.RawAsync(reader, new IamAuthorizer(this), ct, human).ConfigureAwait(false);
        var bindings = await OwnedCredentialBindings(db, tenant, instance).AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false);
        var id = args.Tool == "credentials.binding.propose" ? args.Value.GetProperty("credentialId").GetGuid()
            : bindings.SingleOrDefault(b => b.BindingId == args.Value.GetProperty("bindingId").GetGuid().ToString("D")) is { } selected
                ? Guid.Parse(selected.CredentialId) : throw new AccessDeniedException("credential_read_required");
        await TenantManagementBindingRules.RawAsync(reader, id, human ?? reader.Agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var row = await OwnedCredentials(db, tenant).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking().SingleOrDefaultAsync(c => c.CredentialId == id.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("credential_read_required");
        var credential = await CredentialView(db, tenant, row, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_read_required");
        var source = new TenantManagementSource(reader, new(reader.Agent, await SqliteAgentInstanceStore.ReadSkillsAsync(db, instance, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, instance, ct).ConfigureAwait(false), await ReadIsolationMetadataAsync(db, instance.ToString("D"), ct).ConfigureAwait(false)));
        // Other bindings supply association conflict identities, never credential payloads.
        var context = new TenantManagementBindingContext(source, credential, bindings.Select(MapBinding).ToArray());
        if (human is null) await RequireManagementReadSources(db, reader.Run, [TenantManagementBindingRules.Inspections(context, args)[0]], ct).ConfigureAwait(false);
        return context;
    }
}
