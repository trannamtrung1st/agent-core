using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Tests;

public sealed class TenantMemoryProjectionTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), User = Guid.NewGuid(), Account = Guid.NewGuid(), UserId = Guid.NewGuid(),
        Agent = Guid.NewGuid(), Executor = Guid.NewGuid(), Session = Guid.NewGuid(), Run = Guid.NewGuid(), Entry = Guid.NewGuid();
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Canonical_projection_reserves_cross_session_scopes_under_item_and_character_saturation(bool longFacts)
    {
        var identity = Fact(MemoryScope.IdentityUser, "identity fact", "IDENTITY_SCOPE_EVIDENCE");
        var user = Fact(MemoryScope.User, "user fact", "USER_SCOPE_EVIDENCE");
        var session = Enumerable.Range(0, MemoryLimits.PromptMaxItems + 3)
            .Select(i => Fact(MemoryScope.Session, $"session fact {i}", longFacts ? new string('x', MemoryLimits.MaxContentCharacters) : $"Session observation {i}")).ToArray();
        var candidates = session.Append(identity).Append(user)
            .Append(Fact(MemoryScope.User, "corrupt fact", "CORRUPT_HASH_EVIDENCE") with { ContentHash = "invalid" })
            .Append(Fact(MemoryScope.IdentityUser, "resolved fact", "RESOLVED_EVIDENCE") with { Status = MemoryItemStatus.Resolved }).ToArray();
        var projected = TenantMemoryRules.Project(candidates);
        Assert.Contains(projected, item => item.MemoryId == identity.MemoryId);
        Assert.Contains(projected, item => item.MemoryId == user.MemoryId);
        Assert.InRange(projected.Count, 2, MemoryLimits.PromptMaxItems);
        Assert.InRange(projected.Sum(item => item.Subject.Length + item.Content.Length), 1, MemoryLimits.PromptMaxCharacters);
        Assert.DoesNotContain(projected, item => item.Content is "CORRUPT_HASH_EVIDENCE" or "RESOLVED_EVIDENCE");
        Assert.Equal(projected.Select(item => item.MemoryId), TenantMemoryRules.Project(candidates.Reverse()).Select(item => item.MemoryId));
    }

    private static TenantLearnedMemory Fact(MemoryScope scope, string subject, string content)
    {
        var at = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        return new(Tenant, Guid.NewGuid(), scope, User, Account, UserId, Agent, Executor,
            Session, Run, Run, Entry, MemoryKind.Fact, MemoryItemStatus.Active,
            subject, content, subject.ToLowerInvariant(), "UserExplicit", TenantMemoryRules.Hash(MemoryKind.Fact, subject, content), 1, at, at);
    }
}
