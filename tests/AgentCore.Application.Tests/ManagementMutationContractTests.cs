using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Tests;
public sealed class ManagementMutationContractTests
{
    private static PreparedManagementMutation Plan(string tool, long revision = 3) => tool switch {
        "settings.patch" => new(Guid.NewGuid(), Guid.NewGuid(), tool, TenantManagementMutationContract.Parse(tool, JsonSerializer.Serialize(new { expectedInstanceRevision = revision, section = "instructions", set = new { agentInstructions = "Exact instruction" }, clear = Array.Empty<string>() })).Json,
            new('a',64), new('b',64), new('c',64), "[]", Settings: new(AgentInstructions: new("Exact instruction")), AutomationChanges: []),
        "management.policy.update" => new(Guid.NewGuid(), Guid.NewGuid(), tool, TenantManagementMutationContract.Parse(tool, JsonSerializer.Serialize(new { expectedInstanceRevision = revision, mode = "Assisted", scopes = new[] { "Settings" }, managedAutoApply = Array.Empty<string>() })).Json,
            new('a',64), new('b',64), new('c',64), "[]", Management: new(new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], []), Overrides: new(Mode: new(HarnessManagementMode.Assisted)))),
        "management.freeze" => new(Guid.NewGuid(), Guid.NewGuid(), tool, TenantManagementMutationContract.Parse(tool, JsonSerializer.Serialize(new { expectedInstanceRevision = revision })).Json,
            new('a',64), new('b',64), new('c',64), "[]", Management: new(new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], [], Frozen: true))),
        "automation.preferences.patch" => new(Guid.NewGuid(), Guid.NewGuid(), tool, TenantManagementMutationContract.Parse(tool, JsonSerializer.Serialize(new { expectedInstanceRevision = revision, modelPreference = new { mode = "fixed", offeringId = "dca30000-0000-4000-8000-000000000001", reasoning = new { mode = "modelDefault" } } })).Json,
            new('a',64), new('b',64), new('c',64), "[]", UnattendedModel: AgentCore.Domain.Models.ModelPreference.Fixed("dca30000-0000-4000-8000-000000000001")), _ => throw new ArgumentException()
    };
    [Theory] [InlineData("settings.patch")] [InlineData("management.policy.update")] [InlineData("management.freeze")] [InlineData("automation.preferences.patch")]
    public void Saved_management_plan_roundtrip_preserves_value_equality_closed_payload_hash_and_exact_intent(string tool)
    {
        var plan = Plan(tool); TenantManagementMutationRules.Validate(plan);
        var restored = JsonSerializer.Deserialize<PreparedManagementMutation>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(plan, restored); Assert.Equal(plan.GetHashCode(), restored.GetHashCode());
        var operation = TenantApprovalRules.PrepareManagementMutation(Guid.NewGuid(), Guid.NewGuid(), plan, 3, 1);
        Assert.Equal(TenantOperationKind.ManagementMutation, operation.Kind); Assert.Equal("agents.update", operation.Permission); Assert.Equal("agent", operation.ResourceType);
        Assert.Equal(operation, TenantApprovalRules.Rebuild(JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(operation))!));
        var intent = TenantApprovalToolContract.Parse(new("intent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_management", change = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(plan.ArgumentsJson) } })));
        Assert.True(intent.Matches(operation));
        Assert.Throws<ArgumentException>(() => { _ = (operation with { ManagementRead = new(Guid.NewGuid(),Guid.NewGuid(),"settings.list","{}",3,new('a',64),new('b',64),"{}",new('c',64)) }).Kind; });
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareManagementMutation(Guid.NewGuid(), Guid.NewGuid(), plan, 4, 1));
    }
    [Theory]
    [InlineData("management.freeze", "{\"expectedInstanceRevision\":0}")]
    [InlineData("management.freeze", "{\"expectedInstanceRevision\":3,\"expectedInstanceRevision\":4}")]
    [InlineData("management.freeze", "{\"expectedInstanceRevision\":3,\"instanceId\":\"foreign\"}")]
    [InlineData("management.policy.update", "{\"expectedInstanceRevision\":3,\"mode\":\"Managed\",\"scopes\":[\"Settings\",\"Settings\"],\"managedAutoApply\":[]}")]
    [InlineData("settings.patch", "{\"expectedInstanceRevision\":3,\"section\":\"unknown\",\"set\":{},\"clear\":[]}")]
    [InlineData("settings.patch", "{\"expectedInstanceRevision\":3,\"section\":\"sandboxPolicy\",\"set\":{\"initializationScript\":\"shell text\"},\"clear\":[]}")]
    [InlineData("automation.preferences.patch", "{\"expectedInstanceRevision\":3,\"offeringId\":null,\"reasoningEffort\":null,\"runNow\":true}")]
    public void Wider_duplicate_host_and_malformed_management_commands_are_rejected(string tool, string arguments) => Assert.ThrowsAny<ArgumentException>(() => TenantManagementMutationContract.Parse(tool, arguments));
    [Theory] [InlineData("conversationContextScope","Session")] [InlineData("homeAccess","ReadOnly")] [InlineData("workingCleanup","OnSessionDeletion")] [InlineData("browserIsolation","EphemeralSession")] [InlineData("agentStateVisibility","Off")]
    public void State_isolation_fields_use_the_existing_closed_enum_types_in_management_schemas(string field, string value)
    {
        var args = TenantManagementMutationContract.Parse("settings.patch",JsonSerializer.Serialize(new { expectedInstanceRevision=3,section="stateIsolationPolicy",set=new Dictionary<string,string> { [field]=value },clear=Array.Empty<string>() }));
        Assert.Equal(value,args.Value.GetProperty("set").GetProperty(field).GetString());
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("settings.patch",JsonSerializer.Serialize(new { expectedInstanceRevision=3,section="stateIsolationPolicy",set=new Dictionary<string,string> { [field]="unknown" },clear=Array.Empty<string>() })));
    }
    [Fact]
    public void Semantic_root_correlation_ignores_only_the_mutable_revision_while_action_hash_stays_exact()
    {
        var first = Plan("settings.patch"); var second = first with { ArgumentsJson = first.ArgumentsJson.Replace("\"expectedInstanceRevision\":3", "\"expectedInstanceRevision\":4") };
        var id = Guid.NewGuid(); var owner = Guid.NewGuid(); var a = TenantApprovalRules.PrepareManagementMutation(id, owner, first, 3, 1); var b = TenantApprovalRules.PrepareManagementMutation(id, owner, second, 4, 1);
        Assert.Equal(a.SemanticActionJson, b.SemanticActionJson); Assert.NotEqual(a.ActionHash,b.ActionHash);
        Assert.NotEqual(first, first with { AfterStableHash = new('d',64) });
    }
    [Fact]
    public void Only_the_three_incumbent_preferences_use_the_existing_direct_effect_path()
    {
        Assert.NotNull(TenantManagementMutationContract.Preference("settings.patch", """{"expectedInstanceRevision":3,"section":"conversationPolicy","set":{"language":"vi"},"clear":[]}"""));
        Assert.Null(TenantManagementMutationContract.Preference("settings.patch", """{"expectedInstanceRevision":3,"section":"conversationPolicy","set":{"maxOutputTokens":512},"clear":[]}"""));
        Assert.Null(TenantManagementMutationContract.Preference("management.freeze", """{"expectedInstanceRevision":3}"""));
    }
}
