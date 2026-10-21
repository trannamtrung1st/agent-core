using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class SystemOperatorService(ISystemOperatorStore store, IIamStore security, IdentityService identity, IamAuthorizer authorizer, IIdGenerator ids, IIdentityTokens tokens, TimeProvider time)
{
    private ValueTask Require(HumanSessionReference human, CancellationToken ct) => SystemHumanSessionAuthority.RequireAsync(security, identity, authorizer, human, "system.governance.manage", ct);
    public async ValueTask<SystemOperatorPage> QueryAsync(HumanSessionReference human, SystemCollectionQuery query, bool? isOperator, CancellationToken ct)
    {
        await Require(human, ct); var result = await store.QuerySystemOperatorsAsync(SystemAdministrationRules.Normalize(query), isOperator, ct);
        await Require(human, ct); return result;
    }
    public async ValueTask SetAsync(HumanSessionReference human, Guid accountId, string expectedVersion, bool enabled, CancellationToken ct)
    {
        await Require(human, ct); SystemAdministrationRules.AccountStatus(expectedVersion, IdentityStatus.Active);
        var audit = new AuthorizationAudit(ids.NewId(), null, human.PrincipalId, enabled ? "system.operators.assign" : "system.operators.revoke", accountId.ToString("D"), "allowed", "authorized", time.GetUtcNow());
        if (!await store.TrySetSystemOperatorAsync(human, accountId, expectedVersion, enabled, ids.NewId(), tokens.Create(), audit, ct)) throw new SecurityConflictException("stale_operator");
    }
}
