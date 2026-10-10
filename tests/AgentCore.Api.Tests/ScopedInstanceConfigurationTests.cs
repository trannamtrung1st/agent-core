using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Application.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ScopedInstanceConfigurationTests
{
    private static WebApplicationFactory<Program> Host(bool sqlite, string db) => sqlite ? new ExperienceHost(db) : new AgentCoreApiFactory();
    private static WebApplicationFactory<Program> ControlledHost(bool sqlite, string db) => Host(sqlite, db).WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services => {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && (d.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService) || d.ImplementationType == typeof(AgentCore.Api.BackgroundOccurrenceIntakeHostedService) || d.ImplementationType == typeof(AgentCore.Api.TriggerSchedulerHostedService))).ToArray())
                services.Remove(descriptor);
        }));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_settings_and_resource_journey_preserves_sparse_values_bytes_and_conflicts(bool sqlite)
    {
        var db = Path.Combine(Path.GetTempPath(), $"scoped-{Guid.NewGuid():N}.db");
        await using var host = Host(sqlite, db);
        using var client = host.CreateClient(); TestOwnerCapability.Apply(client, host.Services);
        var admin = host.Services.GetRequiredService<AdminAgentInstanceService>();
        var owner = await admin.CreateManagedAsync("general-assistant", 21);
        var other = await admin.CreateManagedAsync("general-assistant", 21);
        var root = $"/api/v2/admin/agent-instances/{owner.InstanceId}";
        var settings = await client.GetFromJsonAsync<InstanceSettingsSection[]>(root + "/settings");
        Assert.All(settings!, s => Assert.Empty(s.Overrides));
        var changed = await client.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedInstanceRevision = owner.Revision, set = new { maxOutputTokens = 2048 }, clear = Array.Empty<string>() }); changed.EnsureSuccessStatusCode();
        var section = (await changed.Content.ReadFromJsonAsync<InstanceSettingsSection>())!;
        Assert.Single(section.Overrides); Assert.Equal("instance", section.Sources["maxOutputTokens"]);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedInstanceRevision = owner.Revision, set = new { language = "fr-FR" }, clear = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(root + "/settings/memoryPolicy", new { expectedInstanceRevision = section.InstanceRevision, set = new { userPromotion = true }, clear = Array.Empty<string>() })).StatusCode);
        var resources = host.Services.GetRequiredService<AgentInstanceResourceService>();
        async Task InvalidResource(string path, string media, byte[] payload, AgentDefinitionResourceKind kind = AgentDefinitionResourceKind.Reference)
        {
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => resources.UpsertAsync(owner.InstanceId, section.InstanceRevision, null, null, path, kind, media, payload).AsTask());
            Assert.Empty((await host.Services.GetRequiredService<IAgentInstanceStore>().ReadResourcesAsync(owner.InstanceId)).InstanceResources);
        }
        await InvalidResource("../escape.md", "text/markdown", [1]);
        await InvalidResource("unsafe.html", "text/html", [1]);
        await InvalidResource("secret.md", "text/markdown", Encoding.UTF8.GetBytes("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456"));
        await InvalidResource("large.pdf", "application/pdf", new byte[8 * 1024 * 1024 + 1]);
        var bytes = new byte[] { 0, 1, 255, 42, 128 };
        var form = new MultipartFormDataContent(); form.Add(new StringContent(section.InstanceRevision.ToString()), "expectedInstanceRevision"); form.Add(new StringContent("reference/report.bin"), "logicalPath"); form.Add(new StringContent("Reference"), "kind");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new("application/pdf"); form.Add(file, "file", "report.bin");
        var uploaded = await client.PostAsync(root + "/resources", form); uploaded.EnsureSuccessStatusCode();
        var catalog = (await uploaded.Content.ReadFromJsonAsync<InstanceResourceCatalog>())!;
        var resource = Assert.Single(catalog.Resources, r => r.Origin == "Instance");
        Assert.StartsWith("instance:", resource.Key); Assert.Equal("/agent/instance/resources/reference/report.bin", resource.VirtualPath);
        Assert.Equal(bytes, await client.GetByteArrayAsync(root + "/resources/" + Uri.EscapeDataString(resource.Key) + "/content"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/admin/agent-instances/{other.InstanceId}/resources/{Uri.EscapeDataString(resource.Key)}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedInstanceRevision = catalog.InstanceRevision, set = new { unknown = 1 }, clear = Array.Empty<string>() })).StatusCode);
        var reset = await client.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedInstanceRevision = catalog.InstanceRevision, set = new { }, clear = new[] { "maxOutputTokens" } }); reset.EnsureSuccessStatusCode();
        owner = (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
        await admin.SetLifecycleAsync(owner.InstanceId, AgentInstanceLifecycle.Archived, owner.Revision);
        var frozen = (await client.GetFromJsonAsync<InstanceResourceCatalog>(root + "/resources"))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(root + $"/resources/{Uri.EscapeDataString(resource.Key)}?expectedInstanceRevision={frozen.InstanceRevision}&expectedRevision={resource.Revision}")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_session_new_turn_refreshes_exact_version_settings_resources_and_preserves_old_run(bool sqlite)
    {
        var db = Path.Combine(Path.GetTempPath(), $"scoped-run-{Guid.NewGuid():N}.db");
        Guid instanceId, sessionId, firstRunId; AgentRunConfiguration original;
        await using (var host = Host(sqlite, db))
        {
            var s = host.Services; var admin = s.GetRequiredService<AdminAgentInstanceService>();
            var owner = await admin.CreateManagedAsync("general-assistant", 21); instanceId = owner.InstanceId;
            var resources = s.GetRequiredService<AgentInstanceResourceService>();
            var catalog = await resources.UpsertAsync(instanceId, owner.Revision, null, null, "policy.md", AgentDefinitionResourceKind.Knowledge, "text/markdown", Encoding.UTF8.GetBytes("ORIGINAL_BYTES"));
            var resource = Assert.Single(catalog.Resources, r => r.Origin == "Instance");
            var sessions = s.GetRequiredService<SessionManager>();
            var selected = s.GetRequiredService<IModelCatalog>().DefaultKey;
            var snapshot = await sessions.CreateForInstanceAsync(instanceId, SessionMode.Text, modelKey: selected, modelSource: ModelSelectionSource.User); sessionId = snapshot.SessionId;
            var output = new Output(); await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, output);
            await runtime.AttachAsync(); await runtime.SubmitUserTextAsync("Hello"); await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var store = s.GetRequiredService<IAgentRunStore>(); var runOwner = new AgentRunOwner(instanceId, LocalUserProfile.Id);
            var first = Assert.Single(await store.ListForSessionAsync(runOwner, sessionId)); firstRunId = first.AgentRunId;
            Assert.True(first.IsTerminal, string.Join(";", output.Errors)); original = first.Admission.Configuration!;
            Assert.Equal(21, original.Definition.Version);
            owner = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!;
            await s.GetRequiredService<AgentInstanceSettingsService>().PatchAsync(instanceId, "conversationPolicy", owner.Revision,
                new Dictionary<string, JsonElement> { ["maxOutputTokens"] = JsonSerializer.SerializeToElement(2048) }, []);
            owner = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!;
            await resources.UpsertAsync(instanceId, owner.Revision, AgentInstanceResourceService.ParseKey(resource.Key).Id, resource.Revision,
                "policy.md", AgentDefinitionResourceKind.Knowledge, "text/markdown", Encoding.UTF8.GetBytes("UPDATED_BYTES"));
            owner = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!;
            var preview = await admin.PreviewVersionAsync(instanceId, 22);
            Assert.Contains("conversationPolicy.maxOutputTokens", preview.PreservedInstanceOverrides);
            await admin.ReassociateActiveVersionAsync(instanceId, 22, owner.Revision);
            await runtime.SubmitUserTextAsync("Hello again"); await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var runs = await store.ListForSessionAsync(runOwner, sessionId); Assert.Equal(2, runs.Count);
            var next = runs.Single(r => r.AgentRunId != firstRunId);
            Assert.True(next.IsTerminal, string.Join(";", output.Errors)); Assert.Equal(22, next.DefinitionVersion);
            Assert.Equal(2048, next.Admission.Configuration!.Definition.ConversationPolicy.MaxOutputTokens);
            Assert.Equal(selected, next.PinnedModel.CatalogKey); Assert.Equal(ModelSelectionSource.User, runtime.Snapshot.ModelSelection!.SelectionSource);
            Assert.Equal(21, runtime.Snapshot.Definition.Version); Assert.Equal(2, runtime.Snapshot.Entries.Count(e => e.Role == ConversationRole.User));
            var reader = s.GetRequiredService<IRoleKnowledgeContentResolver>();
            Assert.Equal("ORIGINAL_BYTES", await reader.ReadContentAsync(original.Definition, resource.Key));
            Assert.Equal("UPDATED_BYTES", await reader.ReadContentAsync(next.Admission.Configuration.Definition, resource.Key));
            Assert.Equal(original.ConfigurationHash, (await store.GetAsync(runOwner, firstRunId))!.Admission.Configuration!.ConfigurationHash);
        }
        if (sqlite)
        {
            await using var reopened = Host(true, db);
            var run = (await reopened.Services.GetRequiredService<IAgentRunStore>().GetAsync(new(instanceId, LocalUserProfile.Id), firstRunId))!;
            Assert.Equal(original.ConfigurationHash, run.Admission.Configuration!.ConfigurationHash);
            Assert.Equal("ORIGINAL_BYTES", await reopened.Services.GetRequiredService<IRoleKnowledgeContentResolver>().ReadContentAsync(run.Admission.Configuration.Definition, Assert.Single(original.Resources, r => r.Key.StartsWith("instance:")).Key));
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Selected_shared_promotion_keeps_other_instances_and_local_overrides_isolated(bool sqlite)
    {
        await using var host = Host(sqlite, Path.Combine(Path.GetTempPath(), $"scoped-promote-{Guid.NewGuid():N}.db"));
        var services = host.Services; var admin = services.GetRequiredService<AdminAgentInstanceService>();
        var owner = await admin.CreateManagedAsync("general-assistant", 21); var other = await admin.CreateManagedAsync("general-assistant", 21);
        var harness = services.GetRequiredService<HarnessManagementService>();
        owner = await harness.ConfigureAsync(owner.InstanceId, owner.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.Instructions], [], []));
        var settings = services.GetRequiredService<AgentInstanceSettingsService>();
        await settings.PatchAsync(owner.InstanceId, "instructions", owner.Revision,
            new Dictionary<string,JsonElement> { ["systemInstructions"] = JsonSerializer.SerializeToElement("Selected reusable instructions.") }, []);
        owner = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
        await settings.PatchAsync(owner.InstanceId, "conversationPolicy", owner.Revision,
            new Dictionary<string,JsonElement> { ["maxOutputTokens"] = JsonSerializer.SerializeToElement(2048) }, []);
        owner = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
        var resources = services.GetRequiredService<AgentInstanceResourceService>();
        var added = await resources.UpsertAsync(owner.InstanceId, owner.Revision, null, null, "selected.md", AgentDefinitionResourceKind.Knowledge, "text/markdown", Encoding.UTF8.GetBytes("Selected reusable knowledge."));
        var selected = Assert.Single(added.Resources, r => r.Origin == "Instance");
        var unselected = await resources.UpsertAsync(owner.InstanceId, added.InstanceRevision, null, null, "private.md", AgentDefinitionResourceKind.Knowledge, "text/markdown", Encoding.UTF8.GetBytes("Unselected private knowledge."));
        owner = await harness.ProposeSharedAsync(owner.InstanceId, unselected.InstanceRevision, "Explicitly promote selected instructions and knowledge.",
            new Dictionary<string,IReadOnlyList<string>> { ["instructions"] = new[] { "systemInstructions" } }, [selected.Key]);
        var proposed = await harness.ReviewAsync(owner.InstanceId);
        Assert.Equal("Selected reusable instructions.", proposed.Draft!.Candidate.SystemInstructions);
        Assert.NotEqual(2048, proposed.Draft.Candidate.ConversationPolicy!.MaxOutputTokens);
        var reviewedDraft = await services.GetRequiredService<AgentDefinitionLifecycleService>().UpdateDraftAsync(
            proposed.Draft.DraftId, proposed.Draft.Revision, proposed.Draft.Candidate with {
                Skills = [.. proposed.Draft.Candidate.SkillList, new("scoped-reference", "Scoped reference", "Read scoped reference", "Consult the selected reference.", SkillProjection.OnDemand, true, [], ["instance-promoted/selected.md"])]
            });
        owner = await harness.RecordAgentEvidenceAsync(owner.InstanceId, owner.HarnessManagement!.Preparation!.PreparationId,
            new("Agent", reviewedDraft.Revision, "Reviewed scoped Skill dependency", HarnessEvidenceStatus.Verified,
                "The shared Skill binds only the selected resource.", "The draft Skill binds instance-promoted/selected.md; unselected local knowledge is absent."));
        owner = await harness.VerifyAsync(owner.InstanceId, owner.HarnessManagement!.Preparation!.PreparationId);
        var ready = await harness.ReviewAsync(owner.InstanceId);
        Assert.Equal(HarnessPreparationStatus.Ready, ready.State.Preparation!.Status);
        owner = await harness.PromoteAsync(owner.InstanceId, owner.Revision, ready.Draft!.Revision);
        Assert.True(owner.ActiveVersion > 21);
        Assert.Equal(21, (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(other.InstanceId))!.ActiveVersion);
        Assert.Equal(2048, (await settings.ReadAsync(owner.InstanceId, "conversationPolicy")).Effective["maxOutputTokens"]);
        var published = await services.GetRequiredService<IAgentDefinitionResourceAdminStore>().ListPublicationResourcesAsync(owner.DefinitionId, owner.ActiveVersion);
        var shared = Assert.Single(published, r => r.LogicalPath == "instance-promoted/selected.md");
        Assert.DoesNotContain(published, r => r.LogicalPath.Contains("private.md"));
        Assert.Equal("instance-promoted/selected.md", shared.LogicalPath);
        var catalog = await resources.ListAsync(owner.InstanceId); var inherited = Assert.Single(catalog.Resources, r => r.Origin == "Definition" && r.Key == "definition:" + shared.ResourceId);
        Assert.Contains("definition:scoped-reference", inherited.Dependencies);
        var refused = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => resources.SetEnabledAsync(
            owner.InstanceId, inherited.Key, catalog.InstanceRevision, inherited.Revision, false).AsTask());
        Assert.Contains("definition:scoped-reference", refused.Message);
        Assert.Equal(catalog.InstanceRevision, (await resources.ListAsync(owner.InstanceId)).InstanceRevision);
        var skills = services.GetRequiredService<AgentInstanceSkillService>();
        var dependent = await skills.InspectAsync(owner.InstanceId, "definition:scoped-reference");
        await skills.WriteAsync(owner.InstanceId, "set_enabled", dependent.Key, dependent.Revision, enabled: false);
        catalog = await resources.ListAsync(owner.InstanceId);
        var disabled = await resources.SetEnabledAsync(owner.InstanceId, inherited.Key, catalog.InstanceRevision, inherited.Revision, false);
        Assert.False(disabled.Resources.Single(r => r.Key == inherited.Key).Enabled);
        var reset = await resources.SetEnabledAsync(owner.InstanceId, inherited.Key, disabled.InstanceRevision, disabled.Resources.Single(r => r.Key == inherited.Key).Revision, null);
        Assert.True(reset.Resources.Single(r => r.Key == inherited.Key).Enabled);
        var localCopy = await resources.CopyAsync(owner.InstanceId, inherited.Key, reset.InstanceRevision, "instance-promoted/selected.md");
        Assert.Contains(localCopy.Resources, r => r.Origin == "Instance" && r.SourceDefinitionResourceId == shared.ResourceId && r.VirtualPath == "/agent/instance/resources/instance-promoted/selected.md");
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => resources.CopyAsync(owner.InstanceId, inherited.Key, localCopy.InstanceRevision, "instance-promoted/selected.md").AsTask())).Code);
        var fork = await services.GetRequiredService<AgentDefinitionLifecycleService>().ForkDraftAsync(owner.DefinitionId, owner.ActiveVersion, DefinitionDraftSourceKind.ForkDurable);
        var copied = await services.GetRequiredService<IAgentDefinitionResourceAdminStore>().ListDraftResourcesAsync(fork.DraftId);
        Assert.Equal(published.Select(r => r.ResourceId).Order(), copied.Select(r => r.ResourceId).Order());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Waiting_run_retains_configuration_content_and_budget_after_adoption_and_reopen(bool sqlite)
    {
        var db = Path.Combine(Path.GetTempPath(), $"scoped-wait-{Guid.NewGuid():N}.db");
        var host = ControlledHost(sqlite, db);
        try
        {
            var services = host.Services; var admin = services.GetRequiredService<AdminAgentInstanceService>();
            var instance = await admin.CreateManagedAsync("general-assistant", 21);
            var resources = services.GetRequiredService<AgentInstanceResourceService>();
            var catalog = await resources.UpsertAsync(instance.InstanceId, instance.Revision, null, null, "waiting.md", AgentDefinitionResourceKind.Knowledge, "text/markdown", Encoding.UTF8.GetBytes("FROZEN_WAIT_BYTES"));
            var resource = Assert.Single(catalog.Resources, r => r.Origin == "Instance");
            var snapshot = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
            var resolved = await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(instance.InstanceId);
            var now = DateTimeOffset.UtcNow;
            var budget = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, new(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Extended)), null)
                with { RequestedCleanup = true, CleanupIntent = new(true, false) };
            var proposal = AgentRunAdmissionFactory.ForAdmittedSignal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot,
                new(Guid.NewGuid(), TriggerKind.LongSilence, "Close the browser"), now, resolved.Skills, budget: budget, configuration: resolved.Configuration);
            var runs = services.GetRequiredService<IAgentRunStore>(); var owner = proposal.Owner;
            instance = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
            await services.GetRequiredService<AgentInstanceSettingsService>().PatchAsync(instance.InstanceId, "conversationPolicy", instance.Revision,
                new Dictionary<string, JsonElement> { ["maxOutputTokens"] = JsonSerializer.SerializeToElement(2048) }, []);
            Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() =>
                runs.AdmitAsync(snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, proposal).AsTask())).Code);
            Assert.Empty(await runs.ListForSessionAsync(owner, snapshot.SessionId));
            resolved = await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(instance.InstanceId);
            proposal = AgentRunAdmissionFactory.ForAdmittedSignal(proposal.ActivationId, proposal.AgentRunId, proposal.ResponseId!.Value, snapshot,
                new(Guid.NewGuid(), TriggerKind.LongSilence, "Close the browser"), now, resolved.Skills, budget: budget, configuration: resolved.Configuration);
            var admitted = (await runs.AdmitAsync(snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, proposal)).Run;
            var generation = Guid.NewGuid();
            var claimed = await runs.ApplyAsync(owner, admitted.AgentRunId, new AgentRunCommand.Claim(admitted.Revision, now, generation, now.AddMinutes(1)));
            var call = new ModelToolCall("wait-frozen", "execution.wait", "{}");
            var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call])]), 7, 123, 400000, 200000);
            var waiting = await runs.ApplyAsync(owner, claimed.AgentRunId, new AgentRunCommand.SuspendWait(claimed.Revision, now, generation, checkpoint,
                new(call.Id, AgentRunWaitMode.Duration, [], AgentRunWaitUntil.All, now, now.AddMinutes(5))));
            instance = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
            await resources.UpsertAsync(instance.InstanceId, instance.Revision, AgentInstanceResourceService.ParseKey(resource.Key).Id, resource.Revision,
                "waiting.md", AgentDefinitionResourceKind.Knowledge, "text/markdown", Encoding.UTF8.GetBytes("NEXT_RUN_BYTES"));
            instance = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
            await admin.ReassociateActiveVersionAsync(instance.InstanceId, 22, instance.Revision);
            instance = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
            await admin.SetExecutionBudgetsAsync(instance.InstanceId, instance.Revision, new(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Standard)));
            if (sqlite) { await host.DisposeAsync(); host = ControlledHost(true, db); services = host.Services; runs = services.GetRequiredService<IAgentRunStore>(); }
            var restored = (await runs.GetAsync(owner, waiting.AgentRunId))!;
            Assert.Equal(AgentRunStatus.WaitingForSignal, restored.Status);
            Assert.Equal(123, restored.Checkpoint!.OutputBytes);
            var wake = now.AddMinutes(5);
            var resumed = await runs.ApplyAsync(owner, restored.AgentRunId, new AgentRunCommand.ResumeWait(restored.Revision, wake, Guid.NewGuid(), wake.AddMinutes(1)));
            Assert.Equal(21, resumed.DefinitionVersion); Assert.Equal(waiting.Admission.Configuration!.ConfigurationHash, resumed.Admission.Configuration!.ConfigurationHash);
            Assert.Equal(waiting.Admission.ExecutionBudget, resumed.Admission.ExecutionBudget); Assert.True(resumed.Admission.ExecutionBudget!.RequestedCleanup);
            Assert.Equal(7, resumed.Checkpoint!.StepCount); Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var resumedMessages));
            var waitResult = Assert.Single(resumedMessages!, message => message.Role == ModelRole.Tool);
            Assert.Equal(call.Id, waitResult.ToolCallId);
            Assert.Equal(123 + Encoding.UTF8.GetByteCount(waitResult.Text), resumed.Checkpoint.OutputBytes); Assert.Equal(200000, resumed.Checkpoint.ActiveExecutionMs);
            Assert.Equal(waiting.AttemptCount, resumed.AttemptCount);
            Assert.Equal("FROZEN_WAIT_BYTES", await services.GetRequiredService<IRoleKnowledgeContentResolver>().ReadContentAsync(resumed.Admission.Configuration.Definition, resource.Key));
            var next = await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(instance.InstanceId);
            Assert.Equal(22, next.Configuration.Definition.Version);
            Assert.Equal("NEXT_RUN_BYTES", await services.GetRequiredService<IRoleKnowledgeContentResolver>().ReadContentAsync(next.Configuration.Definition, resource.Key));
            Assert.Equal(ExecutionBudgetPreset.Standard, next.InstanceBudgets!.InteractiveBrowser!.Preset);
        }
        finally { await host.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Trigger_settings_reconcile_atomically_and_a_stale_registration_rolls_back_owner_adoption(bool sqlite)
    {
        await using var host = ControlledHost(sqlite, Path.Combine(Path.GetTempPath(), $"scoped-automation-{Guid.NewGuid():N}.db"));
        var services = host.Services; var admin = services.GetRequiredService<AdminAgentInstanceService>();
        var instance = await admin.CreateManagedAsync("general-assistant", 21);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var owner = new TriggerOwner(instance.InstanceId, session.ProfileId!.Value); var now = DateTimeOffset.UtcNow;
        var schedule = new OneShotSchedule(now.AddDays(1), "UTC");
        var triggers = services.GetRequiredService<ITriggerStore>();
        var automation = await triggers.CreateAsync(new(Guid.NewGuid(), owner, AutomationStatus.Active, "Retained work", new ScheduleTrigger(schedule), schedule.AtUtc,
            null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null));
        var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, 22))!;
        var plan = await services.GetRequiredService<InstanceAutomationPolicy>().PlanAsync(instance, definition with { TriggerPolicy = definition.TriggerPolicy! with { Enabled = false } }, now, default);
        Assert.Single(plan);
        var edited = await triggers.UpdateAsync(owner, automation.AutomationId, automation.Revision, "Concurrent owner edit", schedule, schedule.AtUtc, null, now);
        var instances = services.GetRequiredService<IAgentInstanceStore>();
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => instances.UpdateWithExpectedRevisionAsync(
            new(instance.InstanceId, instance.Revision, ActiveVersion: 22, AutomationPolicyChanges: plan), now).AsTask())).Code);
        Assert.Equal(instance.Revision, (await instances.FindAsync(instance.InstanceId))!.Revision);
        Assert.Equal(21, (await instances.FindAsync(instance.InstanceId))!.ActiveVersion);
        Assert.Equal(edited.Revision, (await triggers.GetAsync(owner, automation.AutomationId))!.Revision);
        var settings = services.GetRequiredService<AgentInstanceSettingsService>();
        var disabled = await settings.PatchAsync(instance.InstanceId, "triggerPolicy", instance.Revision,
            new Dictionary<string, JsonElement> { ["enabled"] = JsonSerializer.SerializeToElement(false) }, []);
        Assert.Equal(AutomationStatus.SuspendedPolicy, (await triggers.GetAsync(owner, automation.AutomationId))!.Status);
        await settings.PatchAsync(instance.InstanceId, "triggerPolicy", disabled.InstanceRevision, new Dictionary<string, JsonElement>(), ["enabled"]);
        Assert.Equal(AutomationStatus.Active, (await triggers.GetAsync(owner, automation.AutomationId))!.Status);
        Assert.Equal("Concurrent owner edit", (await triggers.GetAsync(owner, automation.AutomationId))!.Instructions);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Fresh_occurrence_resolves_adopted_configuration_and_retains_existing_session_effort(bool sqlite, bool existingSession)
    {
        await using var host = ControlledHost(sqlite, Path.Combine(Path.GetTempPath(), $"scoped-occurrence-{Guid.NewGuid():N}.db"));
        var services = host.Services;
        var admin = services.GetRequiredService<AdminAgentInstanceService>();
        var instance = await admin.CreateManagedAsync("general-assistant", 21);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId,
            SessionMode.Text, speechLocaleOverride: "en-GB", reasoningEffort: "high");
        var settings = services.GetRequiredService<AgentInstanceSettingsService>();
        var saved = await settings.PatchAsync(instance.InstanceId, "conversationPolicy", instance.Revision,
            new Dictionary<string, JsonElement> { ["maxOutputTokens"] = JsonSerializer.SerializeToElement(2048) }, []);
        var resources = await services.GetRequiredService<AgentInstanceResourceService>().UpsertAsync(instance.InstanceId,
            saved.InstanceRevision, null, null, "occurrence/current.md", AgentDefinitionResourceKind.Knowledge,
            "text/markdown", Encoding.UTF8.GetBytes("Current occurrence knowledge."));
        instance = await admin.ReassociateActiveVersionAsync(instance.InstanceId, 22, resources.InstanceRevision);
        var authoring = services.GetRequiredService<AdminAutomationAuthoringService>();
        var automation = await authoring.SaveAsync(instance.InstanceId, null, 0, true, "Current configuration", "Say hello",
            new ScheduleTrigger(new OneShotSchedule(DateTimeOffset.UtcNow.AddDays(1), "UTC")), null, null,
            executionTarget: existingSession ? AutomationExecutionTarget.Existing(session.SessionId) : AutomationExecutionTarget.Background,
            completionDelivery: AutomationCompletionDelivery.None);
        await authoring.RunNowAsync(instance.InstanceId, automation.AutomationId, automation.Revision);
        await services.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        Assert.Equal(1, (await services.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
        var run = Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListRunnableAsync(DateTimeOffset.UtcNow, 100));
        Assert.Equal(22, run.DefinitionVersion);
        Assert.Equal(instance.Revision, run.Admission.Configuration!.InstanceRevision);
        Assert.Equal(2048, run.Admission.Configuration.Definition.ConversationPolicy.MaxOutputTokens);
        Assert.Contains(run.Admission.Configuration.Resources, r => r.VirtualPath == "/agent/instance/resources/occurrence/current.md");
        var retained = (await services.GetRequiredService<IMemoryStore>().LoadAsync(session.SessionId))!;
        Assert.Equal(21, retained.Definition.Version);
        Assert.Equal("en-GB", retained.SpeechLocaleOverride);
        if (existingSession)
        {
            Assert.Equal(session.SessionId, run.SessionId);
            Assert.Equal("high", run.PinnedModel.ReasoningEffort);
            Assert.True(retained.ModelSelection!.HasExplicitReasoningEffort);
        }
        else Assert.NotEqual(session.SessionId, run.SessionId);
    }

    private sealed class Output : ISessionOutput
    {
        public List<string> Errors { get; } = [];
        public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default)
        { if (output.Payload is ErrorOutput error) Errors.Add(error.SafeMessage); return ValueTask.CompletedTask; }
    }
}
