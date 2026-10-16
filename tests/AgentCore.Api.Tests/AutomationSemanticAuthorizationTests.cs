using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Context;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class AutomationSemanticAuthorizationTests
{
    [Theory(Timeout = 60000)]
    [InlineData(TriggerCommandAuthorizationDecision.Allow, true, false)]
    [InlineData(TriggerCommandAuthorizationDecision.Deny, false, false)]
    [InlineData(TriggerCommandAuthorizationDecision.Ambiguous, false, false)]
    [InlineData(TriggerCommandAuthorizationDecision.ClassifierUnavailable, true, false)]
    [InlineData(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, true, false)]
    [InlineData(TriggerCommandAuthorizationDecision.ClassifierUnavailable, false, false)]
    [InlineData(TriggerCommandAuthorizationDecision.ClassifierUnavailable, true, true)]
    public async Task Exact_proposal_uses_semantics_or_explicit_ui_decision_and_survives_reopen(TriggerCommandAuthorizationDecision decision, bool approve, bool longContent)
    {
        var db = Path.Combine(Path.GetTempPath(), $"semantic-automation-{Guid.NewGuid():N}.db");
        var model = new ProposingModel(); var verifier = new Verifier(decision); var output = new Output();
        var instructions = longContent ? string.Concat(Enumerable.Repeat("記憶の整理", 350)) : "Review learned experience";
        if (longContent) model.Arguments = JsonSerializer.Serialize(new { instructions, kind = "daily", localTime = "09:00", timeZone = "Asia/Ho_Chi_Minh", executionTarget = "backgroundSession", reportBack = false });
        Guid instanceId;
        await using (var host = Host(db, model, verifier))
        {
            var services = host.Services;
            instanceId = (await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, output);
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Hãy xem lại kinh nghiệm mỗi ngày lúc 9 giờ Việt Nam, chạy nền và không báo lại."));
            if (decision is TriggerCommandAuthorizationDecision.ClassifierUnavailable or TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)
            {
                var notice = await output.Approval.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var pending = Assert.IsType<ApprovalRequestedOutput>(notice.Payload);
                Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null));
                Assert.Contains(instructions, pending.Details["Exact action"]);
                Assert.Contains("Asia/Ho_Chi_Minh", pending.Details["Exact action"]);
                var saved = Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instanceId, LocalUserProfile.Id), session.SessionId));
                Assert.Equal(AgentRunStatus.WaitingForApproval, saved.Status);
                Assert.Equal(verifier.Requests[0].ActionHash, saved.Approval!.ActionHash);
                if (longContent) Assert.True(saved.Approval.Preview.Length > AgentRunLimits.MaxPreviewCharacters);
                await runtime.DetachAsync();
                Assert.True(await runtime.AttachAsync());
                var ready = output.Items.Select(item => item.Payload).OfType<ReadyOutput>().Last();
                Assert.Equal(pending.ApprovalId, ready.Ready.PendingApproval!.ApprovalId);
                Assert.Equal(pending.Details["Exact action"], ready.Ready.PendingApproval.Details["Exact action"]);
                Assert.Equal(ResponseApprovalResult.Accepted, await runtime.RespondApprovalAsync(notice.ResponseId!.Value, pending.ApprovalId,
                    approve ? ToolApprovalDecision.Approve : ToolApprovalDecision.Reject));
            }
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Single(verifier.Requests);
            var proposed = verifier.Requests[0];
            Assert.Equal(ToolCatalog.AutomationCreate, proposed.ToolName);
            Assert.Equal(instructions, proposed.ProposedAction.GetProperty("arguments").GetProperty("instructions").GetString());
            var automations = await services.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null);
            var expected = decision == TriggerCommandAuthorizationDecision.Allow || decision is TriggerCommandAuthorizationDecision.ClassifierUnavailable or TriggerCommandAuthorizationDecision.ClassifierInvalidOutput && approve;
            if (expected)
            {
                var created = Assert.Single(automations); var schedule = Assert.IsType<DailySchedule>(created.Schedule);
                Assert.Equal(new TimeOnly(9,0), schedule.LocalTime); Assert.Equal("Asia/Ho_Chi_Minh", schedule.TimeZoneId);
                Assert.Equal(instructions, created.Instructions);
                Assert.Equal(AutomationExecutionTarget.Background, created.ExecutionTarget); Assert.Equal(AutomationCompletionDelivery.None, created.CompletionDelivery);
            }
            else Assert.Empty(automations);
            Assert.Single(model.Results);
        }
        await using var reopened = Host(db, model, verifier);
        var retained = await reopened.Services.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null);
        Assert.Equal(decision == TriggerCommandAuthorizationDecision.Allow || decision is TriggerCommandAuthorizationDecision.ClassifierUnavailable or TriggerCommandAuthorizationDecision.ClassifierInvalidOutput && approve ? 1 : 0, retained.Count);
    }

    [Theory(Timeout = 60000)]
    [InlineData("instructions")]
    [InlineData("localTime")]
    [InlineData("timeZone")]
    [InlineData("executionTarget")]
    [InlineData("reportBack")]
    [InlineData("requiresTools")]
    [InlineData("response")]
    [InlineData("session")]
    [InlineData("occurrence")]
    [InlineData("Historical")]
    [InlineData("Memory")]
    [InlineData("Initiative")]
    [InlineData("Environment")]
    [InlineData("Missing")]
    [InlineData("source")]
    public async Task Approval_cannot_be_reused_for_changed_action_scope_or_non_user_provenance(string change)
    {
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.Allow);
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-changed-{Guid.NewGuid():N}.db"), new ProposingModel(), verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant",24))!;
        var context = new TriggerCommandContext(new(instance.InstanceId, LocalUserProfile.Id), Guid.NewGuid(), "UTC", "Review experience daily at 9 Vietnam time", "auto",
            TriggerAuthorizationClassification.CurrentUserTurn, Guid.NewGuid(), DateTimeOffset.UtcNow) { ResponseId = Guid.NewGuid() };
        var args = JsonNode.Parse("{\"instructions\":\"Review experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"Asia/Ho_Chi_Minh\",\"executionTarget\":\"backgroundSession\",\"reportBack\":false}")!.AsObject();
        var automations = services.GetRequiredService<IAutomationService>();
        var prepared = await TriggerScheduleCommands.PrepareAsync(definition, automations, ToolCatalog.AutomationCreate, JsonSerializer.SerializeToElement(args), context, default);
        Assert.Null(prepared.ErrorJson);
        var approval = new ToolApprovalGrant(Guid.NewGuid(), ToolCatalog.AutomationCreate, prepared.Request!.ActionHash, context.ResponseId, Guid.NewGuid(), Guid.NewGuid());
        if (change == "response") context = context with { ResponseId = Guid.NewGuid() };
        else if (change == "session") context = context with { SessionId = Guid.NewGuid() };
        else if (change == "source") context = context with { SourceEventId = Guid.NewGuid() };
        else if (change == "occurrence" || Enum.TryParse<TriggerAuthorizationClassification>(change, out _))
            context = context with { Classification = change == "occurrence" ? TriggerAuthorizationClassification.Occurrence : Enum.Parse<TriggerAuthorizationClassification>(change) };
        else if (change == "executionTarget") { args[change] = "currentSession"; args.Remove("reportBack"); }
        else if (change is "reportBack" or "requiresTools") args[change] = true;
        else args[change] = change switch { "localTime" => "10:00", "timeZone" => "UTC", _ => "Different work" };
        var result = await TriggerScheduleCommands.ExecuteAsync(definition, automations, ToolCatalog.AutomationCreate, JsonSerializer.SerializeToElement(args), context, default, verifier,
            services.GetRequiredService<IAgentInstanceStore>(), services.GetRequiredService<IAgentDefinitionStore>(), services.GetRequiredService<IMemoryStore>(),
            services.GetRequiredService<AdminAutomationAuthoringService>(), approval);
        Assert.Contains(context.Classification != TriggerAuthorizationClassification.CurrentUserTurn ? "forbidden" : "stale_approval", result.Text);
        Assert.Empty(verifier.Requests);
        Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId,LocalUserProfile.Id),null));
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_and_user_stop_never_save_or_reclassify_a_pending_automation(bool stop)
    {
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.ClassifierUnavailable);
        var model = new ProposingModel(); var output = new Output(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"semantic-expiry-{Guid.NewGuid():N}.db"), model, clock,
            configure: services => { services.RemoveAll<ITriggerCommandAuthorizer>(); services.AddSingleton<ITriggerCommandAuthorizer>(verifier); });
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId,SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session,output);
        await runtime.AttachAsync(); await runtime.SubmitUserTextAsync("Review experience daily at 9 Vietnam time");
        var notice = await output.Approval.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var approval = Assert.IsType<ApprovalRequestedOutput>(notice.Payload);
        if (stop) await runtime.CancelResponseAsync(notice.ResponseId!.Value);
        else clock.Advance(ToolApprovalLimits.Lifetime + TimeSpan.FromSeconds(1));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId,LocalUserProfile.Id),null));
        Assert.Single(verifier.Requests);
        Assert.NotEqual(ResponseApprovalResult.Accepted, await runtime.RespondApprovalAsync(notice.ResponseId!.Value,approval.ApprovalId,ToolApprovalDecision.Approve));
    }

    [Fact(Timeout = 60000)]
    public async Task Changed_target_revision_invalidates_an_exact_approval_before_verification_or_effects()
    {
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.Allow);
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-revision-{Guid.NewGuid():N}.db"), new ProposingModel(), verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 24))!;
        var authoring = services.GetRequiredService<AdminAutomationAuthoringService>();
        var trigger = new ScheduleTrigger(new DailySchedule(1, new TimeOnly(9, 0), "Asia/Ho_Chi_Minh"));
        var original = await authoring.SaveAsync(instance.InstanceId, null, 0, true, "Review", "Review experience", trigger, null, null,
            executionTarget: AutomationExecutionTarget.Background, completionDelivery: AutomationCompletionDelivery.None);
        var context = new TriggerCommandContext(original.Owner, Guid.NewGuid(), "UTC", "Disable this Automation", "auto",
            TriggerAuthorizationClassification.CurrentUserTurn, Guid.NewGuid(), DateTimeOffset.UtcNow) { ResponseId = Guid.NewGuid() };
        var args = JsonSerializer.SerializeToElement(new { automationId = original.AutomationId, expectedRevision = original.Revision });
        var automations = services.GetRequiredService<IAutomationService>();
        var prepared = await TriggerScheduleCommands.PrepareAsync(definition, automations, ToolCatalog.AutomationDisable, args, context, default);
        var approval = new ToolApprovalGrant(Guid.NewGuid(), ToolCatalog.AutomationDisable, prepared.Request!.ActionHash, context.ResponseId, Guid.NewGuid(), Guid.NewGuid());
        var changed = await authoring.SaveAsync(instance.InstanceId, original.AutomationId, original.Revision, true, "Changed name", original.Instructions,
            trigger, null, null);
        var result = await TriggerScheduleCommands.ExecuteAsync(definition, automations, ToolCatalog.AutomationDisable, args, context, default, verifier,
            automationAuthoring: authoring, approval: approval);
        Assert.Contains("conflict", result.Text);
        Assert.Empty(verifier.Requests);
        var retained = Assert.Single(await services.GetRequiredService<ITriggerStore>().ListAsync(original.Owner, null));
        Assert.Equal(changed.Revision, retained.Revision);
        Assert.Equal(AutomationStatus.Active, retained.Status);
    }

    [Theory(Timeout = 60000)]
    [InlineData(TriggerCommandAuthorizationDecision.Allow, true)]
    [InlineData(TriggerCommandAuthorizationDecision.Deny, false)]
    [InlineData(TriggerCommandAuthorizationDecision.ClassifierUnavailable, true)]
    public async Task Run_now_uses_the_same_semantic_and_exact_ui_recovery_contract(TriggerCommandAuthorizationDecision decision, bool approve)
    {
        var verifier = new Verifier(decision);
        var model = new ProposingModel(ToolCatalog.AutomationRun);
        var output = new Output();
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-run-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var original = await services.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instance.InstanceId, null, 0, true, "Review", "Review experience",
            new ScheduleTrigger(new DailySchedule(1, new TimeOnly(9, 0), "Asia/Ho_Chi_Minh")), null, null,
            executionTarget: AutomationExecutionTarget.Background, completionDelivery: AutomationCompletionDelivery.None);
        model.Arguments = JsonSerializer.Serialize(new { automationId = original.AutomationId, expectedRevision = original.Revision });
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, output);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Chạy Automation xem lại kinh nghiệm ngay bây giờ."));
        if (decision == TriggerCommandAuthorizationDecision.ClassifierUnavailable)
        {
            var notice = await output.Approval.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var pending = Assert.IsType<ApprovalRequestedOutput>(notice.Payload);
            Assert.Contains("automation.run", pending.Details["Exact action"]);
            Assert.Equal(ResponseApprovalResult.Accepted, await runtime.RespondApprovalAsync(notice.ResponseId!.Value, pending.ApprovalId,
                approve ? ToolApprovalDecision.Approve : ToolApprovalDecision.Reject));
        }
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ToolCatalog.AutomationRun, Assert.Single(verifier.Requests).ToolName);
        var result = Assert.Single(model.Results);
        if (decision == TriggerCommandAuthorizationDecision.Deny) Assert.Equal("authorization_denied", result.GetProperty("error").GetString());
        else
        {
            var occurrence = await services.GetRequiredService<ITriggerStore>().GetOccurrenceAsync(original.Owner, result.GetProperty("occurrenceId").GetGuid());
            Assert.NotNull(occurrence);
            Assert.Equal(original.AutomationId, occurrence.AutomationId);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Pending_approval_survives_host_restart_without_another_verifier_call()
    {
        var db = Path.Combine(Path.GetTempPath(), $"semantic-restart-{Guid.NewGuid():N}.db");
        var model = new ProposingModel();
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.ClassifierUnavailable);
        var firstOutput = new Output();
        Guid sessionId, instanceId;
        string actionHash, exactPreview;
        await using (var first = Host(db, model, verifier))
        {
            var services = first.Services;
            instanceId = (await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = session.SessionId;
            await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, firstOutput);
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Review experience daily at 9 Vietnam time, quietly in background"));
            var notice = await firstOutput.Approval.Task.WaitAsync(TimeSpan.FromSeconds(20));
            exactPreview = Assert.IsType<ApprovalRequestedOutput>(notice.Payload).Details["Exact action"];
            actionHash = Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instanceId, LocalUserProfile.Id), sessionId)).Approval!.ActionHash;
        }
        await using var restarted = Host(db, model, verifier);
        var s = restarted.Services;
        var restored = (await s.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!;
        await using var resumed = s.GetRequiredService<SessionRuntimeFactory>().Create(restored, new Output());
        await resumed.AttachAsync();
        var waiting = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instanceId, LocalUserProfile.Id), sessionId));
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.Equal(actionHash, waiting.Approval!.ActionHash);
        using (var preview = JsonDocument.Parse(waiting.Approval.Preview))
            Assert.Equal(exactPreview, preview.RootElement.GetProperty("details").GetProperty("Exact action").GetString());
        var a = waiting.Approval;
        var queued = await resumed.ControlAgentRunAsync(waiting.AgentRunId, waiting.Revision, a.ApprovalId, a.Revision, a.ActionHash, AgentRunApprovalDecision.Approved);
        var now = DateTimeOffset.UtcNow;
        var claimed = await s.GetRequiredService<IAgentRunStore>().ApplyAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        Assert.True(await resumed.DispatchAgentRunAsync(claimed.AgentRunId, false));
        await resumed.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(verifier.Requests);
        var created = Assert.Single(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null));
        Assert.Equal("Review learned experience", created.Instructions);
        Assert.Equal(AutomationExecutionTarget.Background, created.ExecutionTarget);
        Assert.Single(model.Results);
    }

    [Theory(Timeout = 60000)]
    [InlineData(TriggerCommandAuthorizationDecision.Allow)]
    [InlineData(TriggerCommandAuthorizationDecision.Deny)]
    public async Task Current_timezone_clarification_supplies_bounded_history_as_evidence_without_granting_authority(TriggerCommandAuthorizationDecision decision)
    {
        var verifier = new Verifier(decision);
        var model = new ProposingModel { AskTimeZoneFirst = true };
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-clarification-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new Output());
        await runtime.AttachAsync();
        const string task = "Every day at 9am, review learned experience quietly in background.";
        Assert.True(await runtime.SubmitUserTextAsync(task));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(verifier.Requests);
        Assert.True(await runtime.SubmitUserTextAsync("Dùng múi giờ Việt Nam."));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var request = Assert.Single(verifier.Requests);
        Assert.Equal("Dùng múi giờ Việt Nam.", request.CurrentUserText);
        using var history = JsonDocument.Parse(request.ConversationEvidenceJson!);
        Assert.Contains(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == task);
        // This fixture sends no display receipt, so unseen assistant prose cannot enter Evidence.
        Assert.DoesNotContain(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == "Which timezone should I use?");
        Assert.DoesNotContain(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == request.CurrentUserText);
        Assert.True(request.ConversationEvidenceJson!.Length <= 12_000);
        var retained = await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null);
        Assert.Equal(decision == TriggerCommandAuthorizationDecision.Allow ? 1 : 0, retained.Count);
    }

    [Fact(Timeout = 60000)]
    public async Task Explicit_disabled_creation_persists_the_state_that_was_semantically_verified()
    {
        var model = new ProposingModel { Arguments = "{\"instructions\":\"Review experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"Asia/Ho_Chi_Minh\",\"executionTarget\":\"backgroundSession\",\"enabled\":false}" };
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.Allow);
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-disabled-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new Output());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Create a disabled daily review at 9 Vietnam time"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(Assert.Single(verifier.Requests).ProposedAction.GetProperty("proposedEnabled").GetBoolean());
        var created = Assert.Single(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
        Assert.Equal(AutomationStatus.Disabled, created.Status);
        Assert.Null(created.NextOccurrenceAtUtc);
    }

    [Fact(Timeout = 60000)]
    public async Task Unavailable_structured_timezone_is_a_schedule_error_before_semantic_verification()
    {
        var model = new ProposingModel { Arguments = "{\"instructions\":\"Review experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"America/NoSuchZone\",\"executionTarget\":\"backgroundSession\"}" };
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.Allow);
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-zone-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new Output());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Review experience daily at 9 in this timezone"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(verifier.Requests);
        Assert.Equal("schedule_validation_failed", Assert.Single(model.Results).GetProperty("error").GetString());
        Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
    }

    private static ExperienceHost Host(string db, ILanguageModel model, ITriggerCommandAuthorizer verifier) => new(db, model, configure: services =>
    { services.RemoveAll<ITriggerCommandAuthorizer>(); services.AddSingleton(verifier); });

    private sealed class Verifier(TriggerCommandAuthorizationDecision decision) : ITriggerCommandAuthorizer
    {
        public List<AutomationAuthorizationRequest> Requests { get; } = [];
        public ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(proposed); return ValueTask.FromResult(new TriggerCommandAuthorizationVerdict(decision)); }
    }

    private sealed class Output : ISessionOutput
    {
        public ConcurrentQueue<SessionOutput> Items { get; } = new();
        public TaskCompletionSource<SessionOutput> Approval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask PublishAsync(SessionOutput output, CancellationToken ct = default)
        { Items.Enqueue(output); if (output.Payload is ApprovalRequestedOutput) Approval.TrySetResult(output); return ValueTask.CompletedTask; }
    }

    private sealed class ProposingModel(string toolName = ToolCatalog.AutomationCreate) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true,true,Tools:true,StructuredOutput:true);
        public List<JsonElement> Results { get; } = [];
        public string? Arguments { get; set; }
        public bool AskTimeZoneFirst { get; set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested();
            var transcript = request.Interaction ?? request.Messages;
            var user = transcript.ToList().FindLastIndex(m => m.Role == ModelRole.User);
            if (AskTimeZoneFirst)
            {
                AskTimeZoneFirst = false;
                yield return new ModelSemanticResponseReady(new("Which timezone should I use?", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            var result = transcript.Skip(user+1).LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == toolName);
            if (result is not null)
            {
                using var json = JsonDocument.Parse(result.Text); Results.Add(json.RootElement.Clone());
                yield return new ModelSemanticResponseReady(new("Automation result received.",new(ModelSpeechMode.None,null),[]));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (request.Tools?.Any(t => t.Name == toolName) != true)
                yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = toolName, limit = 1 })));
            else yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), toolName,
                Arguments ??
                "{\"instructions\":\"Review learned experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"Asia/Ho_Chi_Minh\",\"executionTarget\":\"backgroundSession\",\"reportBack\":false}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
