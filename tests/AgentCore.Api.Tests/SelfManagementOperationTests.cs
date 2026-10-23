using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class SelfManagementOperationTests
{
    private sealed class SelectionReadySandboxFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.RemoveAll<ISandboxReadiness>();
                services.AddSingleton<ISandboxReadiness>(new SelectionReady());
            });
        }
    }
    private sealed class SelectionReady : ISandboxReadiness
    {
        public bool IsAvailable => true;
        public string State => "Ready";
        public string SafeMessage => "Isolated settings-selection fixture.";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unowned_executor_cannot_change_actual_settings_or_resources_despite_all_auto_apply_delegation(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "unowned-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using AgentCoreApiFactory factory = sqlite ? new HarnessSqliteFactory(root) : new AgentCoreApiFactory();
            var services = factory.Services;
            var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
            var policy = services.GetRequiredService<SelfManagementService>();
            var view = await policy.UpdateAsync(owner.InstanceId, owner.Revision, new(new(HarnessManagementMode.Managed),
                new(Enum.GetValues<HarnessManagementScope>()), new(Enum.GetValues<ManagementAutoApply>())));
            var admitted = (await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration;
            var executor = services.GetRequiredService<SessionToolExecutor>(); var session = Guid.NewGuid();
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId,
                OwnerTurnText: "Keep this concise review procedure.", Harness: await executor.HarnessContextAsync(owner.InstanceId, default), OwnedSessionId: session);
            var patch = JsonSerializer.Serialize(new { expectedInstanceRevision = view.InstanceRevision, section = "conversationPolicy", set = new { responseLength = "concise" }, clear = Array.Empty<string>() });
            var resource = JsonSerializer.Serialize(new { expectedInstanceRevision = view.InstanceRevision, logicalPath = "reviews/checklist.md", kind = "Knowledge", mediaType = "text/markdown", content = "Private review procedure.", source = "conversation:user" });
            foreach (var (tool, arguments) in new[] { ("settings.patch", patch), ("resources.upsert", resource) })
            {
                var result = await executor.ExecuteAsync(admitted.Definition, session, new("unowned", tool, arguments), 100000, admission: admission);
                Assert.Contains("forbidden", result.Text);
            }
            Assert.Equal(view.InstanceRevision, (await policy.ReadAsync(owner.InstanceId)).InstanceRevision);
            Assert.Equal("balanced", (await services.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(owner.InstanceId, "conversationPolicy")).Effective["responseLength"]);
            Assert.DoesNotContain((await services.GetRequiredService<AgentInstanceResourceService>().ListAsync(owner.InstanceId)).Resources, r => r.Origin == "Instance");
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("memoryPolicy", "userRetrieval", false)]
    [InlineData("triggerPolicy", "enabled", false)]
    [InlineData("conversationPolicy", "maxOutputTokens", 2048)]
    [InlineData("capabilities", "selected", "workspace.read")]
    public async Task Unowned_sensitive_settings_require_canonical_authority_before_any_consent(string section, string field, object value)
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var view = await services.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Managed), new([HarnessManagementScope.Settings]), new(Enum.GetValues<ManagementAutoApply>())));
        var definition = (await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var args = JsonSerializer.SerializeToElement(new { expectedInstanceRevision = view.InstanceRevision, section, set = new Dictionary<string, object> { [field] = section == "capabilities" ? new[] { value } : value }, clear = Array.Empty<string>() });
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId);
        Assert.Equal(ToolPolicyDecision.Deny, await executor.EvaluateExecutionPolicyAsync(definition, Guid.NewGuid(), new("change", "settings.patch", args.GetRawText()), args, admission, default));
        var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("change", "settings.patch", args.GetRawText()), 100000, admission: admission);
        Assert.Contains("forbidden", result.Text);
        Assert.Equal(view.InstanceRevision, (await services.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId)).InstanceRevision);
    }

    [Fact]
    public async Task Management_projection_is_lazy_builtin_procedures_are_read_only_and_off_discovery_is_silent()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var policy = services.GetRequiredService<SelfManagementService>();
        var view = await policy.UpdateAsync(owner.InstanceId, owner.Revision, new(new(HarnessManagementMode.Managed),
            new([HarnessManagementScope.Settings, HarnessManagementScope.Skills, HarnessManagementScope.Instructions]), new([])));
        var resolved = await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId);
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var context = new AgentContext(resolved.Configuration.Definition, [], "", null, AgentCore.Domain.Conversation.SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"), AgentInstanceId: owner.InstanceId,
            Harness: await executor.HarnessContextAsync(owner.InstanceId, default), PinnedSkillCatalog: resolved.Skills);
        var prompt = new PromptContextBuilder().Build(context, Guid.NewGuid());
        Assert.DoesNotContain(new PromptContextBuilder().OfferTools(context.Definition, context), t => ManagementTools.IsManagement(t.Name));
        var system = string.Join('\n', prompt.Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text));
        Assert.DoesNotContain("configuration.inspect", system);
        Assert.DoesNotContain("Configuration assistance", system);
        Assert.Contains(resolved.Skills, s => s.Key == ManagementBuiltInSkills.ConfigurationKey);
        var builtin = (await services.GetRequiredService<AgentInstanceSkillService>().ListAsync(owner.InstanceId)).Single(s => s.Key == ManagementBuiltInSkills.ConfigurationKey);
        Assert.Null(builtin.DefinitionVersion);
        Assert.Null(builtin.SourceDefinitionId);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await services.GetRequiredService<AgentInstanceSkillService>().WriteAsync(owner.InstanceId, "delete", builtin.Key, builtin.Revision));
        using var query = JsonDocument.Parse("{\"query\":\"configuration.inspect\"}");
        Assert.Empty(CapabilityDiscoveryMatcher.Load(context.Definition, context, ToolConfigurationGates.Unconfigured, query.RootElement, 0).Loaded);
        await policy.FreezeAsync(owner.InstanceId, view.InstanceRevision);
        context = context with { Harness = null };
        var unavailable = CapabilityDiscoveryMatcher.Load(context.Definition, context, ToolConfigurationGates.Unconfigured, query.RootElement, 0);
        Assert.Empty(unavailable.Loaded); Assert.Empty(unavailable.Unavailable);
        prompt = new PromptContextBuilder().Build(context, Guid.NewGuid());
        Assert.DoesNotContain(new PromptContextBuilder().OfferTools(context.Definition, context), t => ManagementTools.IsManagement(t.Name));
        Assert.DoesNotContain("configuration.inspect", string.Join('\n', prompt.Messages.Select(m => m.Text)));
    }
}
