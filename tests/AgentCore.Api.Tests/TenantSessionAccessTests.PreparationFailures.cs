using System.Net.Http.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Attachments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class PreparationFailure(string stage)
    {
        public bool Enabled = true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Error { get; } = new IOException("Controlled private preparation failure; never publish this exception message.");
        public bool NullDecision => stage == "null-brain" && Volatile.Read(ref Enabled);
        public async ValueTask CheckAsync(string current)
        {
            if (!Volatile.Read(ref Enabled) || stage != current && stage != "late-" + current) return;
            Entered.TrySetResult();
            if (stage.StartsWith("late-", StringComparison.Ordinal)) await Release.Task; // Deliberately ignores cancellation.
            throw Error;
        }
    }
    private sealed class PreparationProcessor(IAttachmentProcessor inner, PreparationFailure failure) : IAttachmentProcessor
    {
        public string Version => inner.Version;
        public async ValueTask<AttachmentProcessResult> ProcessSourceAsync(AttachmentRecord source, byte[] bytes, CancellationToken ct = default)
        { await failure.CheckAsync("context"); return await inner.ProcessSourceAsync(source, bytes, ct); }
        public ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(Guid session, IReadOnlyList<Guid> ids, CancellationToken ct = default) => inner.ProcessTurnAsync(session, ids, ct);
    }
    private sealed class PreparationBrain(PreparationFailure failure) : IAgentBrain
    {
        private readonly DefaultAgentBrain inner = new(new PromptContextBuilder());
        public async ValueTask<AgentDecision> DecideAsync(AgentContext context, Guid response, CancellationToken ct = default)
        {
            if (failure.NullDecision) { failure.Entered.TrySetResult(); return null!; }
            await failure.CheckAsync("brain"); return await inner.DecideAsync(context, response, ct);
        }
    }
    private sealed class PreparationErrorObserver(PreparationFailure failure) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => new Observer(failure);
        public void Dispose() { }
        private sealed class Observer(PreparationFailure failure) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            { if (ReferenceEquals(error, failure.Error)) failure.Observed.TrySetResult(); }
        }
    }
    private sealed class PreparationHoldModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
        }
    }
    [Theory] [InlineData(false, "context")] [InlineData(true, "context")]
    [InlineData(false, "brain")] [InlineData(true, "brain")]
    [InlineData(false, "null-brain")] [InlineData(true, "null-brain")]
    public async Task Tenant_preparation_failure_settles_claim_without_provider_or_replay_and_later_message_can_run(bool sqlite, string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-preparation-failure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var failure = new PreparationFailure(stage); var model = new AttachmentReadModel();
            void Configure(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton<IAttachmentProcessor>(p => new PreparationProcessor(
                    new AttachmentProcessor(p.GetRequiredService<IAttachmentStore>(), p.GetRequiredService<IOfficeProcessor>()), failure)));
                services.Replace(ServiceDescriptor.Singleton<IAgentBrain>(new PreparationBrain(failure)));
            }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(); var value = await PrepareAttachmentRead(host.Services, owner, "text"); var v = value.Execution;
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var sessions = host.Services.GetRequiredService<ITenantSessionStore>();
            var submitted = await host.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Read this original", [v.Id], await Revision(owner, v.Tenant), 1);
            await failure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AgentRun failed;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (true)
                {
                    failed = (await runs.FindTenantRunAsync(v.Tenant, submitted.RunId, deadline.Token))!;
                    if (failed.IsTerminal) break;
                    await Task.Delay(25, deadline.Token);
                }
            }
            Assert.Equal(AgentRunStatus.Failed, failed.Status); Assert.Equal("preparation-failed", failed.Failure!.Code);
            Assert.Null(failed.Claim); Assert.Equal(1, failed.AttemptCount); Assert.Empty(model.Requests);
            Assert.Equal(AgentRunSideEffectDisposition.None, failed.SideEffect.Disposition); Assert.Null(failed.Result);
            Assert.DoesNotContain("private preparation failure", failed.Failure.Summary);
            var snapshot = (await sessions.FindTenantSessionAsync(v.Tenant, v.Session))!.Snapshot;
            Assert.DoesNotContain((await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(v.Tenant, v.Session, null, null, 100))!.History.Items,
                e => e.Role == ConversationRole.Assistant && e.ResponseId == failed.ResponseId);
            Volatile.Write(ref failure.Enabled, false);
            var next = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new {
                messageId = Guid.NewGuid(), text = "This is a new independent request", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = snapshot.Revision }));
            var done = await AttachmentTerminal(runs, v.Tenant, next.GetProperty("runId").GetGuid());
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.Single(model.Requests); Assert.Equal(AgentRunStatus.Failed, (await runs.FindTenantRunAsync(v.Tenant, submitted.RunId))!.Status);
            Assert.Equal(0, host.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false, "late-context")] [InlineData(true, "late-context")]
    [InlineData(false, "late-brain")] [InlineData(true, "late-brain")]
    public async Task Tenant_preparation_late_failure_after_HTTP_cancellation_cannot_fail_new_Run_or_hold_capacity(bool sqlite, string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-late-preparation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var failure = new PreparationFailure(stage); var model = new PreparationHoldModel();
        try
        {
            void Configure(IServiceCollection services)
            {
                services.AddLogging(logs => logs.AddProvider(new PreparationErrorObserver(failure)));
                services.Replace(ServiceDescriptor.Singleton<IAttachmentProcessor>(p => new PreparationProcessor(
                    new AttachmentProcessor(p.GetRequiredService<IAttachmentStore>(), p.GetRequiredService<IOfficeProcessor>()), failure)));
                services.Replace(ServiceDescriptor.Singleton<IAgentBrain>(new PreparationBrain(failure)));
            }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(); var value = await PrepareAttachmentRead(host.Services, owner, "text"); var v = value.Execution;
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var sessions = host.Services.GetRequiredService<ITenantSessionStore>();
            var initial = await host.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Read this original", [v.Id], await Revision(owner, v.Tenant), 1);
            await failure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var run = (await runs.FindTenantRunAsync(v.Tenant, initial.RunId))!;
            Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/agent-runs/{run.AgentRunId}/cancel", new {
                expectedRevision = await Revision(owner, v.Tenant), expectedRunRevision = run.Revision })).StatusCode);
            Assert.False(failure.Release.Task.IsCompleted); Assert.Equal(0, model.Calls);
            Volatile.Write(ref failure.Enabled, false);
            var snapshot = (await sessions.FindTenantSessionAsync(v.Tenant, v.Session))!.Snapshot;
            var next = await host.Services.GetRequiredService<TenantRunService>().SubmitTextAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "A new independent message", await Revision(owner, v.Tenant), snapshot.Revision);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            failure.Release.TrySetResult(); await failure.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(v.Tenant, initial.RunId))!.Status);
            Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(v.Tenant, next.RunId))!.Status);
            model.Release.TrySetResult(); var done = await AttachmentTerminal(runs, v.Tenant, next.RunId);
            Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Equal(1, model.Calls);
            Assert.DoesNotContain((await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(v.Tenant, v.Session, null, null, 100))!.History.Items,
                e => e.Role == ConversationRole.Assistant && e.ResponseId == run.ResponseId);
            Assert.Equal(0, host.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally
        {
            failure.Release.TrySetResult(); model.Release.TrySetResult();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
