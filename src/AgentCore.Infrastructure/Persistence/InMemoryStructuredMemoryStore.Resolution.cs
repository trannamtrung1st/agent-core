using AgentCore.Application.Ports;
using AgentCore.Domain.Memory;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryStructuredMemoryStore
{
    public ValueTask<MemoryResolutionResult> ResolveOpenLoopsAsync(
        TrustedMemoryOwner session, TrustedIdentityUserOwner? identity, TrustedUserOwner? user,
        string subjectKey, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var matches = _items.Values.Where(item => item.Kind == MemoryKind.OpenLoop
                && item.SubjectKey == subjectKey
                && item.Status is MemoryItemStatus.Active or MemoryItemStatus.Resolved
                && (item.Scope == MemoryScope.Session && item.SessionId == session.SessionId
                    || identity is not null && item.Scope == MemoryScope.IdentityUser
                        && item.OwnerInstanceId == identity.InstanceId && item.OwnerProfileId == identity.ProfileId
                    || user is not null && item.Scope == MemoryScope.User && item.OwnerProfileId == user.ProfileId))
                .GroupBy(item => item.Scope).OrderBy(group => group.Key)
                .Select(group => group.OrderByDescending(item => item.Status == MemoryItemStatus.Active)
                    .ThenByDescending(item => item.UpdatedAt).ThenByDescending(item => item.MemoryId).First())
                .ToArray();
            var changed = false;
            for (var index = 0; index < matches.Length; index++)
            {
                var item = matches[index];
                if (item.Status != MemoryItemStatus.Active) continue;
                changed = true;
                matches[index] = item with { Status = MemoryItemStatus.Resolved, UpdatedAt = resolvedAt };
                _items[item.MemoryId] = matches[index];
            }
            return ValueTask.FromResult(new MemoryResolutionResult(changed, matches));
        }
    }
}
