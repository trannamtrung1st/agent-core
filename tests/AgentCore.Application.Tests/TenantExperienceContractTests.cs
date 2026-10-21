using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;
public sealed class TenantExperienceContractTests
{
    [Fact]
    public void Experience_source_and_record_intent_reject_supplied_authority_and_keep_exact_cursor()
    {
        var id = Guid.NewGuid(); var source = JsonSerializer.Serialize(new { sourceKind = "Session", sourceId = id });
        var content = new { sourceKind = "Session", sourceId = id, throughCursor = 8, goal = "Observed result", attempts = new[] { "Inspect actual source" }, decisions = Array.Empty<string>(), outcomes = Array.Empty<string>(), corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = Array.Empty<string>() };
        foreach (var (tool, input) in new[] { (TenantExperienceToolContract.Source, source), (TenantExperienceToolContract.Record, JsonSerializer.Serialize(content)) })
        {
            var args = TenantExperienceToolContract.Parse(tool, input); Assert.Equal(id, args.SourceId);
            foreach (var field in new[] { "tenantId", "principalId", "userPrincipalId", "profileId", "permission", "grantId", "observationHash", "sourceWitness" })
            {
                var bad = JsonNode.Parse(input)!.AsObject(); bad[field] = "caller"; Assert.Throws<ArgumentException>(() => TenantExperienceToolContract.Parse(tool, bad.ToJsonString()));
            }
            var envelope = JsonSerializer.Serialize(new { operation = TenantExperienceToolContract.OperationName(tool), experience = new { tool, arguments = JsonDocument.Parse(input).RootElement } });
            Assert.NotNull(TenantApprovalToolContract.Parse(new("exact", ToolCatalog.RequestAuthorization, envelope)).Experience);
        }
        Assert.Equal(8, TenantExperienceToolContract.Parse(TenantExperienceToolContract.Record, JsonSerializer.Serialize(content)).ThroughCursor);
        Assert.Throws<ArgumentException>(() => TenantExperienceToolContract.Parse(TenantExperienceToolContract.Source, source.TrimEnd('}') + ",\"sourceId\":\"" + id + "\"}"));
    }
    [Fact]
    public async Task Experience_offering_requires_admitted_and_live_opt_in_and_independent_maintenance()
    {
        var definition = (await CapabilityProjectionTests.Definition()) with { MemoryPolicy = MemoryPolicy.Disabled, SelfManagement = HarnessManagementPolicy.Disabled };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid(); var enabled = new AgentRunContinuitySettings(true, 1, false, 1);
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            Configuration = new(definition, 1, 1, [], continuitySettings: enabled), CurrentContinuitySettings = enabled,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var tools = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll); Assert.Contains(tools, t => t.Name == TenantExperienceToolContract.Source); Assert.Contains(tools, t => t.Name == TenantExperienceToolContract.Record);
        Assert.DoesNotContain(tools, t => t.Name == TenantExperienceToolContract.Consolidate); Assert.Contains("Experience", Assert.Single(tools, t => t.Name == ToolCatalog.ContinuityGet).ParametersJson);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { Configuration = null }, ToolConfigurationGates.AllowAll), t => TenantExperienceToolContract.IsTool(t.Name));
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { CurrentContinuitySettings = enabled with { ExperienceEnabled = false } }, ToolConfigurationGates.AllowAll), t => TenantExperienceToolContract.IsTool(t.Name));
        var maintenance = enabled with { AllowMaintenance = true }; context = context with { Configuration = new(definition, 1, 1, [], continuitySettings: maintenance), CurrentContinuitySettings = maintenance };
        Assert.Contains(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == TenantExperienceToolContract.Consolidate);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { CurrentContinuitySettings = enabled }, ToolConfigurationGates.AllowAll), t => t.Name == TenantExperienceToolContract.Consolidate);
    }
    [Fact]
    public void Experience_exact_continuity_read_has_no_historical_window_or_owner_selector()
    {
        var args = JsonSerializer.Serialize(new { kind = "Experience", id = Guid.NewGuid() }); Assert.Equal("Experience", TenantContinuityToolContract.Parse(ToolCatalog.ContinuityGet, args).Kind);
        foreach (var field in new[] { "afterEntrySequence", "limit", "userId", "profileId", "tenantId" })
        { var bad = JsonNode.Parse(args)!.AsObject(); bad[field] = 1; Assert.Throws<ArgumentException>(() => TenantContinuityToolContract.Parse(ToolCatalog.ContinuityGet, bad.ToJsonString())); }
    }
}
