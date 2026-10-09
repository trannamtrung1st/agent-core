using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class CapabilityUsageGuidanceTests
{
    [Fact]
    public async Task Phase_restrictions_keep_suppressed_interfaces_out_of_the_guide()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.CapabilitiesLoad,
            ToolCatalog.WorkspaceWrite, ToolCatalog.BrowserFillCredential);
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid() };
        var builder = new PromptContextBuilder(ToolConfigurationGates.AllowAll);
        var request = builder.Build(context, Guid.NewGuid());
        var tools = builder.OfferTools(definition, context);
        var messages = PromptContextBuilder.WithToolEnvironmentSystem(request.Messages, context, tools,
            ToolConfigurationGates.AllowAll, [ToolRegistry.Get(ToolCatalog.WorkspaceWrite).ModelDefinition]);
        var environment = Assert.Single(messages, m => m.Text.StartsWith(PromptContextBuilder.ToolEnvironmentPrefix)).Text;
        Assert.Contains("- workspace.write:", environment);
        Assert.DoesNotContain(ToolCatalog.BrowserFillCredential, environment);
        var finalizing = PromptContextBuilder.WithToolEnvironmentSystem(messages, context, [], ToolConfigurationGates.AllowAll);
        Assert.DoesNotContain(finalizing, m => m.Text.Contains(CapabilityUsageGuidance.CatalogPrefix));
    }

    [Fact]
    public async Task Guide_explains_eligible_interfaces_without_schemas_or_procedures_and_updates_after_load()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.CapabilitiesLoad,
            ToolCatalog.WorkspaceWrite, ToolCatalog.BrowserFillCredential, "browser.mouse");
        var context = CapabilityProjectionTests.Context(definition) with
        {
            AgentInstanceId = Guid.NewGuid(),
            PinnedSkillCatalog = [new("definition:procedure", SkillOrigin.Definition, "procedure", "Procedure", "Optional help",
                "PRIVATE_PROCEDURE_BODY", SkillProjection.OnDemand, [ToolCatalog.BrowserFillCredential], [])],
            ActiveSkillKeys = []
        };
        var builder = new PromptContextBuilder(ToolConfigurationGates.AllowAll);
        var request = builder.Build(context, Guid.NewGuid());
        var environment = Assert.Single(request.Messages, m => m.Text.StartsWith(PromptContextBuilder.ToolEnvironmentPrefix)).Text;
        Assert.Contains("- workspace.write: Create a UTF-8 file", environment);
        Assert.Contains("- browser.fill_credential: Fill an existing-account password input", environment);
        Assert.DoesNotContain("browser.mouse", environment); // Authorized but ineligible for this model.
        Assert.DoesNotContain("PRIVATE_PROCEDURE_BODY", string.Join('\n', request.Messages.Select(m => m.Text)));
        Assert.DoesNotContain("\"properties\"", environment);
        Assert.DoesNotContain(builder.OfferTools(definition, context), t => t.Name == ToolCatalog.BrowserFillCredential);
        var loaded = context with { LoadedCapabilityIds = [ToolCatalog.BrowserFillCredential] };
        var updated = builder.BuildSections(loaded).EnvironmentSystem;
        Assert.Contains("Tools offered now (schemas attached):", updated);
        Assert.Contains(ToolCatalog.BrowserFillCredential, updated);
        Assert.DoesNotContain("- browser.fill_credential:", updated);
        Assert.Contains("- workspace.write:", updated);
        Assert.Empty(loaded.ActiveSkillKeys!);
    }

    [Fact]
    public async Task Guide_does_not_expose_denied_unconfigured_or_detached_only_restricted_interfaces()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.CapabilitiesLoad,
            ToolCatalog.WorkspaceRead, ToolCatalog.BrowserFillCredential);
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid() };
        var unavailable = new PromptContextBuilder().BuildSections(context).EnvironmentSystem;
        Assert.Contains("- workspace.read:", unavailable);
        Assert.DoesNotContain(ToolCatalog.BrowserFillCredential, unavailable);
        Assert.DoesNotContain(ToolCatalog.EmailSend, unavailable);
        var detached = context with { DetachedExecution = true, Trigger = new(Guid.NewGuid(), TriggerKind.ScheduledOccurrence, null) };
        Assert.DoesNotContain(ToolCatalog.BrowserFillCredential, new PromptContextBuilder(ToolConfigurationGates.AllowAll).BuildSections(detached).EnvironmentSystem);
        var toolLess = new PromptContextBuilder(ToolConfigurationGates.AllowAll).BuildSections(context with { ModelSupportsTools = false }).EnvironmentSystem;
        Assert.Contains("Tools offered now (schemas attached): (none).", toolLess);
        Assert.DoesNotContain(CapabilityUsageGuidance.CatalogPrefix, toolLess);
    }

    [Fact]
    public async Task Broad_registry_metadata_stays_bounded_and_does_not_expand_projection()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolRegistry.AllKnownNames().ToArray());
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), ModelSupportsVision = true };
        var tools = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll);
        var guide = CapabilityUsageGuidance.BuildCatalog(definition, context, ToolConfigurationGates.AllowAll, tools);
        Assert.InRange(guide.Length, 1, CapabilityUsageGuidance.MaxCatalogCharacters);
        Assert.Contains("Families:", guide);
        Assert.Contains("- browser.fill_credential:", guide);
        Assert.Contains("- workspace.write:", guide);
        Assert.DoesNotContain(tools, t => t.Name == ToolCatalog.WorkspaceWrite);
        Assert.Equal(tools, ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll));
        Assert.Empty(context.ActiveSkillKeys ?? []);
        Assert.Empty(context.LoadedCapabilityIds ?? []);
    }

    [Theory]
    [InlineData("sign in with saved password")]
    [InlineData("protected credential")]
    [InlineData("browser login")]
    public async Task Credential_intents_find_protected_sink_instead_of_ordinary_typing(string query)
    {
        var definition = await CapabilityProjectionTests.Definition(ToolRegistry.AllKnownNames().ToArray());
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid() };
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query, limit = 1 }));
        var loaded = CapabilityDiscoveryMatcher.Load(definition, context, ToolConfigurationGates.AllowAll, args.RootElement, 0);
        Assert.Equal([ToolCatalog.BrowserFillCredential], loaded.Loaded);
    }
}
