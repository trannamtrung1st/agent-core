using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_request_isolation_actual_MessagePack_publishes_terminal_state_and_reconnects_without_a_new_root(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-isolated-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new IsolatedChildModel(false); model.ReleaseChild.TrySetResult();
        await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
        factory.UseKestrel(0); using var client = factory.CreateClient();
        var setup = await StartTenantBackground(factory, client, submit: false, independentTask: true, fileCapabilities: ["tasks.delegate"], requestIsolated: true);
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent;
        Assert.NotNull(repo); var start = new System.Diagnostics.ProcessStartInfo("node", "tenant-request-isolation.mjs") {
            WorkingDirectory = Path.Combine(repo.FullName, "tests", "realtime-js"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BASE_URL"] = client.BaseAddress!.ToString().TrimEnd('/'); start.Environment["TENANT_ID"] = setup.Tenant.ToString("D"); start.Environment["SESSION_ID"] = setup.Session.ToString("D");
        using var process = System.Diagnostics.Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        try { await process.WaitForExitAsync(deadline.Token); } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("Tenant request isolation wire passed", await stdout);
    }
    [Fact(Timeout = 60000)]
    public async Task Task_runtime_shutdown_waits_for_unregistered_Runtime_retirement_before_disposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "task-runtime-retirement-" + Guid.NewGuid().ToString("N"));
        var model = new IsolatedChildModel(false); model.ReleaseChild.TrySetResult();
        await using var factory = new TenantBackgroundMemoryFactory(root, model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var setup = await StartTenantBackground(factory, owner, submit: false);
        var host = factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantSessionRuntimeHost>();
        var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterRuntimeUnregistered = async run => { if (run.SessionId == setup.Session) { unregistered.TrySetResult(); await release.Task; } };
        try
        {
            var response = await owner.PostAsJsonAsync($"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}/messages", new {
                messageId = Guid.NewGuid(), text = "Start exact Task shutdown check", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 });
            Assert.True(response.IsSuccessStatusCode);
            await unregistered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var stop = host.StopAsync();
            await Task.Delay(50); Assert.False(stop.IsCompleted);
            release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { release.TrySetResult(); }
    }
    private sealed class IsolatedChildModel(bool report, bool quiet = false, bool childFirst = false) : TenantBackgroundModel
    {
        public TaskCompletionSource ChildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseChild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChildReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ParentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseParent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Origin(request) == ExecutionOrigin.DelegatedTask) { ChildEntered.TrySetResult(); await ReleaseChild.Task.WaitAsync(cancellationToken); }
            if (childFirst && Origin(request) == ExecutionOrigin.UserMessage && request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == "tasks.delegate"))
            { ParentEntered.TrySetResult(); await ReleaseParent.Task.WaitAsync(cancellationToken); }
            try
            {
                await foreach (var value in base.GenerateAsync(request, cancellationToken))
                {
                    if (!report && value is ModelToolCallEvent { Call.Name: "tasks.delegate" } call)
                        yield return new ModelToolCallEvent(call.Call with { ArgumentsJson = call.Call.ArgumentsJson.Replace("\"reportOnCompletion\":true", "\"reportOnCompletion\":false", StringComparison.Ordinal) });
                    else if (quiet && value is ModelToolCallEvent { Call.Name: ToolCatalog.WorkComplete } complete)
                        yield return new ModelToolCallEvent(complete.Call with { ArgumentsJson = complete.Call.ArgumentsJson.Replace("\"outcome\":\"Response\"", "\"outcome\":\"NoAction\"", StringComparison.Ordinal) });
                    else yield return value;
                }
            }
            finally { if (Origin(request) == ExecutionOrigin.DelegatedTask) ChildReturned.TrySetResult(); }
        }
    }
    [Theory(Timeout = 90000)]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    [InlineData(false, true, false, true)] [InlineData(true, true, false, true)]
    public async Task Tenant_request_isolation_waits_actual_child_and_report_accounting_without_another_model_call(bool sqlite, bool report, bool quiet = false, bool childFirst = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-isolated-child-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new IsolatedChildModel(report, quiet, childFirst);
        await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        try
        {
            var (tenant, session, source) = await StartTenantBackground(factory, owner, reportReads: report, independentTask: true,
                fileCapabilities: ["tasks.delegate"], requestIsolated: true);
            await model.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            if (childFirst)
            {
                await model.ParentEntered.Task.WaitAsync(TimeSpan.FromSeconds(15)); model.ReleaseChild.TrySetResult();
                var child = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == source);
                for (var i = 0; i < 200 && (await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))?.IsTerminal != true; i++) await Task.Delay(25);
                Assert.True((await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!.IsTerminal);
                Assert.False((await runs.FindTenantRunAsync(tenant, source))!.IsTerminal); model.ReleaseParent.TrySetResult();
            }
            for (var i = 0; i < 200 && (await runs.FindTenantRunAsync(tenant, source))?.IsTerminal != true; i++) await Task.Delay(25);
            Assert.Equal(AgentRunStatus.Completed, (await runs.FindTenantRunAsync(tenant, source))!.Status);
            var parent = (await sessions.FindTenantSessionAsync(tenant, session))!;
            Assert.Equal(SessionLifecycleStatus.Active, parent.Snapshot.LifecycleStatus);
            Assert.False(await factory.Services.GetRequiredService<ITenantRequestIsolationControl>().SettleRequestAsync(parent.Owner));
            if (!report)
            {
                var api = $"/api/v2/tenants/{tenant}"; var isolation = api + $"/agents/{parent.Owner.AgentInstanceId}/state-isolation";
                var preview = await Json(await owner.PostAsync(isolation + "/presets/DefinitionDefaults", null));
                Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PostAsJsonAsync(isolation + "/apply", new {
                    expectedRevision = preview.GetProperty("securityRevision").GetInt64(), expected = preview.GetProperty("plan").GetProperty("expected"),
                    draft = preview.GetProperty("plan").GetProperty("draft"), fingerprint = preview.GetProperty("plan").GetProperty("fingerprint").GetString() })).StatusCode);
                var refused = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "A separate request while the child settles", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = parent.Snapshot.Revision });
                Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode); Assert.Contains("fresh_session_required", await refused.Content.ReadAsStringAsync());
                Assert.Single(await runs.ListTenantRunIdsAsync(tenant, session));
            }
            model.ReleaseChild.TrySetResult();
            for (var i = 0; i < 400 && (await sessions.FindTenantSessionAsync(tenant, session))?.Snapshot.LifecycleStatus != SessionLifecycleStatus.Completed; i++)
            {
                await factory.Services.GetRequiredService<ITenantRequestIsolationControl>().SettleRequestAsync(parent.Owner);
                await Task.Delay(25);
            }
            var settlement = await factory.Services.GetRequiredService<ITenantRequestIsolationStore>().ReadTenantRequestIsolationAsync(parent.Owner);
            var currentChildren = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Where(s => s.Snapshot.Origin.OriginatingSessionId == session).ToArray();
            var facts = new List<object>();
            foreach (var currentChild in currentChildren)
                foreach (var childId in await runs.ListTenantRunIdsAsync(tenant, currentChild.Owner.SessionId))
                {
                    var currentRun = (await runs.FindTenantRunAsync(tenant, childId))!;
                    var savedTask = currentRun.Admission.DelegatedTask is { } work ? await factory.Services.GetRequiredService<ITenantTaskStore>().FindTaskAsync(tenant, work.TaskId) : null;
                    facts.Add(new { currentRun.Status, currentRun.SideEffect, currentRun.Failure, Task = savedTask is null ? null : new { savedTask.State, savedTask.Reason, savedTask.TerminalRunId } });
                }
            Assert.True((await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot.LifecycleStatus == SessionLifecycleStatus.Completed,
                System.Text.Json.JsonSerializer.Serialize(new { settlement?.Ready, Source = (await runs.FindTenantRunAsync(tenant, source))!.SideEffect, Facts = facts, Failures = model.Failures }));
            var completedChild = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == source);
            var childRun = (await runs.FindTenantRunAsync(tenant, completedChild.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
            Assert.Equal(quiet ? AgentRunOutcomeKind.NoAction : AgentRunOutcomeKind.Response, childRun.Result!.OutcomeKind);
            Assert.Null(await runs.FindTenantCompletionAsync(childRun.Owner, childRun.AgentRunId));
            var task = (await factory.Services.GetRequiredService<ITenantTaskStore>().FindTaskAsync(tenant, childRun.Admission.DelegatedTask!.TaskId))!;
            Assert.True(AgentCore.Domain.Tasks.TaskLimits.Terminal(task.State));
            Assert.Equal(childRun.AgentRunId, task.TerminalRunId);
            var reportRuns = new List<AgentRun>();
            foreach (var id in await runs.ListTenantRunIdsAsync(tenant, session))
                if (await runs.FindTenantRunAsync(tenant, id) is { Admission.TaskReport: not null } item) reportRuns.Add(item);
            if (report) Assert.True(Assert.Single(reportRuns).IsTerminal);
            else Assert.Empty(reportRuns);
            var requests = model.Requests.Count; Assert.False(await factory.Services.GetRequiredService<ITenantRequestIsolationControl>().SettleRequestAsync(parent.Owner));
            Assert.Equal(requests, model.Requests.Count);
            var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{session}/messages?limit=100"));
            Assert.Contains("Background started.", history.GetRawText()); Assert.Empty(model.Failures);
            if (quiet) Assert.DoesNotContain("Background work:", history.GetRawText());
        }
        finally { model.ReleaseChild.TrySetResult(); model.ReleaseParent.TrySetResult(); }
    }
}
