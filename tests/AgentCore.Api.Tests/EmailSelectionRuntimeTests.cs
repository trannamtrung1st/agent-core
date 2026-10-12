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
    [InlineData("alreadyBound")]
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
        var account = await s.GetRequiredService<CredentialService>().CreateEmailAccountAsync("Selected mail", EmailAccountProfile.Custom("demo@example.test"), "protected-fixture-4821");
        if (decision == "alreadyBound")
        {
            var credentials = s.GetRequiredService<CredentialService>();
            var other = await credentials.CreateEmailAccountAsync("Other mail", EmailAccountProfile.Custom("other@example.test"), "other-fixture-password");
            await credentials.BindAsync(owner.InstanceId, other.CredentialId, "primary", owner.Revision);
            owner = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
            await credentials.BindAsync(owner.InstanceId, account.CredentialId, "secondary", owner.Revision);
            owner = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
            model.SelectedAlias = "secondary";
        }
        var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner.InstanceId, SessionMode.Text);
        await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new Output());
        await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Show my latest 10 emails"));
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
            Assert.True(await runtime.DecideEmailAccountAsync(request.RequestId, decision is "allow" or "alreadyBound" ? account.CredentialId : null, "mail", owner.Revision, default));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<AgentCoreException>(() => runtime.DecideEmailAccountAsync(request.RequestId, account.CredentialId, "mail", owner.Revision, default));
        }
        var runs = await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(owner.InstanceId, LocalUserProfile.Id), snapshot.SessionId);
        if (decision is "allow" or "alreadyBound")
        {
            Assert.True(runs.Count == 2, System.Text.Json.JsonSerializer.Serialize(new { source.Admission.Activation.SourceEntryIds, Source = runtime.Snapshot.Entries.Select(e => new { e.EntryId, e.Sequence, e.Role }), Selection = await s.GetRequiredService<IEmailStore>().GetSelectionAsync(request.RequestId), Safe = AgentCore.Application.Email.EmailSelectionSafety.CanContinue(source, request), Configured = s.GetRequiredService<IToolConfigurationGate>().IsConfigured(ToolCatalog.EmailSearch) }));
            var continuation = Assert.Single(runs, r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation);
            Assert.Equal(AgentRunStatus.Completed, continuation.Status);
            Assert.Equal(source.AgentRunId, continuation.Admission.Activation.SourceAgentRunId); Assert.Empty(continuation.Admission.Activation.SourceEntryIds);
            Assert.NotEqual(source.ResponseId, continuation.ResponseId);
            Assert.Equal(decision == "alreadyBound" ? 2 : 0, source.Admission.Configuration!.CredentialBindings.Count);
            Assert.Contains(continuation.Admission.Configuration!.CredentialBindings, b => b.CredentialId == account.CredentialId && b.Reference == model.SelectedAlias);
            var receipt = await s.GetRequiredService<IEmailStore>().GetSelectionAsync(request.RequestId);
            Assert.Equal(account.CredentialId, receipt!.SelectedAccountId); Assert.Equal(model.SelectedAlias, receipt.SelectedAccountRef);
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Text.Contains("Owner-selected account (JSON identity data): {\"accountRef\":\"" + model.SelectedAlias + "\"}")));
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.EmailSearch && m.Text.Contains("\"accountRef\":\"" + model.SelectedAlias + "\"")));
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
    [Theory]
    [InlineData(ToolCatalog.EmailSearch)]
    [InlineData(ToolCatalog.EmailAccounts)]
    public async Task Unattended_disconnected_Gmail_read_or_inventory_terminalizes_attention_without_consent(string tool)
    {
        var db = Path.Combine(Path.GetTempPath(), $"gmail-unattended-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db, new DirectEmailModel(tool)); var services = host.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 25);
        var credentials = services.GetRequiredService<CredentialService>();
        var account = await credentials.CreateEmailAccountAsync("Disconnected Gmail", EmailAccountProfile.Gmail(), "");
        await credentials.BindAsync(owner.InstanceId, account.CredentialId, "mail", owner.Revision);
        var automation = await services.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(owner.InstanceId, null, 0, true, 3600, "Inspect the bound Gmail account", null, null);
        await services.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(owner.InstanceId, automation.AutomationId, automation.Revision);
        await AutomationJourneyTests.Intake(services); await services.ExecuteRunsAsync(100);
        var run = Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListAsync(new(owner.InstanceId, LocalUserProfile.Id), 100));
        Assert.Equal(AgentRunStatus.Completed, run.Status); Assert.Equal(AgentRunOutcomeKind.NeedsAttention, run.Result!.OutcomeKind);
        Assert.Contains("/admin/connections/credentials", run.Result.Text);
        Assert.Null(await services.GetRequiredService<IEmailStore>().GetSelectionAsync(run.AgentRunId));
        Assert.Equal("NotConnected", (await services.GetRequiredService<IEmailOAuthService>().ConnectionAsync(account.CredentialId, default)).Status);
    }
    private sealed class DirectEmailModel(string tool) : ILanguageModel
    {
        public ModelCapabilities Capabilities => new ScriptedLanguageModel().Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new ModelToolCallEvent(new("mail", tool, tool == ToolCatalog.EmailSearch ? "{\"accountRef\":\"mail\"}" : "{}")); yield return new ModelCompleted(ModelStopReason.ToolCalls); }
    }
    private sealed class Output : ISessionOutput { public ValueTask PublishAsync(SessionOutput output, CancellationToken ct = default) => ValueTask.CompletedTask; }
    private sealed class SelectionModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new ScriptedLanguageModel().Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public string SelectedAlias { get; set; } = "mail";
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            var continuation = request.Messages.Any(m => m.Text.Contains("Continue the owner's email task", StringComparison.Ordinal));
            var hasResult = request.Messages.Any(m => m.Role == ModelRole.Tool);
            const string marker = "Owner-selected account (JSON identity data): ";
            var selectionLine = request.Messages.SelectMany(m => m.Text.Split('\n')).FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));
            string? selectedAlias = null;
            if (selectionLine is not null)
            {
                using var hint = System.Text.Json.JsonDocument.Parse(selectionLine[marker.Length..]);
                selectedAlias = hint.RootElement.GetProperty("accountRef").GetString();
            }
            if (!hasResult && request.Tools?.Any(t => t.Name == (continuation ? ToolCatalog.EmailSearch : ToolCatalog.EmailRequestAccount)) == true)
                yield return new ModelToolCallEvent(new("email-select", continuation ? ToolCatalog.EmailSearch : ToolCatalog.EmailRequestAccount,
                    continuation ? System.Text.Json.JsonSerializer.Serialize(new { accountRef = selectedAlias, limit = 10 }) : "{}"));
            else yield return new ModelTextDelta(continuation ? "Email inspected with the selected account." : "Select an account in the trusted owner UI.");
            yield return new ModelCompleted(!hasResult ? ModelStopReason.ToolCalls : ModelStopReason.Completed);
        }
    }
}
