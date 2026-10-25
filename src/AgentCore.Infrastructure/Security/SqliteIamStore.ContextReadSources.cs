using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireContextReadSources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        if (run.Admission.Configuration?.SpaceContext is not { EnvironmentId: { } environment } pin) return;
        var state = await ReadTenantAsync(db, pin.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable"); var iam = new IamAuthorizer(this);
        await RequireSpaceExecutionSource(db, run, state, iam, ct).ConfigureAwait(false);
        var row = await OwnedAgents(db, pin.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(row);
        await TenantContextReadRules.RequireAsync(run, state, agent, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), iam,
            async name => (await db.Set<EnvironmentVariableRecord>().SingleOrDefaultAsync(v => v.TenantId == pin.TenantId && v.SpaceId == pin.SpaceId && v.EnvironmentId == environment && v.Name == name, ct).ConfigureAwait(false))?.Domain(),
            () => ContextCredentialBindings(db, run, ct), async id => await db.Set<Secret>().AnyAsync(s => s.TenantId == pin.TenantId && s.SecretId == id && s.Status == SecretStatus.Active, ct).ConfigureAwait(false)
                && await db.Set<SpaceAddedBindingRecord>().AnyAsync(b => b.TenantId == pin.TenantId && b.SpaceId == pin.SpaceId && b.InstanceId == run.AgentInstanceId.ToString("D") && b.Kind == SpaceBindingKind.Secret && b.ResourceKey == id.ToString("D"), ct).ConfigureAwait(false), async () => (await db.Set<SpaceEnvironmentRecord>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == pin.TenantId && e.SpaceId == pin.SpaceId && e.EnvironmentId == environment && e.DeletedAt == null, ct).ConfigureAwait(false))?.Revision, ct).ConfigureAwait(false);
    }
}
