using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record SystemOperatorSummary(Guid AccountId, string LoginKey, string DisplayName, IdentityStatus Status,
    DateTimeOffset CreatedAt, bool IsOperator, string OperatorVersion);
public sealed record SystemOperatorPage(IReadOnlyList<SystemOperatorSummary> Items, int Total, int Page, int PageSize);
public interface ISystemOperatorStore
{
    ValueTask<SystemOperatorPage> QuerySystemOperatorsAsync(SystemCollectionQuery query, bool? isOperator, CancellationToken ct = default);
    ValueTask<bool> TrySetSystemOperatorAsync(HumanSessionReference human, Guid accountId, string expectedVersion, bool enabled,
        Guid assignmentId, string nextSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default);
}
