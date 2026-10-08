using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class UnattendedBrowserTests
{
    private const string Store = "http://127.0.0.1:5088";
    private const string Other = "http://127.0.0.1:5099";
    private const string PublishRef = "el_0123456789abcdefghijkl";
    private const string RepairRef = "el_abcdefghijklmnopqrstuv";

    [Theory]
    [InlineData(false, TriggerKind.UserTurn, true)]
    [InlineData(false, TriggerKind.ScheduledOccurrence, false)]
    [InlineData(true, TriggerKind.ScheduledOccurrence, false)]
    [InlineData(true, TriggerKind.ApplicationEvent, false)]
    public void Geolocation_projection_matches_direct_turn_execution(bool detached, TriggerKind kind, bool offered)
    {
        var definition=Definition() with { Environment=new RoleEnvironment(ToolAllowlist: [ToolCatalog.BrowserGeolocation]) };
        var context=Context(trusted:true,kind:kind) with { Definition=definition, DetachedExecution=detached };
        Assert.Equal(offered, ToolPolicy.IsOffered(definition,context,ToolCatalog.BrowserGeolocation,ToolConfigurationGates.AllowAll));
        var admission=Admission() with { Detached=detached,TriggerKind=kind };
        Assert.Equal(offered ? ToolPolicyDecision.RequireApproval : ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(definition,ToolCatalog.BrowserGeolocation,ToolConfigurationGates.AllowAll,admission:admission));
    }

    [Fact]
    public async Task Work_recovery_keeps_loaded_instance_content_and_new_work_resolves_current_catalog()
    {
        var browser = new RecordingBrowser(); var agents = await ActiveAgentsAsync(OwnerId); var definition = Definition();
        var now = DateTimeOffset.Parse("2026-10-07T00:00:00Z"); var localId = Guid.NewGuid().ToString("D"); var alwaysId = Guid.NewGuid().ToString("D");
        var localKey = "instance:" + localId; var alwaysKey = "instance:" + alwaysId;
        await agents.MutateSkillsAsync(new(OwnerId, 1, InstanceSkill: new(localId, OwnerId, "Accounting", "Check totals", "OLD_PROCEDURE",
            SkillProjection.OnDemand, true, [], 1, now, now, SkillAuthor.Agent)));
        await agents.MutateSkillsAsync(new(OwnerId, 2, InstanceSkill: new(alwaysId, OwnerId, "Startup", "Startup guidance", "ALWAYS_PROCEDURE",
            SkillProjection.Always, true, [], 1, now, now, SkillAuthor.Agent)));
        var work = new InMemoryWorkItemStore(); var generation = Guid.NewGuid();
        await work.CreateAsync(WorkItem.Create(WorkId, new(OwnerId, ProfileId), Provenance(now), new("synthetic-default", "synthetic", "synthetic-small", null), 3, now));
        var claimed = (await work.TryClaimAsync(WorkId, generation, now, now.AddMinutes(1)))!;
        var first = new RecordingScriptModel(false,
            () => ToolRound(Call(ToolCatalog.SkillsLoad, JsonSerializer.Serialize(new { ids = new[] { localKey } }))),
            () => [new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Temporary failure"))]);
        var retry = Assert.IsType<DurableOccurrenceRetry>(await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(claimed,
            new(Guid.NewGuid(), [new(ModelRole.User, "review")]), first, definition, TriggerKind.ScheduledOccurrence,
            (item, body, ct) => work.CheckpointAsync(item.WorkItemId, item.Revision, generation, body, null, now, ct),
            work, generation, now, Ids(), CancellationToken.None));
        Assert.Contains(first.Requests[0].Messages, m => m.Text.Contains("ALWAYS_PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(first.Requests[0].Messages, m => m.Text.Contains("OLD_PROCEDURE", StringComparison.Ordinal));
        Assert.Contains(first.Requests[1].Messages, m => m.Text.Contains("OLD_PROCEDURE", StringComparison.Ordinal));
        var pinned = DurableToolCallCheckpoint.ReadSkillState(retry.Running.Checkpoint);
        Assert.Equal(1, pinned.LoadCount); Assert.Equal(new[] { alwaysKey, localKey }.Order(), pinned.ActiveKeys.Order());
        await agents.MutateSkillsAsync(new(OwnerId, 3, InstanceSkill: new(localId, OwnerId, "Accounting", "Check totals", "NEW_PROCEDURE",
            SkillProjection.OnDemand, true, [], 2, now, now, SkillAuthor.Agent), ExpectedSkillRevision: 1));
        await agents.MutateSkillsAsync(new(OwnerId, 4, InstanceSkill: new(Guid.NewGuid().ToString("D"), OwnerId, "Later", "Created after admission", "LATER_PROCEDURE",
            SkillProjection.OnDemand, true, [], 1, now, now, SkillAuthor.Agent)));
        await work.RecoverExpiredClaimsAsync(now.AddMinutes(1)); generation = Guid.NewGuid();
        var reclaimed = (await work.TryClaimAsync(WorkId, generation, now.AddMinutes(1), now.AddMinutes(5)))!;
        var resumed = new RecordingScriptModel(false, () => CompleteRound("Recovered snapshot"));
        Assert.IsType<DurableOccurrenceCompleted>(await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(reclaimed,
            new(Guid.NewGuid(), [new(ModelRole.User, "review")]), resumed, definition, TriggerKind.ScheduledOccurrence,
            (item, body, ct) => work.CheckpointAsync(item.WorkItemId, item.Revision, generation, body, null, now.AddMinutes(1), ct),
            work, generation, now.AddMinutes(1), Ids(), CancellationToken.None));
        var recoveredText = string.Join("\n", Assert.Single(resumed.Requests).Messages.Select(m => m.Text));
        Assert.Contains("OLD_PROCEDURE", recoveredText); Assert.DoesNotContain("NEW_PROCEDURE", recoveredText); Assert.DoesNotContain("Created after admission", recoveredText);
        var fresh = new RecordingScriptModel(false,
            () => ToolRound(Call(ToolCatalog.SkillsLoad, JsonSerializer.Serialize(new { ids = new[] { localKey } }))),
            () => CompleteRound("Fresh snapshot"));
        await RunSecretaryModeAsync(browser, agents, definition, OwnerId, ProfileId, Guid.NewGuid(), Guid.NewGuid(), "fresh-skills", TriggerKind.ScheduledOccurrence, fresh);
        Assert.Contains(fresh.Requests[0].Messages, m => m.Text.Contains("Created after admission", StringComparison.Ordinal));
        Assert.Contains(fresh.Requests[1].Messages, m => m.Text.Contains("NEW_PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(fresh.Requests[1].Messages, m => m.Text.Contains("OLD_PROCEDURE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capability_load_reclaims_same_work_checkpoint_and_new_work_starts_empty()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var definition = Definition() with { Environment = new(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad, ToolCatalog.EmailSearch, ToolCatalog.WorkComplete]), Projection: new([])) };
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.NewGuid();
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(WorkId, new(OwnerId, ProfileId), Provenance(now),
            new("synthetic-default", "synthetic", "synthetic-small", null), 3, now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(1)))!;
        var firstModel = new RecordingScriptModel(false,
            () => ToolRound(Call(ToolCatalog.CapabilitiesLoad, "{\"query\":\"email.search\"}")),
            () => [new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Temporary failure"))]);
        var first = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(claimed,
            new(Guid.NewGuid(), [new(ModelRole.User, "review")]), firstModel, definition, TriggerKind.ScheduledOccurrence,
            (item, body, ct) => store.CheckpointAsync(item.WorkItemId, item.Revision, generation, body, null, now, ct),
            store, generation, now, Ids(), CancellationToken.None);
        var retry = Assert.IsType<DurableOccurrenceRetry>(first);
        Assert.DoesNotContain(firstModel.Requests[0].Tools!, t => t.Name == ToolCatalog.EmailSearch);
        Assert.Contains(firstModel.Requests[1].Tools!, t => t.Name == ToolCatalog.EmailSearch);
        var state = DurableToolCallCheckpoint.ReadCapabilityState(retry.Running.Checkpoint);
        Assert.Equal([ToolCatalog.EmailSearch], state.Ids);
        Assert.Equal(1, state.Calls);
        Assert.Equal(1, (await store.RecoverExpiredClaimsAsync(now.AddMinutes(1))).RecoveredCount);
        var nextGeneration = Guid.NewGuid();
        var reclaimed = (await store.TryClaimAsync(WorkId, nextGeneration, now.AddMinutes(1), now.AddMinutes(5)))!;
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.CheckpointAsync(WorkId, reclaimed.Revision, generation,
            reclaimed.Checkpoint!, null, now.AddMinutes(1)).AsTask());
        var resumedModel = new RecordingScriptModel(false, () => CompleteRound("Recovered exact interfaces"));
        var continued = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(reclaimed,
            new(Guid.NewGuid(), [new(ModelRole.User, "review")]), resumedModel, definition, TriggerKind.ScheduledOccurrence,
            (item, body, ct) => store.CheckpointAsync(item.WorkItemId, item.Revision, nextGeneration, body, null, now.AddMinutes(1), ct),
            store, nextGeneration, now.AddMinutes(1), Ids(), CancellationToken.None);
        var completed = Assert.IsType<DurableOccurrenceCompleted>(continued);
        Assert.Equal("Recovered exact interfaces", WorkCompletionRequest.Summary(completed.Text));
        Assert.Contains(Assert.Single(resumedModel.Requests).Tools!, t => t.Name == ToolCatalog.EmailSearch);
        Assert.Equal(1, DurableToolCallCheckpoint.ReadCapabilityState(completed.Running.Checkpoint).Calls);
        var fresh = new RecordingScriptModel(false, () => CompleteRound("Fresh work"));
        await RunSecretaryModeAsync(browser, agents, definition, OwnerId, ProfileId, Guid.NewGuid(), Guid.NewGuid(), "fresh-capabilities", TriggerKind.ScheduledOccurrence, fresh);
        Assert.DoesNotContain(Assert.Single(fresh.Requests).Tools!, t => t.Name == ToolCatalog.EmailSearch);
    }

    [Fact]
    public async Task Four_secretary_modes_share_one_instance_profile_and_definition()
    {
        var secretary = await LoadSecretaryV2Async();
        var instanceId = Guid.Parse("019944af-00f1-7000-8000-000000000001");
        var profileId = Guid.Parse("019944af-00f1-7000-8000-000000000002");
        var sessionId = Guid.Parse("019944af-00f1-7000-8000-000000000003");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x53, 0x45, 0x43, 0x52 };
        var browser = new RecordingBrowser { CapturePng = png };
        var agents = new InMemoryAgentInstanceStore();
        await agents.InsertAsync(new AgentInstance(instanceId, secretary.Id, secretary.Version, secretary.Identity,
            AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), initialSkills: secretary.SkillList);
        var executor = new SessionToolExecutor(
            artifacts: new InMemoryArtifactStore(TimeProvider.System),
            configurationGate: ToolConfigurationGates.AllowAll,
            browser: browser,
            agentInstances: agents);
        var interactive = new ToolExecutionAdmission(
            false,
            TriggerKind.UserTurn,
            AgentInstanceId: instanceId);

        var navigated = await executor.ExecuteAsync(
            secretary,
            sessionId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: interactive);
        var observed = await executor.ExecuteAsync(
            secretary,
            sessionId,
            Call(ToolCatalog.BrowserSnapshot, "{}"),
            ToolLimits.MaxOutputBytes,
            admission: interactive);
        Assert.DoesNotContain("error", navigated.Text, StringComparison.Ordinal);
        Assert.Contains("Published", observed.Text, StringComparison.Ordinal);
        Assert.Equal(1, browser.NavigateCalls);
        Assert.Equal(1, browser.ObserveCalls);
        Assert.Equal(0, browser.CaptureCalls);
        Assert.Equal([instanceId], browser.BoundAgents.Distinct());
        Assert.Empty(browser.UnattendedAgents);

        var acted = await executor.ExecuteAsync(
            secretary,
            sessionId,
            Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}"""),
            ToolLimits.MaxOutputBytes,
            admission: interactive);
        Assert.DoesNotContain("error", acted.Text, StringComparison.Ordinal);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(0, browser.CaptureCalls);

        var textOnly = await executor.ExecuteAsync(
            secretary,
            sessionId,
            Call(ToolCatalog.BrowserScreenshot, "{}"),
            ToolLimits.MaxOutputBytes,
            admission: interactive);
        Assert.Contains("artifactId", textOnly.Text, StringComparison.Ordinal);
        Assert.Empty(textOnly.Parts ?? []);
        Assert.Equal(1, browser.CaptureCalls);

        var captured = await executor.ExecuteAsync(
            secretary,
            sessionId,
            Call(ToolCatalog.BrowserScreenshot, "{}"),
            ToolLimits.MaxOutputBytes,
            admission: interactive with { SupportsVision = true });
        var image = Assert.IsType<ModelImageContent>(Assert.Single(captured.Parts!));
        Assert.Equal(png, image.Bytes);
        Assert.Equal(2, browser.CaptureCalls);
        Assert.Equal(1, browser.ActCalls);

        var scheduled = await RunSecretaryModeAsync(
            browser,
            agents,
            secretary,
            instanceId,
            profileId,
            Guid.Parse("019944af-00f1-7000-8000-000000000011"),
            Guid.Parse("019944af-00f1-7000-8000-000000000021"),
            "schedule|store-review",
            TriggerKind.ScheduledOccurrence,
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => ToolRound(Call(ToolCatalog.WorkComplete, """{"summary":"No store changes need attention.","attentionRequired":false,"outcome":"ActionCompleted"}"""))));
        Assert.False(scheduled.AttentionRequired);
        Assert.Equal("No store changes need attention.", WorkCompletionRequest.Summary(scheduled.Text));
        Assert.Equal("Automation · Schedule", scheduled.Running.OriginLabel);

        var reactive = await RunSecretaryModeAsync(
            browser,
            agents,
            secretary,
            instanceId,
            profileId,
            Guid.Parse("019944af-00f1-7000-8000-000000000012"),
            Guid.Parse("019944af-00f1-7000-8000-000000000022"),
            "order.placed:evt-four-mode",
            TriggerKind.ApplicationEvent,
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin/orders"}""")),
                () => ToolRound(Call(ToolCatalog.WorkComplete, """{"summary":"One pending order needs review.","attentionRequired":true,"outcome":"AttentionRequested"}"""))));
        Assert.True(reactive.AttentionRequired);
        Assert.Equal("One pending order needs review.", WorkCompletionRequest.Summary(reactive.Text));
        Assert.Equal("Automation · Event", reactive.Running.OriginLabel);
        Assert.Equal(secretary.Id, scheduled.Running.Provenance.DefinitionId);
        Assert.Equal(3, scheduled.Running.Provenance.DefinitionVersion);
        Assert.Equal(scheduled.Running.Owner, reactive.Running.Owner);
        Assert.Equal(3, reactive.Running.Provenance.DefinitionVersion);
        Assert.Equal([instanceId], browser.UnattendedAgents.Distinct());
        Assert.Equal([instanceId], browser.BoundAgents.Distinct());
        Assert.Equal(2, browser.NavigateCalls);
        Assert.Equal(2, browser.CaptureCalls);
    }

    [Fact]
    public async Task Scheduled_browser_navigates_only_host_permitted_origins_and_its_own_profile()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var executor = Executor(browser, agents);
        browser.PolicyMode = BrowserPolicyMode.Restricted;
        var allowed = await executor.ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: Admission());
        var denied = await executor.ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Other}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: Admission());

        Assert.DoesNotContain("error", allowed.Text, StringComparison.Ordinal);
        Assert.Contains("target_denied", denied.Text, StringComparison.Ordinal);
        Assert.Equal([Store + "/admin"], browser.Navigated);
        Assert.Equal([OwnerId], browser.BoundAgents.Distinct());
        Assert.DoesNotContain(Guid.Empty, browser.BoundAgents);
        Assert.DoesNotContain(OtherOwnerId, browser.BoundAgents);
    }

    [Theory]
    [InlineData(AgentInstanceLifecycle.Archived)]
    [InlineData(null)]
    public async Task Archived_or_missing_agent_cannot_navigate(AgentInstanceLifecycle? status)
    {
        var browser = new RecordingBrowser();
        var agents = new InMemoryAgentInstanceStore();
        if (status is AgentInstanceLifecycle value)
        {
            await AddAgentAsync(agents, OwnerId, value);
        }

        var denied = await Executor(browser, agents).ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: Admission());

        Assert.Contains("active Agent Instance", denied.Text, StringComparison.Ordinal);
        Assert.Empty(browser.Navigated);
        Assert.DoesNotContain(OtherOwnerId, browser.BoundAgents);
    }

    [Fact]
    public void Skill_metadata_and_detached_messaging_do_not_grant_browser_or_mail()
    {
        var bare = Definition() with
        {
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.WorkspaceRead]),
            Skills =
            [
                new SkillSpec(
                    "store.product.manage",
                    "Manage a store product",
                    "Procedure only.",
                    "Use the browser.", SkillProjection.OnDemand, true,
                    [ToolCatalog.BrowserNavigate],
                    [])
            ]
        };
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(bare, ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: Admission()));
        var messaging = Definition() with
        {
            Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.AppMessageSend, ToolCatalog.BrowserNavigate])
        };
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(messaging, ToolCatalog.AppMessageSend, ToolConfigurationGates.AllowAll, admission: Admission()));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(Definition(), ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: Admission() with { TriggerKind = TriggerKind.ApplicationEvent, AgentInstanceId = null }));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(Definition(), ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: Admission() with { TriggerKind = TriggerKind.ApplicationEvent, AgentInstanceId = OwnerId }));
        Assert.False(ToolPolicy.IsOffered(
            Definition(),
            Context(trusted: false),
            ToolCatalog.BrowserNavigate,
            ToolConfigurationGates.AllowAll));
        Assert.True(ToolPolicy.IsOffered(
            Definition(),
            Context(trusted: true),
            ToolCatalog.BrowserNavigate,
            ToolConfigurationGates.AllowAll));
    }

    [Fact]
    public async Task Cancellation_and_provider_loss_do_not_act_later()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(browser, agents).ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}"""),
            ToolLimits.MaxOutputBytes,
            cancelled.Token,
            admission: Admission()));
        Assert.Equal(0, browser.ActCalls);

        browser.ErrorCode = "provider_unavailable";
        var outcome = await RunAsync(
            browser,
            agents,
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}""")),
                () => ToolRound(Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}""")),
                () => CompleteRound("stopped")));
        Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.Equal(1, browser.NavigateCalls);
        Assert.Equal(0, browser.ActCalls);
    }

    [Fact]
    public async Task Human_verification_stops_the_occurrence()
    {
        var browser = new RecordingBrowser { Intervention = true };
        var agents = await ActiveAgentsAsync(OwnerId);
        var outcome = await RunAsync(
            browser,
            agents,
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}""")),
                () => TextRound("should not be claimed")));
        var failed = Assert.IsType<DurableOccurrenceFailed>(outcome);
        Assert.Equal("user_intervention_required", failed.Code);
        Assert.Equal(0, browser.ActCalls);
    }

    [Fact]
    public async Task Bound_browser_occurrence_runs_past_the_standard_step_budget()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var completed = await RunAsync(browser, agents, new CountingNavigateModel(25));
        Assert.IsType<DurableOccurrenceCompleted>(completed);
        Assert.Equal(25, browser.NavigateCalls);

        browser = new RecordingBrowser();
        var failed = Assert.IsType<DurableOccurrenceFailed>(
            await RunAsync(browser, agents, new CountingNavigateModel(33)));
        Assert.Equal("tool-step-limit", failed.Code);
        Assert.Equal(ToolExecutionBudget.UnattendedBoundBrowser.MaxSteps, browser.NavigateCalls);
    }

    [Fact]
    public async Task Durable_vision_capture_reaches_the_next_model_call_and_not_the_checkpoint()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x53, 0x45, 0x43, 0x52, 0x45, 0x54 };
        var browser = new RecordingBrowser { CapturePng = png };
        var agents = await ActiveAgentsAsync(OwnerId);
        var captures = new InMemoryWorkCaptureStore(TimeProvider.System);
        var model = new RecordingScriptModel(
            true,
            () => ToolRound(Call(ToolCatalog.BrowserScreenshot, "{}")),
            () => CompleteRound("saw the page"));
        var now = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
        var generation = Guid.Parse("019944af-00e6-7000-8000-000000000001");
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(5)))!;
        string? checkpoint = null;
        var outcome = await new DurableOccurrenceExecution(
            new SessionToolExecutor(
                browser: browser,
                configurationGate: ToolConfigurationGates.AllowAll,
                agentInstances: agents,
                workCaptures: captures),
            TimeProvider.System).RunAsync(
            claimed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "look")]),
            model,
            Definition(),
            TriggerKind.ScheduledOccurrence,
            (current, body, token) =>
            {
                checkpoint = body.PayloadJson;
                return store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token);
            },
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);

        Assert.IsType<DurableOccurrenceCompleted>(outcome);
        var followUp = model.Requests[1];
        var tool = Assert.Single(followUp.Messages, message => message.Role == ModelRole.Tool);
        var image = Assert.IsType<ModelImageContent>(Assert.Single(tool.Parts!));
        Assert.Equal(png, image.Bytes);
        Assert.NotNull(checkpoint);
        Assert.Contains("artifactId", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(png), checkpoint, StringComparison.Ordinal);
        Assert.True(DurableToolCallCheckpoint.TryRead(new WorkCheckpoint(checkpoint, 0, 0, 1), out var resumed));
        Assert.All(resumed!, message => Assert.Null(message.Parts));
    }

    [Fact]
    public async Task Restarted_capture_is_reloaded_for_the_next_model_call()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x52, 0x45, 0x53, 0x54 };
        var captures = new InMemoryWorkCaptureStore(TimeProvider.System);
        var saved = await captures.SaveAsync(WorkId, OwnerId, "image/png", png);
        var artifactId = saved.Capture!.CaptureId;
        var call = Call(ToolCatalog.BrowserScreenshot, "{}");
        var payload = DurableToolCallCheckpoint.Write(
        [
            new ModelMessage(ModelRole.User, "look"),
            new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call]),
            new ModelMessage(
                ModelRole.Tool,
                $$"""{"contentType":"image/png","byteSize":{{png.Length}},"artifactId":"{{artifactId}}"}""",
                ToolCallId: call.Id,
                Name: ToolCatalog.BrowserScreenshot)
        ], skillState: new([], [], 0));
        var now = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
        var generation = Guid.Parse("019944af-00e6-7000-8000-000000000002");
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now, "source|capture-restart"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(5)))!;
        var checkpoint = new WorkCheckpoint(payload, 1, png.Length, (int)ToolLimits.Overall.TotalMilliseconds);
        var running = await store.CheckpointAsync(WorkId, claimed.Revision, generation, checkpoint, null, now);
        var model = new RecordingScriptModel(true, () => CompleteRound("saw the restored image"));
        var outcome = await new DurableOccurrenceExecution(
            Executor(new RecordingBrowser(), await ActiveAgentsAsync(OwnerId)),
            TimeProvider.System,
            captures).RunAsync(
            running,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "look")]),
            model,
            Definition(),
            TriggerKind.ScheduledOccurrence,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);

        Assert.IsType<DurableOccurrenceCompleted>(outcome);
        var tool = Assert.Single(model.Requests[0].Messages, message => message.Role == ModelRole.Tool);
        var image = Assert.IsType<ModelImageContent>(Assert.Single(tool.Parts!));
        Assert.Equal(png, image.Bytes);
    }

    [Fact]
    public async Task Missing_capture_bytes_force_a_fresh_capture_before_the_model_continues()
    {
        var call = Call(ToolCatalog.BrowserScreenshot, "{}");
        var missing = Guid.Parse("019944af-00e6-7000-8000-000000000099");
        var payload = DurableToolCallCheckpoint.Write(
        [
            new ModelMessage(ModelRole.User, "look"),
            new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call]),
            new ModelMessage(
                ModelRole.Tool,
                $$"""{"artifactId":"{{missing}}"}""",
                ToolCallId: call.Id,
                Name: ToolCatalog.BrowserScreenshot)
        ], skillState: new([], [], 0));
        var now = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
        var generation = Guid.Parse("019944af-00e6-7000-8000-000000000003");
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now, "source|capture-missing"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(5)))!;
        var running = await store.CheckpointAsync(
            WorkId,
            claimed.Revision,
            generation,
            new WorkCheckpoint(payload, 1, 0, (int)ToolLimits.Overall.TotalMilliseconds),
            null,
            now);
        var model = new RecordingScriptModel(true, () => CompleteRound("the picture was missing"));
        var outcome = await new DurableOccurrenceExecution(
            Executor(new RecordingBrowser(), await ActiveAgentsAsync(OwnerId)),
            TimeProvider.System,
            new InMemoryWorkCaptureStore(TimeProvider.System)).RunAsync(
            running,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "look")]),
            model,
            Definition(),
            TriggerKind.ScheduledOccurrence,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);

        Assert.IsType<DurableOccurrenceCompleted>(outcome);
        var tool = Assert.Single(model.Requests[0].Messages, message => message.Role == ModelRole.Tool);
        Assert.Null(tool.Parts);
        Assert.Equal(WorkCaptureRehydration.Unavailable, tool.Text);
    }

    [Fact]
    public async Task Empty_model_continuation_after_a_browser_tool_retries_with_that_reason()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var outcome = await RunAsync(
            browser,
            agents,
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => [new ModelCompleted(ModelStopReason.Completed)]));
        var retry = Assert.IsType<DurableOccurrenceRetry>(outcome);
        Assert.Equal("empty-result", retry.Code);
        Assert.Equal("The model returned no result.", retry.Summary);
        Assert.Null(retry.Running.KnownEffectSummary);
        Assert.Equal(1, browser.ObserveCalls);
    }

    [Fact]
    public async Task Plain_text_does_not_finish_unattended_work()
    {
        var outcome = await RunAsync(
            new RecordingBrowser(),
            await ActiveAgentsAsync(OwnerId),
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => TextRound("I looked at the page.")));
        var retry = Assert.IsType<DurableOccurrenceRetry>(outcome);
        Assert.Equal("completion-required", retry.Code);
    }

    [Fact]
    public async Task A_browser_change_is_the_visible_external_action()
    {
        var outcome = await RunAsync(
            new RecordingBrowser(),
            await ActiveAgentsAsync(OwnerId),
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}""")),
                () => CompleteRound("The product was updated.")));
        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.Equal(WorkKnownEffects.ExternalActionCompleted, completed.Running.KnownEffectSummary);
    }

    [Fact]
    public async Task Capture_limits_survive_a_large_scope_map()
    {
        var browser = new RecordingBrowser
        {
            CapturePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01]
        };
        var executor = new SessionToolExecutor(
            browser: browser,
            configurationGate: ToolConfigurationGates.AllowAll,
            agentInstances: await ActiveAgentsAsync(OwnerId),
            workCaptures: new InMemoryWorkCaptureStore(TimeProvider.System));
        var limited = Guid.Parse("019944af-00e7-7000-8000-000000000001");
        for (var attempt = 0; attempt < BrowserToolLimits.MaxCapturesPerScope; attempt++)
        {
            var accepted = await CaptureOnce(executor, limited);
            Assert.Contains("artifactId", accepted.Text, StringComparison.Ordinal);
        }

        var blocked = await CaptureOnce(executor, limited);
        Assert.Contains("capture_limit", blocked.Text, StringComparison.Ordinal);

        var scopeBytes = new byte[16];
        scopeBytes[0] = 0x19;
        for (var index = 0; index < 1025; index++)
        {
            scopeBytes[1] = (byte)(index >> 8);
            scopeBytes[2] = (byte)index;
            var scope = new Guid(scopeBytes);
            var accepted = await CaptureOnce(executor, scope);
            Assert.Contains("artifactId", accepted.Text, StringComparison.Ordinal);
        }

        var stillBlocked = await CaptureOnce(executor, limited);
        Assert.Contains("capture_limit", stillBlocked.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_captures_are_deleted_without_another_save()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-03T00:00:00Z"));
        var captures = new InMemoryWorkCaptureStore(clock);
        var saved = await captures.SaveAsync(WorkId, OwnerId, "image/png", new byte[] { 0x89, 0x50 });
        clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(1, await captures.PurgeExpiredAsync());
        Assert.Null(await captures.ReadAsync(saved.Capture!.CaptureId));
    }

    private static Task<ToolExecutionResult> CaptureOnce(SessionToolExecutor executor, Guid scope) =>
        executor.ExecuteAsync(
            Definition(),
            Guid.NewGuid(),
            Call(ToolCatalog.BrowserScreenshot, "{}"),
            10_000,
            CancellationToken.None,
            admission: new ToolExecutionAdmission(
                true,
                TriggerKind.ScheduledOccurrence,
                AgentInstanceId: OwnerId,
                SupportsVision: true,
                CaptureScope: scope.ToString("D"),
                WorkItemId: scope));

    [Fact]
    public async Task Scheduled_downloads_share_the_work_item_owner_and_stop_at_two()
    {
        var csv = "sku,name\nAC-1042,Keyboard\n"u8.ToArray();
        var pdf = "%PDF-1.4\n1 0 obj\n"u8.ToArray();
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var captures = new InMemoryWorkCaptureStore(TimeProvider.System);
        var executor = new SessionToolExecutor(
            browser: browser,
            configurationGate: ToolConfigurationGates.AllowAll,
            agentInstances: agents,
            workCaptures: captures);
        var admission = Admission() with { CaptureScope = WorkId.ToString("D"), WorkItemId = WorkId };
        browser.Downloads =
        [
            new BrowserDownload(null, "notes.csv", "text/csv", csv)
        ];
        var first = await executor.ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: admission);
        browser.Downloads =
        [
            new BrowserDownload(null, "sheet.pdf", "application/pdf", pdf),
            new BrowserDownload(null, "payload.exe", null, "MZ-not-allowed"u8.ToArray()),
            new BrowserDownload(null, "extra.csv", "text/csv", csv)
        ];
        var second = await executor.ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/files"}"""),
            ToolLimits.MaxOutputBytes,
            admission: admission);

        Assert.Contains("text/csv", first.Text, StringComparison.Ordinal);
        Assert.Contains("notes.csv", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AC-1042", first.Text, StringComparison.Ordinal);
        Assert.Contains("application/pdf", second.Text, StringComparison.Ordinal);
        Assert.Contains("download_rejected", second.Text, StringComparison.Ordinal);
        Assert.Contains("download_limit", second.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("%PDF", second.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("MZ-not-allowed", second.Text, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(first.Text, "artifactId") + CountOf(second.Text, "artifactId"));
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    [Fact]
    public async Task Uncertain_browser_act_is_not_replayed_until_observe()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e1-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])], skillState: new([], [], 0));
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(1)))!;
        var checkpoint = new WorkCheckpoint(payload, 0, 0, (int)ToolLimits.Overall.TotalMilliseconds);
        var saved = await store.CheckpointAsync(WorkId, claimed.Revision, generation, checkpoint, null, now);
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserClick, JsonDocument.Parse(act.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            WorkId, saved.Revision, generation, WorkSideEffectDisposition.Prepared, act.Id, hash, now);
        var fenced = await store.MarkSideEffectAsync(
            WorkId, prepared.Revision, generation, WorkSideEffectDisposition.InFlight, act.Id, hash, now);
        var outcome = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(
            fenced,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(act),
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{RepairRef}}"}""")),
                () => CompleteRound("observed")),
            Definition(),
            TriggerKind.ApplicationEvent,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);

        Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(RepairRef, browser.LastActRef);
        Assert.True(browser.ObserveCalls >= 1);
        Assert.Equal([OwnerId], browser.UnattendedAgents);
    }

    [Fact]
    public async Task Uncertain_browser_act_stays_unreplayed_until_the_page_is_observed()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e4-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])], skillState: new([], [], 0));
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(1)))!;
        var checkpoint = new WorkCheckpoint(payload, 0, 0, (int)ToolLimits.Overall.TotalMilliseconds);
        var saved = await store.CheckpointAsync(WorkId, claimed.Revision, generation, checkpoint, null, now);
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserClick, JsonDocument.Parse(act.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            WorkId, saved.Revision, generation, WorkSideEffectDisposition.Prepared, act.Id, hash, now);
        var fenced = await store.MarkSideEffectAsync(
            WorkId, prepared.Revision, generation, WorkSideEffectDisposition.InFlight, act.Id, hash, now);
        var outcome = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(
            fenced,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(() => TextRound("I stopped before looking.")),
            Definition(),
            TriggerKind.ScheduledOccurrence,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);

        var failed = Assert.IsType<DurableOccurrenceFailed>(outcome);
        Assert.Equal("observation-required", failed.Code);
        Assert.Equal(0, browser.ActCalls);
        var recovery = await store.RecoverExpiredClaimsAsync(now.AddMinutes(2));
        var resumed = Assert.Single(recovery.ObservationResumes);
        Assert.Empty(recovery.TerminalFailures);
        Assert.Equal(WorkItemStatus.Running, resumed.Status);
        Assert.NotEqual(WorkItemStatus.WaitingToRetry, resumed.Status);
        Assert.Equal(WorkSideEffectDisposition.InFlight, resumed.SideEffect.Disposition);
        Assert.Contains("\"ObservationRequired\":true", resumed.Checkpoint!.PayloadJson, StringComparison.Ordinal);
        var resumeGeneration = resumed.Claim!.Generation;
        var continued = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(
            resumed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{RepairRef}}"}""")),
                () => CompleteRound("observed")),
            Definition(),
            TriggerKind.ScheduledOccurrence,
            (current, body, token) => store.CheckpointAsync(
                current.WorkItemId,
                current.Revision,
                resumeGeneration,
                body,
                null,
                now.AddMinutes(2),
                token),
            store,
            resumeGeneration,
            now.AddMinutes(2),
            Ids(),
            CancellationToken.None);
        Assert.IsType<DurableOccurrenceCompleted>(continued);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(RepairRef, browser.LastActRef);
    }

    [Theory]
    [InlineData(ToolCatalog.BrowserClick)]
    [InlineData(ToolCatalog.BrowserHover)]
    [InlineData(ToolCatalog.BrowserType)]
    public async Task In_flight_browser_interaction_without_a_saved_flag_is_observed_after_claim_expiry(string interruptedTool)
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e5-7000-8000-000000000001");
        var act = Call(interruptedTool, interruptedTool == ToolCatalog.BrowserType
            ? $$"""{"ref":"{{PublishRef}}","text":"attempted change"}"""
            : $$"""{"ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])], skillState: new([], [], 0));
        Assert.DoesNotContain("\"ObservationRequired\":true", payload, StringComparison.Ordinal);
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(1)))!;
        var saved = await store.CheckpointAsync(
            WorkId,
            claimed.Revision,
            generation,
            new WorkCheckpoint(payload, 0, 0, (int)ToolLimits.Overall.TotalMilliseconds),
            null,
            now);
        var hash = ToolActionHash.Compute(interruptedTool, JsonDocument.Parse(act.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            WorkId, saved.Revision, generation, WorkSideEffectDisposition.Prepared, act.Id, hash, now);
        await store.MarkSideEffectAsync(
            WorkId, prepared.Revision, generation, WorkSideEffectDisposition.InFlight, act.Id, hash, now);
        var recovery = await store.RecoverExpiredClaimsAsync(now.AddMinutes(2));
        var resumed = Assert.Single(recovery.ObservationResumes);
        Assert.Empty(recovery.TerminalFailures);
        Assert.Equal(WorkItemStatus.Running, resumed.Status);
        Assert.NotEqual(WorkItemStatus.WaitingToRetry, resumed.Status);
        Assert.Contains("\"ObservationRequired\":true", resumed.Checkpoint!.PayloadJson, StringComparison.Ordinal);
        var resumeGeneration = resumed.Claim!.Generation;
        var continued = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(
            resumed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(act),
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{RepairRef}}"}""")),
                () => CompleteRound("observed")),
            Definition() with { Environment = new RoleEnvironment(ToolAllowlist: Definition().Environment!.ToolAllowlist!.Append(interruptedTool).Distinct().ToArray()) },
            TriggerKind.ScheduledOccurrence,
            (current, body, token) => store.CheckpointAsync(
                current.WorkItemId,
                current.Revision,
                resumeGeneration,
                body,
                null,
                now.AddMinutes(2),
                token),
            store,
            resumeGeneration,
            now.AddMinutes(2),
            Ids(),
            CancellationToken.None);
        Assert.IsType<DurableOccurrenceCompleted>(continued);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(RepairRef, browser.LastActRef);
    }

    [Fact]
    public async Task Application_event_uses_the_instance_profile_and_bound_budget()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var completed = await RunAsync(
            browser,
            agents,
            new CountingNavigateModel(25),
            TriggerKind.ApplicationEvent);
        Assert.IsType<DurableOccurrenceCompleted>(completed);
        Assert.Equal(25, browser.NavigateCalls);
        Assert.Equal([OwnerId], browser.UnattendedAgents);
        Assert.Empty(browser.UnattendedOrigins[0]);

        browser = new RecordingBrowser();
        var standard = Assert.IsType<DurableOccurrenceFailed>(await RunAsync(
            browser,
            agents,
            new CountingNavigateModel(25),
            TriggerKind.ApplicationEvent,
            browserConfigured: false));
        Assert.Equal("tool-step-limit", standard.Code);
        Assert.Equal(0, browser.NavigateCalls);
        Assert.Empty(browser.UnattendedAgents);

        var offered = ToolCatalog.For(
            Definition(),
            Context(trusted: true, TriggerKind.ApplicationEvent),
            ToolConfigurationGates.AllowAll);
        Assert.Contains(offered, tool => tool.Name == ToolCatalog.BrowserNavigate);
        var quiet = ToolCatalog.For(
            Definition(),
            Context(trusted: false, TriggerKind.ApplicationEvent),
            ToolConfigurationGates.AllowAll);
        Assert.DoesNotContain(quiet, tool => ToolCatalog.IsBrowserTool(tool.Name));
        Assert.Contains(quiet, tool => tool.Name == ToolCatalog.WorkComplete);

        var live = new ToolExecutionAdmission(
            false,
            TriggerKind.ApplicationEvent,
            AgentInstanceId: OwnerId);
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(Definition(), ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: live));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                Definition(),
                ToolCatalog.BrowserNavigate,
                ToolConfigurationGates.AllowAll,
                admission: live with { AgentInstanceId = null }));
        var liveBrowser = new RecordingBrowser();
        var navigated = await Executor(liveBrowser, agents).ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: live);
        Assert.DoesNotContain("error", navigated.Text, StringComparison.Ordinal);
        Assert.Equal([OwnerId], liveBrowser.BoundAgents.Distinct());
        Assert.Empty(liveBrowser.UnattendedAgents);
    }

    [Fact]
    public async Task Succeeded_browser_act_without_a_tool_result_is_not_replayed()
    {
        var browser = new RecordingBrowser();
        var agents = await ActiveAgentsAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e7-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserClick, $$"""{"ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])], skillState: new([], [], 0));
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now, "order.placed:evt-1"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(1)))!;
        var saved = await store.CheckpointAsync(
            WorkId,
            claimed.Revision,
            generation,
            new WorkCheckpoint(payload, 0, 0, (int)ToolLimits.Overall.TotalMilliseconds),
            null,
            now);
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserClick, JsonDocument.Parse(act.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            WorkId, saved.Revision, generation, WorkSideEffectDisposition.Prepared, act.Id, hash, now);
        var inflight = await store.MarkSideEffectAsync(
            WorkId, prepared.Revision, generation, WorkSideEffectDisposition.InFlight, act.Id, hash, now);
        var succeeded = await store.MarkSideEffectAsync(
            WorkId, inflight.Revision, generation, WorkSideEffectDisposition.Succeeded, act.Id, hash, now);
        string? checkpoint = null;
        var definition = Definition() with
        {
            Environment = new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserSnapshot,
                ToolCatalog.BrowserClick,
                ToolCatalog.WorkComplete
            ])
        };
        var outcome = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(
            succeeded,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(act),
                () => ToolRound(Call(ToolCatalog.BrowserSnapshot, "{}")),
                () => ToolRound(Call(ToolCatalog.WorkComplete, """{"summary":"Order checked.","attentionRequired":false,"outcome":"ActionCompleted"}"""))),
            definition,
            TriggerKind.ApplicationEvent,
            (current, body, token) =>
            {
                checkpoint = body.PayloadJson;
                return store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token);
            },
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);

        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.Equal("Order checked.", WorkCompletionRequest.Summary(completed.Text));
        Assert.False(completed.AttentionRequired);
        Assert.Equal(0, browser.ActCalls);
        Assert.True(browser.ObserveCalls >= 1);
        Assert.NotNull(checkpoint);
        Assert.Contains("already_completed", checkpoint, StringComparison.Ordinal);
        Assert.Contains("replayed", checkpoint, StringComparison.Ordinal);
        Assert.Equal("Automation · Event", succeeded.OriginLabel);
    }

    private static async Task<DurableOccurrenceCompleted> RunSecretaryModeAsync(
        RecordingBrowser browser,
        InMemoryAgentInstanceStore agents,
        AgentDefinition definition,
        Guid instanceId,
        Guid profileId,
        Guid workId,
        Guid generation,
        string dedupeKey,
        TriggerKind kind,
        ILanguageModel model)
    {
        var now = DateTimeOffset.Parse("2026-10-04T02:00:00Z");
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            workId,
            new WorkOwner(instanceId, profileId),
            new WorkProvenance(
                workId,
                kind == TriggerKind.ApplicationEvent ? WorkSourceKind.ApplicationEvent : WorkSourceKind.Schedule,
                null,
                null,
                null,
                dedupeKey,
                now,
                now,
                """{"instruction":"synthetic"}""",
                definition.Id,
                definition.Version,
                definition.Identity.Name),
            new WorkModelPin("scripted-vision", "primary-llm", "scripted-vision", null),
            3,
            now));
        var claimed = (await store.TryClaimAsync(workId, generation, now, now.AddMinutes(5)))!;
        var outcome = await new DurableOccurrenceExecution(Executor(browser, agents), TimeProvider.System).RunAsync(
            claimed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "review the store")]),
            model,
            definition,
            kind,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);
        return Assert.IsType<DurableOccurrenceCompleted>(outcome);
    }

    private static async Task<AgentDefinition> LoadSecretaryV2Async()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agents", "secretary-v3.json")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("agents/secretary-v3.json");
        }

        var store = new FileAgentDefinitionStore(Path.Combine(directory.FullName, "agents"), SyntheticProviderAliases.Default);
        return (await store.GetAsync("secretary", 3))!;
    }

    private static async Task<DurableOccurrenceOutcome> RunAsync(
        RecordingBrowser browser,
        InMemoryAgentInstanceStore agents,
        ILanguageModel model,
        TriggerKind kind = TriggerKind.ScheduledOccurrence,
        bool browserConfigured = true)
    {
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e2-7000-8000-000000000001");
        var store = new InMemoryWorkItemStore();
        await store.CreateAsync(WorkItem.Create(
            WorkId,
            new WorkOwner(OwnerId, ProfileId),
            Provenance(now),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(WorkId, generation, now, now.AddMinutes(5)))!;
        return await new DurableOccurrenceExecution(new SessionToolExecutor(browser: browser, agentInstances: agents, configurationGate: browserConfigured ? ToolConfigurationGates.AllowAll : ToolConfigurationGates.Unconfigured), TimeProvider.System).RunAsync(
            claimed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            model,
            Definition(),
            kind,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None);
    }

    private static SessionToolExecutor Executor(RecordingBrowser browser, InMemoryAgentInstanceStore agents) =>
        new(browser: browser, configurationGate: ToolConfigurationGates.AllowAll, agentInstances: agents);

    private static ToolExecutionAdmission Admission() =>
        new(true, TriggerKind.ScheduledOccurrence, AgentInstanceId: OwnerId);

    private static ModelToolCall Call(string name, string arguments) => new("call-" + name, name, arguments);

    private static IReadOnlyList<ModelGenerationEvent> ToolRound(ModelToolCall call) =>
    [
        new ModelToolCallEvent(call),
        new ModelCompleted(ModelStopReason.ToolCalls)
    ];

    private static IReadOnlyList<ModelGenerationEvent> CompleteRound(string summary) =>
        ToolRound(Call(ToolCatalog.WorkComplete, $$"""{"summary":"{{summary}}","attentionRequired":false,"outcome":"ActionCompleted"}"""));

    private static IReadOnlyList<ModelGenerationEvent> TextRound(string text) =>
    [
        new ModelTextDelta(text),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static async Task<InMemoryAgentInstanceStore> ActiveAgentsAsync(Guid agent)
    {
        var instances = new InMemoryAgentInstanceStore();
        await AddAgentAsync(instances, agent, AgentInstanceLifecycle.Active);
        return instances;
    }
    private static ValueTask AddAgentAsync(InMemoryAgentInstanceStore instances, Guid id, AgentInstanceLifecycle lifecycle) =>
        instances.InsertAsync(new AgentInstance(id, "general-assistant", 11,
            new("Test", "Secretary", "Fixture", "neutral"), lifecycle,
            DateTimeOffset.Parse("2026-10-02T00:00:00Z"), DateTimeOffset.Parse("2026-10-02T00:00:00Z")));

    private static WorkProvenance Provenance(DateTimeOffset now, string dedupeKey = "source|browser") =>
        new(
            Guid.Parse("019944af-00e1-7000-8000-000000000005"),
            dedupeKey.StartsWith("order.placed:", StringComparison.Ordinal)
                ? WorkSourceKind.ApplicationEvent
                : WorkSourceKind.Schedule,
            null,
            null,
            null,
            dedupeKey,
            now,
            now,
            """{"instruction":"synthetic"}""",
            "general-assistant",
            11,
            "Test");

    private static AgentDefinition Definition() =>
        new(
            1,
            "general-assistant",
            11,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserSnapshot,
                ToolCatalog.BrowserClick,
                ToolCatalog.BrowserScreenshot,
                ToolCatalog.AppMessageSend
            ]));

    private static AgentContext Context(bool trusted, TriggerKind kind = TriggerKind.ScheduledOccurrence) =>
        new(
            Definition(),
            [],
            "",
            null,
            Domain.Conversation.SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), kind, "review"),
            DetachedExecution: true,
            AgentInstanceId: trusted ? OwnerId : null);

    private static DeterministicIdGenerator Ids() =>
        new(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-00e3-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940e301")]);

    private static readonly Guid OwnerId = Guid.Parse("019944af-00e1-7000-8000-000000000003");
    private static readonly Guid OtherOwnerId = Guid.Parse("019944af-00e1-7000-8000-000000000099");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00e1-7000-8000-000000000004");
    private static readonly Guid WorkId = Guid.Parse("019944af-00e1-7000-8000-000000000002");

    private sealed class CountingNavigateModel(int navigations) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var done = request.Messages.Count(message => message.Role == ModelRole.Tool);
            if (done < navigations)
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    $"nav-{done + 1}",
                    ToolCatalog.BrowserNavigate,
                    $$"""{"url":"{{Store}}/admin"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelToolCallEvent(new ModelToolCall(
                "done",
                ToolCatalog.WorkComplete,
                """{"summary":"observed","attentionRequired":false,"outcome":"ActionCompleted"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class RecordingScriptModel(bool vision, params Func<IReadOnlyList<ModelGenerationEvent>>[] rounds) : ILanguageModel
    {
        private readonly Queue<Func<IReadOnlyList<ModelGenerationEvent>>> _rounds = new(rounds);

        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Vision: vision, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            foreach (var item in _rounds.Dequeue()())
            {
                yield return item;
            }
        }
    }

    private sealed class ScriptModel(params Func<IReadOnlyList<ModelGenerationEvent>>[] rounds) : ILanguageModel
    {
        private readonly Queue<Func<IReadOnlyList<ModelGenerationEvent>>> _rounds = new(rounds);

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in _rounds.Dequeue()())
            {
                yield return item;
            }
        }
    }

    private sealed class RecordingBrowser : IBrowser, IBrowserProfileBinding, IBrowserContextUse
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        private readonly List<Guid> _bound = [];

        public string? ErrorCode { get; set; }

        public bool Intervention { get; set; }

        public byte[]? CapturePng { get; init; }

        public IReadOnlyList<BrowserDownload>? Downloads { get; set; }

        public int NavigateCalls { get; private set; }

        public int ObserveCalls { get; private set; }

        public int ActCalls { get; private set; }

        public int CaptureCalls { get; private set; }

        public string? LastActRef { get; private set; }

        public List<string> Navigated { get; } = [];

        public IReadOnlyList<Guid> BoundAgents => _bound;

        public bool IsAvailable => true;

        public BrowserPolicyMode PolicyMode { get; set; } = BrowserPolicyMode.OpenWeb;
        public BrowserHostPolicy HostPolicy => new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            [Store],
            PolicyMode: PolicyMode);

        public void BindSession(Guid sessionId, Guid? agentInstanceId)
        {
            if (agentInstanceId is Guid agent && agent != Guid.Empty)
            {
                _bound.Add(agent);
            }
        }

        public List<Guid> UnattendedAgents { get; } = [];

        public List<IReadOnlyList<string>> UnattendedOrigins { get; } = [];

        public ValueTask<IAsyncDisposable> EnterUnattendedAsync(
            Guid agentInstanceId,
            IReadOnlyList<string> origins,
            CancellationToken cancellationToken = default)
        {
            UnattendedAgents.Add(agentInstanceId);
            UnattendedOrigins.Add(origins);
            return new(NoopLease.Instance);
        }

        public void AdoptUnattendedFlow(Guid agentInstanceId)
        {
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public static readonly NoopLease Instance = new();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(new Uri(Store + "/admin"));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NavigateCalls++;
            Navigated.Add(request.Url!.GetLeftPart(UriPartial.Authority) + request.Url.AbsolutePath);
            var downloads = Downloads;
            Downloads = null;
            return new(Result(request.Url!.AbsoluteUri, "Admin", downloads));
        }

        public ValueTask<BrowserScreenshotResult> CaptureViewportAsync(
            BrowserScreenshotRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCalls++;
            return new(CapturePng is { Length: > 0 } png
                ? new BrowserScreenshotResult(null, png, 1, 8, 8)
                : new BrowserScreenshotResult("provider_unavailable", null, 0));
        }

        public ValueTask<BrowserOperationResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveCalls++;
            return new(Result(Store + "/admin", "AC Keyboard $99 Published ac-keyboard.png"));
        }

        public ValueTask<BrowserOperationResult> InteractAsync(
            BrowserInteractionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActCalls++;
            LastActRef = request.Ref;
            return new(Result(Store + "/admin", "Saved"));
        }

        private BrowserOperationResult Result(string url, string text, IReadOnlyList<BrowserDownload>? downloads = null)
        {
            if (ErrorCode is not null)
            {
                return new BrowserOperationResult(ErrorCode, null);
            }

            return new BrowserOperationResult(
                null,
                new BrowserSnapshot(
                    url,
                    "Page",
                    text,
                    false,
                    [],
                    Intervention ? BrowserInterventionKind.HumanVerificationRequired : BrowserInterventionKind.None),
                Downloads: downloads);
        }
    }
}
