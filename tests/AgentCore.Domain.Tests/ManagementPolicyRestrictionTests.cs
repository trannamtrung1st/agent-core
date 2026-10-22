using AgentCore.Domain.Definitions;
namespace AgentCore.Domain.Tests;

public sealed class ManagementPolicyRestrictionTests
{
    private static HarnessManagementPolicy Full(HarnessManagementMode mode = HarnessManagementMode.Managed) => new(mode,
        Enum.GetValues<HarnessManagementScope>(), ["conversation:user", "https://example.test"], ["workspace.read", "web.fetch"],
        ManagedAutoApply: Enum.GetValues<ManagementAutoApply>());
    [Theory]
    [InlineData(HarnessManagementMode.Disabled, HarnessManagementMode.Managed, HarnessManagementMode.Disabled)]
    [InlineData(HarnessManagementMode.Managed, HarnessManagementMode.Disabled, HarnessManagementMode.Disabled)]
    [InlineData(HarnessManagementMode.Assisted, HarnessManagementMode.Managed, HarnessManagementMode.Assisted)]
    [InlineData(HarnessManagementMode.Managed, HarnessManagementMode.Assisted, HarnessManagementMode.Assisted)]
    [InlineData(HarnessManagementMode.Managed, HarnessManagementMode.Managed, HarnessManagementMode.Managed)]
    public void Neither_live_nor_pinned_mode_can_be_bypassed(HarnessManagementMode pin, HarnessManagementMode live, HarnessManagementMode expected)
        => Assert.Equal(expected, ManagementPolicyResolver.Restrict(Full(pin), Full(live)).Mode);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Freeze_of_either_owner_and_each_field_ceiling_are_preserved(bool pinned)
    {
        var original = Full();
        var narrow = Full() with { Frozen = true, Scopes = [HarnessManagementScope.Settings], Sources = ["conversation:user"],
            EligibleTools = ["workspace.read"], ManagedAutoApply = [ManagementAutoApply.ConversationPreferences], BuiltInSkills = new(false, true) };
        var value = ManagementPolicyResolver.Restrict(pinned ? narrow : original, pinned ? original : narrow);
        Assert.True(value.Frozen); Assert.False(value.Allows(HarnessManagementScope.Settings));
        Assert.Equal(narrow.Scopes, value.Scopes); Assert.Equal(narrow.Sources, value.Sources); Assert.Equal(narrow.EligibleTools, value.EligibleTools);
        Assert.Equal(narrow.ManagedAutoApply, value.ManagedAutoApply); Assert.False(value.BuiltInSkills!.ConfigurationAssistance); Assert.True(value.BuiltInSkills.HarnessAuthoring);
        Assert.Equal(9, original.Scopes.Count); Assert.False(original.Frozen);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_policy_does_not_infer_management_authority(bool pinned)
    {
        var result = ManagementPolicyResolver.Restrict(pinned ? null : Full(), pinned ? Full() : null);
        Assert.Equal(HarnessManagementMode.Disabled, result.Mode); Assert.Empty(result.Scopes); Assert.Empty(result.ManagedAutoApply!);
    }
}
