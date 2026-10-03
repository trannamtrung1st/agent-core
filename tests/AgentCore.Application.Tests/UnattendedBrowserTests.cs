using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Connections;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class UnattendedBrowserTests
{
    private const string Store = "http://127.0.0.1:5088";
    private const string Other = "http://127.0.0.1:5099";
    private const string PublishRef = "el_0123456789abcdefghijkl";
    private const string RepairRef = "el_abcdefghijklmnopqrstuv";

    [Fact]
    public async Task Scheduled_connection_navigates_only_the_trusted_origin_and_its_own_profile()
    {
        var browser = new RecordingBrowser();
        var connections = await ConnectedStoreAsync(OwnerId);
        var executor = Executor(browser, connections);
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
    [InlineData(ApplicationConnectionStatus.NotConnected)]
    [InlineData(ApplicationConnectionStatus.NeedsReauthentication)]
    [InlineData(null)]
    public async Task Revoked_missing_and_reauthentication_connections_do_not_navigate(ApplicationConnectionStatus? status)
    {
        var browser = new RecordingBrowser();
        var connections = new InMemoryApplicationConnectionStore();
        if (status is ApplicationConnectionStatus value)
        {
            await SaveAsync(connections, value);
        }

        var denied = await Executor(browser, connections).ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}"""),
            ToolLimits.MaxOutputBytes,
            admission: Admission());

        Assert.Contains("cannot be used", denied.Text, StringComparison.Ordinal);
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
                    "Use the browser.",
                    ["store product"],
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
            ToolPolicy.EvaluateExecution(Definition(), ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: Admission() with { TriggerKind = TriggerKind.ApplicationEvent, TrustedConnection = false }));
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(Definition(), ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: Admission() with { TriggerKind = TriggerKind.ApplicationEvent, TrustedConnection = true }));
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
        var connections = await ConnectedStoreAsync(OwnerId);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(browser, connections).ExecuteAsync(
            Definition(),
            WorkId,
            Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{PublishRef}}"}"""),
            ToolLimits.MaxOutputBytes,
            cancelled.Token,
            admission: Admission()));
        Assert.Equal(0, browser.ActCalls);

        browser.ErrorCode = "provider_unavailable";
        var outcome = await RunAsync(
            browser,
            connections,
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserNavigate, $$"""{"url":"{{Store}}/admin"}""")),
                () => ToolRound(Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{PublishRef}}"}""")),
                () => TextRound("stopped")));
        Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.Equal(1, browser.NavigateCalls);
        Assert.Equal(0, browser.ActCalls);
    }

    [Fact]
    public async Task Human_verification_stops_the_occurrence()
    {
        var browser = new RecordingBrowser { Intervention = true };
        var connections = await ConnectedStoreAsync(OwnerId);
        var outcome = await RunAsync(
            browser,
            connections,
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
        var connections = await ConnectedStoreAsync(OwnerId);
        var completed = await RunAsync(browser, connections, new CountingNavigateModel(25));
        Assert.IsType<DurableOccurrenceCompleted>(completed);
        Assert.Equal(25, browser.NavigateCalls);

        browser = new RecordingBrowser();
        var failed = Assert.IsType<DurableOccurrenceFailed>(
            await RunAsync(browser, connections, new CountingNavigateModel(33)));
        Assert.Equal("tool-step-limit", failed.Code);
        Assert.Equal(ToolExecutionBudget.UnattendedBoundBrowser.MaxSteps, browser.NavigateCalls);
    }

    [Fact]
    public async Task Durable_vision_capture_reaches_the_next_model_call_and_not_the_checkpoint()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x53, 0x45, 0x43, 0x52, 0x45, 0x54 };
        var browser = new RecordingBrowser { CapturePng = png };
        var connections = await ConnectedStoreAsync(OwnerId);
        var captures = new InMemoryWorkCaptureStore(TimeProvider.System);
        var model = new RecordingScriptModel(
            true,
            () => ToolRound(Call(ToolCatalog.BrowserCapture, "{}")),
            () => TextRound("saw the page"));
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
                applicationConnections: connections,
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
            CancellationToken.None,
            trustedConnection: true);

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
    public async Task Scheduled_downloads_share_the_work_item_owner_and_stop_at_two()
    {
        var csv = "sku,name\nAC-1042,Keyboard\n"u8.ToArray();
        var pdf = "%PDF-1.4\n1 0 obj\n"u8.ToArray();
        var browser = new RecordingBrowser();
        var connections = await ConnectedStoreAsync(OwnerId);
        var captures = new InMemoryWorkCaptureStore(TimeProvider.System);
        var executor = new SessionToolExecutor(
            browser: browser,
            configurationGate: ToolConfigurationGates.AllowAll,
            applicationConnections: connections,
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
        var connections = await ConnectedStoreAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e1-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])]);
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
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserAct, JsonDocument.Parse(act.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            WorkId, saved.Revision, generation, WorkSideEffectDisposition.Prepared, act.Id, hash, now);
        var fenced = await store.MarkSideEffectAsync(
            WorkId, prepared.Revision, generation, WorkSideEffectDisposition.InFlight, act.Id, hash, now);
        var outcome = await new DurableOccurrenceExecution(Executor(browser, connections), TimeProvider.System).RunAsync(
            fenced,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(act),
                () => ToolRound(Call(ToolCatalog.BrowserObserve, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{RepairRef}}"}""")),
                () => TextRound("observed")),
            Definition(),
            TriggerKind.ApplicationEvent,
            (current, body, token) => store.CheckpointAsync(current.WorkItemId, current.Revision, generation, body, null, now, token),
            store,
            generation,
            now,
            Ids(),
            CancellationToken.None,
            trustedConnection: true);

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
        var connections = await ConnectedStoreAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e4-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])]);
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
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserAct, JsonDocument.Parse(act.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            WorkId, saved.Revision, generation, WorkSideEffectDisposition.Prepared, act.Id, hash, now);
        var fenced = await store.MarkSideEffectAsync(
            WorkId, prepared.Revision, generation, WorkSideEffectDisposition.InFlight, act.Id, hash, now);
        var outcome = await new DurableOccurrenceExecution(Executor(browser, connections), TimeProvider.System).RunAsync(
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
            CancellationToken.None,
            trustedConnection: true);

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
        var continued = await new DurableOccurrenceExecution(Executor(browser, connections), TimeProvider.System).RunAsync(
            resumed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(Call(ToolCatalog.BrowserObserve, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{RepairRef}}"}""")),
                () => TextRound("observed")),
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
            CancellationToken.None,
            trustedConnection: true);
        Assert.IsType<DurableOccurrenceCompleted>(continued);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(RepairRef, browser.LastActRef);
    }

    [Fact]
    public async Task In_flight_browser_act_without_a_saved_flag_is_observed_after_claim_expiry()
    {
        var browser = new RecordingBrowser();
        var connections = await ConnectedStoreAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e5-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])]);
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
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserAct, JsonDocument.Parse(act.ArgumentsJson).RootElement);
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
        var continued = await new DurableOccurrenceExecution(Executor(browser, connections), TimeProvider.System).RunAsync(
            resumed,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(act),
                () => ToolRound(Call(ToolCatalog.BrowserObserve, "{}")),
                () => ToolRound(Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{RepairRef}}"}""")),
                () => TextRound("observed")),
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
            CancellationToken.None,
            trustedConnection: true);
        Assert.IsType<DurableOccurrenceCompleted>(continued);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(RepairRef, browser.LastActRef);
    }

    [Fact]
    public async Task Application_event_uses_the_instance_profile_and_bound_budget()
    {
        var browser = new RecordingBrowser();
        var connections = await ConnectedStoreAsync(OwnerId);
        var completed = await RunAsync(
            browser,
            connections,
            new CountingNavigateModel(25),
            TriggerKind.ApplicationEvent);
        Assert.IsType<DurableOccurrenceCompleted>(completed);
        Assert.Equal(25, browser.NavigateCalls);
        Assert.Equal([OwnerId], browser.UnattendedAgents);
        Assert.Contains(Store, browser.UnattendedOrigins[0]);

        browser = new RecordingBrowser();
        var standard = Assert.IsType<DurableOccurrenceFailed>(await RunAsync(
            browser,
            connections,
            new CountingNavigateModel(25),
            TriggerKind.ApplicationEvent,
            trusted: false));
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
            AgentInstanceId: OwnerId,
            TrustedConnection: true);
        Assert.Equal(
            ToolPolicyDecision.Allow,
            ToolPolicy.EvaluateExecution(Definition(), ToolCatalog.BrowserNavigate, ToolConfigurationGates.AllowAll, admission: live));
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                Definition(),
                ToolCatalog.BrowserNavigate,
                ToolConfigurationGates.AllowAll,
                admission: live with { TrustedConnection = false }));
        var liveBrowser = new RecordingBrowser();
        var navigated = await Executor(liveBrowser, connections).ExecuteAsync(
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
        var connections = await ConnectedStoreAsync(OwnerId);
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var generation = Guid.Parse("019944af-00e7-7000-8000-000000000001");
        var act = Call(ToolCatalog.BrowserAct, $$"""{"operation":"click","ref":"{{PublishRef}}"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [act])]);
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
        var hash = ToolActionHash.Compute(ToolCatalog.BrowserAct, JsonDocument.Parse(act.ArgumentsJson).RootElement);
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
                ToolCatalog.BrowserObserve,
                ToolCatalog.BrowserAct,
                ToolCatalog.WorkComplete
            ])
        };
        var outcome = await new DurableOccurrenceExecution(Executor(browser, connections), TimeProvider.System).RunAsync(
            succeeded,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "publish")]),
            new ScriptModel(
                () => ToolRound(act),
                () => ToolRound(Call(ToolCatalog.BrowserObserve, "{}")),
                () => ToolRound(Call(ToolCatalog.WorkComplete, """{"summary":"Order checked.","attentionRequired":false}"""))),
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
            CancellationToken.None,
            trustedConnection: true);

        var completed = Assert.IsType<DurableOccurrenceCompleted>(outcome);
        Assert.Equal("Order checked.", completed.Text);
        Assert.False(completed.AttentionRequired);
        Assert.Equal(0, browser.ActCalls);
        Assert.True(browser.ObserveCalls >= 1);
        Assert.NotNull(checkpoint);
        Assert.Contains("already_completed", checkpoint, StringComparison.Ordinal);
        Assert.Contains("replayed", checkpoint, StringComparison.Ordinal);
        Assert.Equal("Order placed", succeeded.OriginLabel);
    }

    private static async Task<DurableOccurrenceOutcome> RunAsync(
        RecordingBrowser browser,
        InMemoryApplicationConnectionStore connections,
        ILanguageModel model,
        TriggerKind kind = TriggerKind.ScheduledOccurrence,
        bool trusted = true)
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
        return await new DurableOccurrenceExecution(Executor(browser, connections), TimeProvider.System).RunAsync(
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
            CancellationToken.None,
            trustedConnection: trusted);
    }

    private static SessionToolExecutor Executor(RecordingBrowser browser, InMemoryApplicationConnectionStore connections) =>
        new(browser: browser, configurationGate: ToolConfigurationGates.AllowAll, applicationConnections: connections);

    private static ToolExecutionAdmission Admission() =>
        new(true, TriggerKind.ScheduledOccurrence, AgentInstanceId: OwnerId, TrustedConnection: true);

    private static ModelToolCall Call(string name, string arguments) => new("call-" + name, name, arguments);

    private static IReadOnlyList<ModelGenerationEvent> ToolRound(ModelToolCall call) =>
    [
        new ModelToolCallEvent(call),
        new ModelCompleted(ModelStopReason.ToolCalls)
    ];

    private static IReadOnlyList<ModelGenerationEvent> TextRound(string text) =>
    [
        new ModelTextDelta(text),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static async Task<InMemoryApplicationConnectionStore> ConnectedStoreAsync(Guid agent)
    {
        var store = new InMemoryApplicationConnectionStore();
        await SaveAsync(store, ApplicationConnectionStatus.Connected, agent);
        return store;
    }

    private static async Task SaveAsync(
        InMemoryApplicationConnectionStore store,
        ApplicationConnectionStatus status,
        Guid? agent = null)
    {
        var id = agent ?? OwnerId;
        var now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        await store.SaveAsync(
            new ApplicationConnection(
                Guid.NewGuid(),
                id,
                ApplicationConnectionKinds.NopCommerce,
                "Store",
                Store,
                [Store],
                status,
                id,
                1,
                now,
                now,
                null),
            0);
    }

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
                ToolCatalog.BrowserObserve,
                ToolCatalog.BrowserAct,
                ToolCatalog.BrowserCapture,
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
            TrustedConnection: trusted);

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

            yield return new ModelTextDelta("observed");
            yield return new ModelCompleted(ModelStopReason.Completed);
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

    private sealed class RecordingBrowser : IBrowserSession, IBrowserProfileBinding, IBrowserContextUse
    {
        private readonly List<Guid> _bound = [];

        public string? ErrorCode { get; set; }

        public bool Intervention { get; set; }

        public byte[]? CapturePng { get; init; }

        public IReadOnlyList<BrowserDownload>? Downloads { get; set; }

        public int NavigateCalls { get; private set; }

        public int ObserveCalls { get; private set; }

        public int ActCalls { get; private set; }

        public string? LastActRef { get; private set; }

        public List<string> Navigated { get; } = [];

        public IReadOnlyList<Guid> BoundAgents => _bound;

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            [Store],
            PolicyMode: BrowserPolicyMode.OpenWeb);

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

        public ValueTask<BrowserCaptureResult> CaptureViewportAsync(
            BrowserCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(CapturePng is { Length: > 0 } png
                ? new BrowserCaptureResult(null, png, 1, 8, 8)
                : new BrowserCaptureResult("provider_unavailable", null, 0));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveCalls++;
            return new(Result(Store + "/admin", "AC Keyboard $99 Published ac-keyboard.png"));
        }

        public ValueTask<BrowserOperationResult> ActAsync(
            BrowserActRequest request,
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
                new BrowserObservation(
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
