using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class HarnessChatAuthoringTests
{
    [Fact]
    public async Task Inspect_separates_future_authoring_from_the_current_session_pin()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.ToolSelection], [], []));
        var sessions = services.GetRequiredService<AgentCore.Application.Sessions.SessionManager>();
        var current = await sessions.CreateForInstanceAsync(instance.InstanceId, AgentCore.Domain.Conversation.SessionMode.Text);
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        var args = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = instance.HarnessManagement!.PolicyRevision,
            id = ToolCatalog.WorkspaceMove, enabled = false, expected = "Future sessions omit move.", observed = "Owner removes the capability." });
        using var action = JsonDocument.Parse(args);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), "harness.tool.select", ToolActionHash.Compute("harness.tool.select", action.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var saved = await executor.ExecuteAsync(current.Definition, current.SessionId, new("change", "harness.tool.select", args), 100000, approvalGrant: grant, admission: admission);
        Assert.Contains("\"saved\":true", saved.Text);
        var inspection = await executor.ExecuteAsync(current.Definition, current.SessionId, new("inspect", "harness.inspect", "{}"), 100000, admission: admission);
        using var json = JsonDocument.Parse(inspection.Text);
        var root = json.RootElement;
        Assert.Equal(17, root.GetProperty("activeDefinitionVersion").GetInt32());
        Assert.Equal(16, root.GetProperty("currentSessionPinnedDefinitionVersion").GetInt32());
        Assert.True(root.GetProperty("changesApplyToFutureSessions").GetBoolean());
        Assert.True(root.TryGetProperty("authoringEligibleTools", out _));
        Assert.DoesNotContain(ToolCatalog.WorkspaceMove, root.GetProperty("activeDefinitionAuthorizedCapabilities").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains(ToolCatalog.WorkspaceMove, RoleEnvironments.Of((await sessions.GetAsync(current.SessionId)).Definition).ToolList);
        var fresh = await sessions.CreateForInstanceAsync(instance.InstanceId, AgentCore.Domain.Conversation.SessionMode.Text);
        Assert.Equal(17, fresh.Definition.Version);
        Assert.DoesNotContain(ToolCatalog.WorkspaceMove, RoleEnvironments.Of(fresh.Definition).ToolList);
    }

    [Fact]
    public async Task Managed_chat_forks_the_runtime_effective_built_in_when_a_durable_version_collides()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var definitions = services.GetRequiredService<IAgentDefinitionStore>();
        var builtIn = (await definitions.GetAsync("general-assistant", 16))!;
        var adminStore = services.GetRequiredService<IAgentDefinitionAdminStore>();
        var now = DateTimeOffset.UtcNow;
        var collision = await adminStore.CreateDraftAsync(new AgentDefinitionDraftCreate(builtIn.Id,
            AgentDefinitionCandidate.FromDefinition(builtIn) with { SystemInstructions = "Durable collision sentinel." },
            DefinitionDraftSourceKind.New, null, now));
        var durable = await adminStore.PublishDraftAsync(new AgentDefinitionDraftPublish(collision.DraftId,
            collision.Revision, Enumerable.Range(1, 15).ToArray(), now));
        Assert.Equal(16, durable.Version);
        Assert.Equal(builtIn.SystemInstructions, (await definitions.GetAsync(builtIn.Id, 16))!.SystemInstructions);

        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync(builtIn.Id, 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var context = (await authoring.ChatContextAsync(instance.InstanceId, default))!;
        var payload = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = context.PolicyRevision,
            skill = new { name = "Kubernetes review", description = "Guide a safe cluster review.",
                procedure = "Confirm context and namespace before proposing a change." } });
        var result = await services.GetRequiredService<SessionToolExecutor>().ExecuteAsync(builtIn, Guid.NewGuid(),
            new("save", "harness.skill.upsert", payload), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
        Assert.True(result.Text.Contains("\"saved\":true", StringComparison.Ordinal), result.Text);
        var updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(16, updated.HarnessManagement!.Preparation!.BaseVersion);
        Assert.Equal(DefinitionDraftSourceKind.ForkBuiltIn,
            (await adminStore.GetDraftAsync(updated.HarnessManagement.Preparation.DraftId))!.SourceKind);
        var future = (await definitions.GetAsync(builtIn.Id, updated.ActiveVersion))!;
        Assert.Equal(builtIn.SystemInstructions, future.SystemInstructions);
        Assert.NotEqual(durable.Payload.SystemInstructions, future.SystemInstructions);
        Assert.Contains(future.SkillList, skill => skill.Name == "Kubernetes review");
    }

    [Fact]
    public async Task Managed_chat_can_save_twice_from_built_in_and_durable_sources_with_inherited_unavailable_browser_tool()
    {
        await using var factory = new BrowserUnavailableFactory();
        var services = factory.Services;
        Assert.False(services.GetRequiredService<IToolConfigurationGate>().IsConfigured(ToolCatalog.BrowserNavigate));
        var definitions = services.GetRequiredService<IAgentDefinitionStore>();
        var builtIn = (await definitions.GetAsync("general-assistant", 16))!;
        Assert.Contains(ToolCatalog.BrowserNavigate, RoleEnvironments.Of(builtIn).ToolList);
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync(builtIn.Id, 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var context = (await authoring.ChatContextAsync(instance.InstanceId, default))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        async Task<AgentInstance> SaveAsync(int version, string name)
        {
            var payload = JsonSerializer.Serialize(new { expectedVersion = version, policyRevision = context.PolicyRevision,
                skill = new { name, description = "Guide a safe cluster review.",
                    procedure = "Confirm context and namespace before proposing a change." } });
            var result = await executor.ExecuteAsync(builtIn, Guid.NewGuid(),
                new(name, "harness.skill.upsert", payload), 100000,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
            Assert.Contains("\"saved\":true", result.Text);
            return (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        }

        var first = await SaveAsync(16, "Kubernetes review");
        Assert.Equal(DefinitionDraftSourceKind.ForkBuiltIn,
            (await services.GetRequiredService<IAgentDefinitionAdminStore>().GetDraftAsync(first.HarnessManagement!.Preparation!.DraftId))!.SourceKind);
        var second = await SaveAsync(first.ActiveVersion, "Kubernetes study");
        Assert.Equal(DefinitionDraftSourceKind.ForkDurable,
            (await services.GetRequiredService<IAgentDefinitionAdminStore>().GetDraftAsync(second.HarnessManagement!.Preparation!.DraftId))!.SourceKind);
        Assert.True(second.ActiveVersion > first.ActiveVersion);
        var future = (await definitions.GetAsync(builtIn.Id, second.ActiveVersion))!;
        Assert.Equal(builtIn.SkillList.Count + 2, future.SkillList.Count);
        Assert.Contains(future.SkillList, skill => skill.Name == "Kubernetes review");
        Assert.Contains(future.SkillList, skill => skill.Name == "Kubernetes study");
        Assert.Contains(ToolCatalog.BrowserNavigate, RoleEnvironments.Of(future).ToolList);
    }

    private sealed class BrowserUnavailableFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolConfigurationGate>();
                services.AddSingleton(ToolConfigurationGates.Unconfigured);
            });
        }
    }

    [Fact]
    public async Task Inspection_example_can_save_a_skill_and_activate_its_exact_procedure()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        var inspection = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("inspect", "harness.inspect", "{}"), 100000, admission: admission);
        using var json = JsonDocument.Parse(inspection.Text);
        var example = json.RootElement.GetProperty("skillUpsertExample");
        var result = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("save", "harness.skill.upsert", example.GetRawText()), 100000, admission: admission);
        Assert.Contains("\"saved\":true", result.Text);
        var updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(updated.DefinitionId, updated.ActiveVersion))!;
        var skill = Assert.Single(future.SkillList, skill => skill.Id == "operations-review");
        Assert.Equal("operations-review", skill.Id);
        Assert.Contains("\"skillId\":\"operations-review\"", result.Text);
        Assert.DoesNotContain(updated.HarnessManagement!.Preparation!.Evidence, evidence => evidence.Actor == "Agent");
        Assert.Contains(updated.HarnessManagement.Preparation.Evidence, evidence => evidence.Actor == "Core"
            && evidence.Check == "Candidate Skill activation" && evidence.Status == HarnessEvidenceStatus.Verified);
        var plan = SkillLoadAdmission.Plan(future, [], 0, [skill.Id]);
        Assert.Contains(skill.Id, plan.Admitted);
        Assert.Contains(example.GetProperty("skill").GetProperty("procedure").GetString()!, PromptContextBuilder.BuildActiveSkillSystem(future, plan.Admitted));
        Assert.DoesNotContain(pinned.SkillList, skill => skill.Id == "operations-review");
    }

    [Theory]
    [InlineData("{\"id\":\"INVALID ID\"}", "skill id")]
    [InlineData("{\"name\":null}", "Skill name")]
    [InlineData("{\"description\":null}", "Skill description")]
    [InlineData("{\"activationKeywords\":null}", "activationKeywords")]
    [InlineData("{\"activationKeywords\":[\"cluster\",\"cluster\"]}", "activationKeywords")]
    [InlineData("{\"activationKeywords\":{\"keywords\":[\"cluster\"]}}", "Payload field types")]
    public async Task Invalid_skill_payload_has_actionable_guidance_and_a_fresh_corrected_call_succeeds(string patch, string expectedError)
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        var inspection = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("inspect", "harness.inspect", "{}"), 100000, admission: admission);
        var example = System.Text.Json.Nodes.JsonNode.Parse(inspection.Text)!["skillUpsertExample"]!;
        var invalid = example.DeepClone();
        foreach (var field in System.Text.Json.Nodes.JsonNode.Parse(patch)!.AsObject()) invalid["skill"]![field.Key] = field.Value?.DeepClone();
        var rejected = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("bad", "harness.skill.upsert", invalid.ToJsonString()), 100000, admission: admission);
        Assert.Contains(expectedError, rejected.Text);
        var afterRejection = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(16, afterRejection.ActiveVersion);
        Assert.Null(afterRejection.HarnessManagement!.Preparation);
        var corrected = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("fixed", "harness.skill.upsert", example.ToJsonString()), 100000, admission: admission);
        Assert.Contains("\"saved\":true", corrected.Text);
    }

    [Fact]
    public async Task Explicit_hyphenated_skill_id_remains_loadable_after_publication()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var inspection = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("inspect", "harness.inspect", "{}"), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
        var example = System.Text.Json.Nodes.JsonNode.Parse(inspection.Text)!["skillUpsertExample"]!;
        example["skill"]!["id"] = "kubernetes-learning";
        var result = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("save", "harness.skill.upsert", example.ToJsonString()), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
        Assert.Contains("\"skillId\":\"kubernetes-learning\"", result.Text);
        var updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(updated.DefinitionId, updated.ActiveVersion))!;
        Assert.Contains("kubernetes-learning", SkillLoadAdmission.Plan(future, [], 0, ["kubernetes-learning"]).Admitted);
    }

    [Fact]
    public async Task Omitted_id_updates_a_unique_matching_skill_and_identical_retry_does_not_publish()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var context = (await authoring.ChatContextAsync(instance.InstanceId, default))!;
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        string Payload(int version, string procedure) => JsonSerializer.Serialize(new { expectedVersion = version,
            policyRevision = context.PolicyRevision, skill = new { name = "Kubernetes Learning and Cluster Support",
                description = "Review cluster work safely.", procedure } });
        var firstPayload = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = context.PolicyRevision,
            skill = new { name = "Kubernetes Learning and Cluster Support", description = "Review cluster work safely.",
                procedure = "Confirm context before each change.", activationKeywords = new[] { "cluster review" },
                requiredCapabilities = new[] { "chat.respond" } } });
        var first = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("first", "harness.skill.upsert",
            firstPayload), 100000, admission: admission);
        Assert.Contains("\"skillId\":\"kubernetes-learning-and-cluster-support\"", first.Text);
        var afterFirst = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var second = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("second", "harness.skill.upsert",
            Payload(afterFirst.ActiveVersion, "Confirm context, then check namespace before each change.")), 100000, admission: admission);
        Assert.Contains("\"changed\":true", second.Text);
        Assert.Contains("\"skillId\":\"kubernetes-learning-and-cluster-support\"", second.Text);
        var afterSecond = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var metadataOnly = JsonSerializer.Serialize(new { expectedVersion = afterSecond.ActiveVersion,
            policyRevision = context.PolicyRevision, skill = new { name = "Kubernetes Learning and Cluster Support",
                activationKeywords = new[] { "cluster review", "payments" } } });
        var partial = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("partial", "harness.skill.upsert",
            metadataOnly), 100000, admission: admission);
        Assert.Contains("\"changed\":true", partial.Text);
        var afterPartial = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var retry = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("retry", "harness.skill.upsert",
            Payload(afterPartial.ActiveVersion, "Confirm context, then check namespace before each change.")), 100000, admission: admission);
        Assert.Contains("\"saved\":true", retry.Text);
        Assert.Contains("\"changed\":false", retry.Text);
        var unchanged = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(afterPartial.ActiveVersion, unchanged.ActiveVersion);
        Assert.Equal(afterPartial.Revision, unchanged.Revision);
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(unchanged.DefinitionId, unchanged.ActiveVersion))!;
        var skill = Assert.Single(future.SkillList, skill => skill.Id == "kubernetes-learning-and-cluster-support");
        Assert.Equal("Confirm context, then check namespace before each change.", skill.Procedure);
        Assert.Equal(["cluster review", "payments"], skill.ActivationKeywords);
        Assert.Equal(["chat.respond"], skill.RequiredCapabilities);
    }

    [Fact]
    public async Task Omitted_id_with_ambiguous_existing_skill_names_requires_an_explicit_id()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var context = (await authoring.ChatContextAsync(instance.InstanceId, default))!;
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        string Payload(int version, string? id) => JsonSerializer.Serialize(new { expectedVersion = version,
            policyRevision = context.PolicyRevision, skill = new { id, name = "Cluster Review",
                description = "Review a cluster change.", procedure = "Check context and namespace." } });
        var first = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("one", "harness.skill.upsert", Payload(16, "cluster-review-a")), 100000,
            admission: admission);
        Assert.Contains("\"saved\":true", first.Text);
        var afterFirst = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var second = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("two", "harness.skill.upsert", Payload(afterFirst.ActiveVersion, "cluster-review-b")), 100000,
            admission: admission);
        Assert.Contains("\"saved\":true", second.Text);
        var afterSecond = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var omitted = JsonSerializer.Serialize(new { expectedVersion = afterSecond.ActiveVersion, policyRevision = context.PolicyRevision,
            skill = new { name = " cluster   review ", description = "Review a cluster change.", procedure = "Check context and namespace." } });
        var rejected = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("ambiguous", "harness.skill.upsert", omitted), 100000,
            admission: admission);
        Assert.Contains("More than one existing Skill has this name", rejected.Text);
        var unchanged = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(afterSecond.ActiveVersion, unchanged.ActiveVersion);
        Assert.Equal(afterSecond.Revision, unchanged.Revision);
    }

    [Fact]
    public async Task Missing_skill_resource_returns_a_field_specific_verification_failure_without_adoption()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var inspection = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("inspect", "harness.inspect", "{}"), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
        var example = System.Text.Json.Nodes.JsonNode.Parse(inspection.Text)!["skillUpsertExample"]!;
        example["skill"]!["resourcePaths"] = new System.Text.Json.Nodes.JsonArray("knowledge/missing.md");
        var result = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("save", "harness.skill.upsert", example.ToJsonString()), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
        Assert.Contains("\"saved\":false", result.Text);
        Assert.Contains("missing_skill_resource", result.Text);
        Assert.Contains("skills[1].resourcePaths[0]", result.Text);
        var unchanged = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(16, unchanged.ActiveVersion);
        Assert.Equal(HarnessPreparationStatus.Failed, unchanged.HarnessManagement!.Preparation!.Status);
    }

    [Fact]
    public async Task Skill_knowledge_ids_resolve_from_active_knowledge_without_a_resource_path()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.Skills], [], []));
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var context = (await authoring.ChatContextAsync(instance.InstanceId, default))!;
        var knowledge = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = context.PolicyRevision,
            id = "cluster-guide", content = "Confirm context and namespace before any change.", source = "conversation:user",
            expected = "Retain safe cluster guidance.", observed = "Owner supplied the guidance." });
        var savedKnowledge = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("learn", "harness.knowledge.upsert", knowledge), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Confirm context and namespace before any change."));
        Assert.Contains("\"saved\":true", savedKnowledge.Text);
        var updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var args = JsonSerializer.Serialize(new { expectedVersion = updated.ActiveVersion, policyRevision = context.PolicyRevision,
            skill = new { name = "Cluster review", description = "Review safe cluster changes.",
                procedure = "Read cluster guidance and review the target before proposing changes.", knowledgeIds = new[] { "cluster-guide" } } });
        var savedSkill = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("skill", "harness.skill.upsert", args), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId));
        Assert.Contains("\"saved\":true", savedSkill.Text);
        updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(updated.DefinitionId, updated.ActiveVersion))!;
        Assert.Equal(KnowledgeSourcePaths.ResolveBackingPath(future.Environment!.KnowledgeList.Single(k => k.Identity == "cluster-guide")),
            Assert.Single(Assert.Single(future.SkillList, skill => skill.Id == "cluster-review").ResourcePaths));
    }

    [Fact]
    public async Task Invalid_knowledge_reference_can_be_repaired_without_rewriting_the_existing_skill()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.Skills], [], []));
        var context = (await authoring.ChatContextAsync(instance.InstanceId, default))!;
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        const string procedure = "Confirm cluster context, inspect current state, plan rollback, and verify the result.";
        var create = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = context.PolicyRevision,
            skill = new { name = "Kubernetes safety", description = "Review cluster changes safely.", procedure } });
        var created = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("create", "harness.skill.upsert", create),
            100000, admission: admission);
        Assert.Contains("\"saved\":true", created.Text);
        var afterCreate = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        string Bind(int version) => JsonSerializer.Serialize(new { expectedVersion = version,
            policyRevision = context.PolicyRevision, skill = new { id = "kubernetes-safety",
                knowledgeIds = new[] { "cluster-guide" } } });
        var rejected = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("invalid", "harness.skill.upsert",
            Bind(afterCreate.ActiveVersion)), 100000, admission: admission);
        Assert.Contains("A knowledgeIds entry is not in the active harness", rejected.Text);
        var unchanged = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(afterCreate.ActiveVersion, unchanged.ActiveVersion);
        Assert.Equal(afterCreate.Revision, unchanged.Revision);

        var knowledge = JsonSerializer.Serialize(new { expectedVersion = unchanged.ActiveVersion,
            policyRevision = context.PolicyRevision, id = "cluster-guide", content = procedure,
            source = "conversation:user", expected = "Retain the cluster checklist.", observed = "Owner supplied it." });
        var learned = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("learn", "harness.knowledge.upsert",
            knowledge), 100000, admission: admission with { OwnerTurnText = procedure });
        Assert.Contains("\"saved\":true", learned.Text);
        var afterKnowledge = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var repaired = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("repair", "harness.skill.upsert",
            Bind(afterKnowledge.ActiveVersion)), 100000, admission: admission);
        Assert.Contains("\"saved\":true", repaired.Text);
        var afterRepair = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId,
            afterRepair.ActiveVersion))!;
        var skill = Assert.Single(future.SkillList, skill => skill.Id == "kubernetes-safety");
        Assert.Equal(procedure, skill.Procedure);
        Assert.Equal("Review cluster changes safely.", skill.Description);
        Assert.Equal(KnowledgeSourcePaths.ResolveBackingPath("cluster-guide", null), Assert.Single(skill.ResourcePaths));
    }

    [Fact]
    public async Task Managed_chat_publishes_knowledge_for_future_sessions_without_source_configuration()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var admin = services.GetRequiredService<AdminAgentInstanceService>();
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await admin.CreateManagedAsync("general-assistant", 16);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var context = await executor.HarnessContextAsync(instance.InstanceId, default);
        var offered = ToolCatalog.For(pinned, new(pinned, [], "", null, AgentCore.Domain.Conversation.SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Learn this for future conversations"), Harness: context), executorGate(services));
        Assert.Contains(offered, t => t.Name == "harness.knowledge.upsert");
        Assert.DoesNotContain(offered, t => t.Name == "harness.skill.upsert");
        var args = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = context!.PolicyRevision,
            id = "owner-policy", content = "Orders need payment, shipping and fraud review.", source = "conversation:user",
            expected = "Reusable order policy can be retained.", observed = "Owner supplied enduring role knowledge." });
        var result = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("learn", "harness.knowledge.upsert", args), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Orders need payment, shipping and fraud review."));
        Assert.Contains("\"saved\":true", result.Text);
        var updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.True(updated.ActiveVersion > 16);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await authoring.CancelAsync(instance.InstanceId, updated.Revision));
        Assert.Equal(HarnessPreparationStatus.Published, (await authoring.ReviewAsync(instance.InstanceId)).State.Preparation!.Status);
        Assert.Equal(16, pinned.Version);
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(updated.DefinitionId, updated.ActiveVersion))!;
        Assert.Contains(future.Environment!.KnowledgeList, k => k.Identity == "owner-policy");
        Assert.Equal(HarnessPreparationStatus.Published, updated.HarnessManagement!.Preparation!.Status);
        Assert.Contains(updated.HarnessManagement.Preparation.Evidence, e => e.Actor == "Core" && e.Check == "Candidate knowledge readback" && e.Status == HarnessEvidenceStatus.Verified);
        var repeatedArgs = JsonSerializer.Serialize(new { expectedVersion = updated.ActiveVersion, policyRevision = context.PolicyRevision,
            id = "owner-policy", content = "Orders need payment, shipping and fraud review.", source = "conversation:user",
            expected = "Reusable order policy can be retained.", observed = "Owner supplied enduring role knowledge." });
        var repeated = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("repeat", "harness.knowledge.upsert", repeatedArgs), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Orders need payment, shipping and fraud review."));
        Assert.Contains("\"changed\":false", repeated.Text);
        var unchanged = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.Equal(updated.ActiveVersion, unchanged.ActiveVersion);
        Assert.Equal(updated.Revision, unchanged.Revision);
    }
    [Theory]
    [InlineData(HarnessManagementMode.Assisted, "harness.knowledge.upsert", HarnessManagementScope.KnowledgeResources)]
    [InlineData(HarnessManagementMode.Managed, "harness.instructions.update", HarnessManagementScope.Instructions)]
    [InlineData(HarnessManagementMode.Managed, "harness.tool.select", HarnessManagementScope.ToolSelection)]
    [InlineData(HarnessManagementMode.Managed, "harness.tool.configure", HarnessManagementScope.ToolSelection)]
    public async Task Approval_is_exact_and_authority_is_rechecked_after_the_wait(HarnessManagementMode mode, string name, HarnessManagementScope scope)
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var service = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, new(mode, [scope], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var tools = services.GetRequiredService<SessionToolExecutor>();
        var context = await tools.HarnessContextAsync(instance.InstanceId, default);
        var args = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = context!.PolicyRevision,
            id = name == "harness.tool.configure" ? "attachments.read" : name == "harness.tool.select" ? "web.fetch" : "policy",
            enabled = false, allowUnreadUnsupportedTypes = true, content = "Reusable owner policy.", source = "conversation:user", expected = "Retain policy", observed = "Owner supplied it." });
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Retain this policy.");
        var call = new ModelToolCall("change", name, args);
        Assert.Contains("approval_required", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: admission)).Text);
        Assert.Equal(16, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
        using var json = JsonDocument.Parse(args);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), name, ToolActionHash.Compute(name, json.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Contains("stale_approval", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, approvalGrant: grant with { ActionHash = "altered" }, admission: admission)).Text);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, instance.HarnessManagement!.Policy);
        var stale = await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, approvalGrant: grant, admission: admission);
        Assert.Contains("changed", stale.Text);
        Assert.Equal(16, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
        var freshArgs = args.Replace($"\"policyRevision\":{context.PolicyRevision}", $"\"policyRevision\":{instance.HarnessManagement!.PolicyRevision}");
        using var freshJson = JsonDocument.Parse(freshArgs);
        var result = await tools.ExecuteAsync(pinned, Guid.NewGuid(), call with { ArgumentsJson = freshArgs }, 100000,
            approvalGrant: grant with { ActionHash = ToolActionHash.Compute(name, freshJson.RootElement), ApprovalId = Guid.NewGuid() }, admission: admission);
        Assert.Contains("\"saved\":true", result.Text);
        Assert.Equal(ToolPolicyDecision.RequireApproval, ToolPolicy.EvaluateExecution(pinned, "http.request", executorGate(services)));
    }

    [Fact]
    public async Task Disabled_frozen_background_absent_scope_and_unread_source_cannot_author()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var service = services.GetRequiredService<HarnessManagementService>();
        var tools = services.GetRequiredService<SessionToolExecutor>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        Assert.Null(await tools.HarnessContextAsync(instance.InstanceId, default));
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], [], []));
        var ctx = await tools.HarnessContextAsync(instance.InstanceId, default);
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(pinned, "harness.knowledge.upsert", admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, Harness: ctx, SupportsTools: false)));
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(pinned, "harness.skill.upsert", admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, Harness: ctx)));
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(pinned, "harness.knowledge.upsert", admission: new(true, TriggerKind.ScheduledOccurrence, AgentInstanceId: instance.InstanceId, Harness: ctx)));
        var args = JsonSerializer.Serialize(new { expectedVersion = 16, policyRevision = ctx!.PolicyRevision, id = "unread", source = "https://unread.example", content = "Unproven content", expected = "Read it", observed = "Claimed success" });
        var call = new ModelToolCall("unread", "harness.knowledge.upsert", args);
        Assert.Contains("Read the source", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId))).Text);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, instance.HarnessManagement!.Policy with { Frozen = true });
        Assert.Null(await tools.HarnessContextAsync(instance.InstanceId, default));
        Assert.Contains("forbidden", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, Harness: ctx))).Text);
        Assert.Equal(16, instance.ActiveVersion);
    }

    [Fact]
    public void Harness_approval_shows_the_actual_semantic_operation_and_untruncated_change()
    {
        using var content = JsonDocument.Parse(JsonSerializer.Serialize(new { content = new string('x', 600) + " exact tail", expectedVersion = 16, policyRevision = 1 }));
        Assert.EndsWith("exact tail", ToolApprovalPreview.Build("harness.instructions.update", content.RootElement).Details["Change"]);
        using var tool = JsonDocument.Parse("""{"id":"http.request","enabled":false,"content":"misleading unrelated content","expectedVersion":7,"policyRevision":1}""");
        var preview = ToolApprovalPreview.Build("harness.tool.select", tool.RootElement);
        Assert.Contains("http.request", preview.Details["Change"]);
        Assert.DoesNotContain("misleading", preview.Details["Change"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"source_denied\",\"content\":\"not obtained\"}")]
    [InlineData("{\"finalUrl\":\"https://example.test\"}")]
    public void Failed_empty_or_non_object_results_never_create_source_receipts(string result)
    {
        Assert.Empty(HarnessChatTools.Sources(new("read", "web.fetch", """{"url":"https://example.test"}"""), result));
    }

    private static IToolConfigurationGate executorGate(IServiceProvider services) => services.GetRequiredService<IToolConfigurationGate>();
}
