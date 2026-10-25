using AgentCore.Domain.Spaces;

namespace AgentCore.Domain.Tests;

public sealed class SpaceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
    [Theory]
    [InlineData(SpaceKind.Private, false)]
    [InlineData(SpaceKind.Shared, true)]
    public void Kind_requires_exact_private_owner(SpaceKind kind, bool owner)
    {
        var space = new Space(Guid.NewGuid(), Guid.NewGuid(), kind, owner ? Guid.NewGuid() : null, "Room", "", "", false, 1, At, At);
        Assert.Throws<ArgumentException>(space.Validate);
    }
    [Fact]
    public void Effective_bindings_restrict_inherited_without_rewriting_the_baseline_and_reject_alias_shadowing()
    {
        var credential = new SpaceResourceBinding(SpaceBindingKind.Credential, Guid.NewGuid().ToString("D"), "mail");
        var resource = new SpaceResourceBinding(SpaceBindingKind.InstanceResource, "instance:" + Guid.NewGuid().ToString("D"));
        var other = new SpaceResourceBinding(SpaceBindingKind.Credential, Guid.NewGuid().ToString("D"), "other");
        var member = new SpaceMembership(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Reviewer", "Review changes", [resource.StableKey], [other], 1, At, At);
        var inherited = new[] { credential, resource };
        var effective = EffectiveSpaceBindings.Resolve(inherited, member);
        Assert.Equal(new[] { credential, other }, effective); Assert.Equal(2, inherited.Length);
        Assert.Throws<ArgumentException>(() => EffectiveSpaceBindings.Resolve(inherited, member with { AddedBindings = [other with { AccountRef = "mail" }] }));
        Assert.Empty(EffectiveSpaceBindings.Prune(member, [credential]).DisabledInheritedBindingKeys);
        Assert.Equal(new[] { credential, other }, EffectiveSpaceBindings.Resolve([credential], EffectiveSpaceBindings.Prune(member, [credential])));
    }
    [Fact]
    public void Membership_freezes_collections_and_rejects_duplicate_resources()
    {
        var disabled = new List<string> { "InstanceResource:instance:" + Guid.NewGuid().ToString("D") };
        var member = new SpaceMembership(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "", disabled, [], 1, At, At).Freeze();
        disabled.Clear(); Assert.Single(member.DisabledInheritedBindingKeys);
        var binding = new SpaceResourceBinding(SpaceBindingKind.Secret, Guid.NewGuid().ToString("D"));
        Assert.Throws<ArgumentException>(() => (member with { AddedBindings = [binding, binding] }).Freeze());
    }
}
