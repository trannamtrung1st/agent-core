using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentCore.Application.Admin;
using AgentCore.Application.Triggers;
using AgentCore.Application.Events;
using AgentCore.Application.Credentials;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class EmailSelectionRuntimeTests
{
    [Theory]
    [InlineData("allow")]
    [InlineData("cancel")]
    [InlineData("newerInput")]
    [InlineData("foreignSession")]
    [InlineData("expiry")]
    [InlineData("staleRevision")]
    [InlineData("detach")]
    public async Task Owner_selection_continues_once_with_fresh_pins_and_never_duplicates_user_input(string decision)
    {
        var model = new SelectionModel(); var db = Path.Combine(Path.GetTempPath(), $"email-selection-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db, model); var s = host.Services;
        var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 25);
        var account = await s.GetRequiredService<CredentialService>().CreateEmailAccountAsync("Selected mail", EmailAccountProfile.Gmail("demo@example.test"), "protected-fixture-4821");
        var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner.InstanceId, SessionMode.Text);
        await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new Output());
        await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Inspect email after account selection"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var source = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId, LocalUserProfile.Id), snapshot.SessionId));
        var request = await s.GetRequiredService<IEmailStore>().GetSelectionAsync(source.AgentRunId);
        Assert.True(request is not null, System.Text.Json.JsonSerializer.Serialize(new { source.Status, source.Checkpoint, Requests = model.Requests.Select(r => new { Tools = r.Tools?.Select(t => t.Name), r.Messages }) })); Assert.Equal("pending", request!.Status);
        Assert.Equal(AgentRunStatus.Completed, source.Status);
        Assert.DoesNotContain(model.Requests, r => r.Messages.Any(m => m.Text.Contains("demo@example.test", StringComparison.Ordinal)));
        Assert.DoesNotContain(model.Requests, r => string.Join('\n', r.Messages.Select(m => m.Text)).Contains("protected-fixture-4821"));
        if (decision == "newerInput")
        {
            Assert.True(await runtime.SubmitUserTextAsync("A newer task")); await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<AgentCoreException>(() => runtime.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision, default));
        }
        else if (decision == "expiry")
        {
            await s.GetRequiredService<IEmailStore>().UpdateSelectionAsync(request with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, "pending");
            await Assert.ThrowsAsync<AgentCoreException>(() => runtime.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision, default));
        }
        else if (decision == "staleRevision")
            await Assert.ThrowsAsync<AgentCoreException>(() => runtime.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision + 1, default));
        else if (decision == "detach")
        {
            await runtime.DetachAsync(); await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<AgentCoreException>(() => runtime.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision, default));
        }
        else if (decision == "foreignSession")
        {
            var other = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner.InstanceId, SessionMode.Text);
            await using var foreign = s.GetRequiredService<SessionRuntimeFactory>().Create(other, new Output()); await foreign.AttachAsync();
            await Assert.ThrowsAsync<AgentCoreException>(() => foreign.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision, default));
        }
        else
        {
            Assert.True(await runtime.DecideEmailAccountAsync(request.RequestId, decision == "allow" ? account.CredentialId : null, "mail", owner.Revision, default));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<AgentCoreException>(() => runtime.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision, default));
        }
        var runs = await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId, LocalUserProfile.Id), snapshot.SessionId);
        if (decision == "allow")
        {
            Assert.True(runs.Count == 2, System.Text.Json.JsonSerializer.Serialize(new { source.Admission.Activation.SourceEntryIds, Source = runtime.Snapshot.Entries.Select(e => new { e.EntryId, e.Sequence, e.Role }), Selection = await s.GetRequiredService<IEmailStore>().GetSelectionAsync(request.RequestId), Safe = AgentCore.Application.Email.EmailSelectionSafety.CanContinue(source, request), Configured = s.GetRequiredService<IToolConfigurationGate>().IsConfigured(ToolCatalog.EmailSearch) }));
            var continuation = Assert.Single(runs, r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation);
            Assert.Equal(AgentRunStatus.Completed, continuation.Status);
            Assert.Equal(source.AgentRunId, continuation.Admission.Activation.SourceAgentRunId); Assert.Empty(continuation.Admission.Activation.SourceEntryIds);
            Assert.NotEqual(source.ResponseId, continuation.ResponseId); Assert.Empty(source.Admission.Configuration!.CredentialBindings);
            Assert.Equal(account.CredentialId, Assert.Single(continuation.Admission.Configuration!.CredentialBindings).CredentialId);
            Assert.Contains(runtime.Snapshot.Entries, e => e.Text == "Email inspected with the selected account.");
        }
        else Assert.DoesNotContain(runs, r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation);
        Assert.Equal(decision == "newerInput" ? 2 : 1, runtime.Snapshot.Entries.Count(e => e.Role == ConversationRole.User));
    }
    [Fact]
    public async Task Unattended_account_request_terminalizes_as_attention_without_a_picker()
    {
        var db = Path.Combine(Path.GetTempPath(), $"email-unattended-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db, new SelectionModel()); var s = host.Services;
        var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 25);
        var automation = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(owner.InstanceId, null, 0, true, 3600, "Inspect email after account selection", null, null);
        await s.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(owner.InstanceId, automation.AutomationId, automation.Revision);
        await AutomationJourneyTests.Intake(s); await s.ExecuteRunsAsync(100);
        var run = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(owner.InstanceId, LocalUserProfile.Id), 100));
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(AgentRunOutcomeKind.NeedsAttention, run.Result!.OutcomeKind);
        Assert.Contains("/admin/connections/credentials", run.Result.Text);
        Assert.Null(await s.GetRequiredService<IEmailStore>().GetSelectionAsync(run.AgentRunId));
    }
    private sealed class Output : ISessionOutput { public ValueTask PublishAsync(SessionOutput output, CancellationToken ct = default) => ValueTask.CompletedTask; }
    private sealed class SelectionModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new ScriptedLanguageModel().Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            var continuation = request.Messages.Any(m => m.Text.Contains("Continue the owner's email task", StringComparison.Ordinal));
            var hasResult = request.Messages.Any(m => m.Role == ModelRole.Tool);
            if (!hasResult && request.Tools?.Any(t => t.Name == (continuation ? ToolCatalog.EmailSearch : ToolCatalog.EmailRequestAccount)) == true)
                yield return new ModelToolCallEvent(new("email-select", continuation ? ToolCatalog.EmailSearch : ToolCatalog.EmailRequestAccount,
                    continuation ? """{"query":"harness"}""" : "{}"));
            else yield return new ModelTextDelta(continuation ? "Email inspected with the selected account." : "Select an account in the trusted owner UI.");
            yield return new ModelCompleted(!hasResult ? ModelStopReason.ToolCalls : ModelStopReason.Completed);
        }
    }
}
