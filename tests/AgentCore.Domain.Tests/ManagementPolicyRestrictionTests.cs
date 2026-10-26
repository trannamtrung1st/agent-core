using AgentCore.Domain.Definitions;
namespace AgentCore.Domain.Tests;

public sealed class ManagementPolicyRestrictionTests
{
    [Fact]
    public void Definition_adoption_inherits_sparse_defaults_but_preserves_saved_source_and_tool_eligibility()
    {
        var first = new AgentDefinition(2, "test", 1, new("Test", "Test", "", ""), [], "", null!, null!, null!, null!, null!,
            new Dictionary<string, string>(), SelfManagement: Full());
        var now = DateTimeOffset.UtcNow;
        var instance = new AgentInstance(Guid.NewGuid(), first.Id, 1, first.Identity, AgentInstanceLifecycle.Active, now, now,
            HarnessManagement: new(Full() with { Frozen = true }, Overrides: new()));
        var adopted = first with { Version = 2, SelfManagement = Full(HarnessManagementMode.Assisted) with {
            Sources = ["new-definition-source"], EligibleTools = ["workspace.write"], Scopes = [HarnessManagementScope.Skills] } };
        var effective = ManagementPolicyResolver.Resolve(adopted, instance with { ActiveVersion = 2 });
        Assert.Equal(HarnessManagementMode.Assisted, effective.Mode);
        Assert.Equal(new[] { HarnessManagementScope.Skills }, effective.Scopes);
        Assert.Equal(instance.HarnessManagement!.Policy.Sources, effective.Sources);
        Assert.Equal(instance.HarnessManagement!.Policy.EligibleTools, effective.EligibleTools);
        Assert.True(effective.Frozen);
    }

    [Theory]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"mode\":{\"value\":\"Managed\",\"unknown\":true}}")]
    [InlineData("{\"configurationAssistance\":{}}")]
    public void Sparse_policy_JSON_rejects_unknown_fields_and_missing_wrapper_values(string json)
        => Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<ManagementPolicyOverrides>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    [Fact]
    public void Unnormalized_live_state_fails_closed_instead_of_inheriting_or_using_the_full_policy()
    {
        // Only policy participates in this pure resolver; the other fields are irrelevant here.
        var definition = new AgentDefinition(2, "test", 1, new("Test", "Test", "", ""), [], "", null!, null!, null!, null!, null!, new Dictionary<string, string>(), SelfManagement: Full());
        var now = DateTimeOffset.UtcNow;
        var owner = new AgentInstance(Guid.NewGuid(), definition.Id, 1, definition.Identity, AgentInstanceLifecycle.Active, now, now,
            HarnessManagement: new(Full()));
        Assert.Contains("SelfManagementNormalizationRequired", Assert.Throws<InvalidOperationException>(() => ManagementPolicyResolver.Resolve(definition, owner)).Message);
        Assert.Equal(HarnessManagementMode.Managed, ManagementPolicyResolver.Resolve(definition, owner with { HarnessManagement = null }).Mode);
        var explicitEmpty = new ManagementPolicyOverrides(Scopes: new([]), AutoApply: new([]), ConfigurationAssistance: new(false));
        var effective = ManagementPolicyResolver.Resolve(definition, owner with { HarnessManagement = new(Full() with { Frozen = true }, Overrides: explicitEmpty) });
        Assert.Empty(effective.Scopes); Assert.Empty(effective.ManagedAutoApply!);
        Assert.False(effective.BuiltInSkills!.ConfigurationAssistance); Assert.True(effective.Frozen);
    }
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
