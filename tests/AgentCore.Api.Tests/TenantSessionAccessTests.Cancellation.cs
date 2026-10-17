using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class CancellationModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LateAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InspectionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InspectionRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref Calls);
            if (call == 1)
            {
                Entered.TrySetResult(); await Release.Task;
                LateAttempted.TrySetResult();
                yield return new ModelTextDelta("LATE AFTER TENANT CANCELLATION");
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (call == 3) { InspectionEntered.TrySetResult(); await InspectionRelease.Task.WaitAsync(cancellationToken); }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    private sealed class CancellationFactory(string root, ILanguageModel model) : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => true;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.Replace(ServiceDescriptor.Singleton(model));
                var options = (PersistenceOptions)services.Last(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.WorkspaceRoot = Path.Combine(root, "workspaces"); options.ArtifactRoot = Path.Combine(root, "artifacts");
                options.AttachmentRoot = Path.Combine(root, "attachments"); options.DefinitionResourceRoot = Path.Combine(root, "resources");
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys");
            });
        }
    }
    private static async Task<AgentRun> AwaitCancelledTenantRun(ITenantRunStore runs, Guid tenant, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var run = (await runs.FindTenantRunAsync(tenant, id, timeout.Token))!;
            if (run.IsTerminal) return run;
            await Task.Delay(25, timeout.Token);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Active_tenant_HTTP_cancellation_closes_saved_response_and_releases_capacity_before_a_noncooperative_model_returns(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new CancellationModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new CancellationFactory(root, model);
            try
            {
                using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentCore.Api.Realtime.AgentCoreOptions>>().Value.MaxActiveSessions = 1;
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
                    .GetProperty("instance").GetProperty("instanceId").GetGuid();
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var id = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hold this Synthetic turn", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })))
                    .GetProperty("runId").GetGuid();
                await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var storage = factory.Services.GetRequiredService<ITenantRuntimeStore>();
                var running = (await runs.FindTenantRunAsync(tenant, id))!; Assert.Equal(AgentRunStatus.Running, running.Status);
                var frame = new TenantRuntimeFrame(running.Owner, session, id, running.Claim!.Generation);
                var savedBefore = (await storage.LoadTenantRuntimeAsync(frame))!;
                var revision = await Revision(owner, tenant); var route = api + $"/agent-runs/{id}/cancel";
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(route, new { expectedRevision = revision - 1, expectedRunRevision = running.Revision })).StatusCode);
                Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(tenant, id))!.Status);
                var response = await owner.PostAsJsonAsync(route, new { expectedRevision = revision, expectedRunRevision = running.Revision });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("cancelled", (await Json(response)).GetProperty("status").GetString());
                var cancelled = (await runs.FindTenantRunAsync(tenant, id))!;
                Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status); Assert.True(cancelled.CancellationRequested); Assert.Null(cancelled.Claim);
                Assert.Equal(revision + 1, await Revision(owner, tenant));
                Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100), a => a.Action == "agent-runs.cancel");
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(route, new { expectedRevision = revision, expectedRunRevision = running.Revision })).StatusCode);
                await Assert.ThrowsAsync<AccessDeniedException>(() => storage.SaveTenantRuntimeAsync(frame, savedBefore, savedBefore.Revision).AsTask());
                var capacity = factory.Services.GetRequiredService<AgentCore.Api.Realtime.SessionHost>(); var released = false;
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    while (!released)
                    {
                        released = capacity.TryReserveTenantRuntime();
                        if (released) capacity.ReleaseTenantRuntime(); else await Task.Delay(25, deadline.Token);
                    }
                Assert.False(model.Release.Task.IsCompleted); Assert.Equal(1, model.Calls);
                var second = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var next = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{second}/messages", new { messageId = Guid.NewGuid(), text = "A separate Synthetic turn", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, next)).Status);
                model.Release.TrySetResult(); await model.LateAttempted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var metadata = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
                var inspect = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect cancellation", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = metadata.Revision }))).GetProperty("runId").GetGuid();
                // A fresh current frame may inspect retained bytes; no delivery acknowledgement is fabricated.
                await model.InspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var inspection = (await runs.FindTenantRunAsync(tenant, inspect))!;
                var retained = (await storage.LoadTenantRuntimeAsync(new(inspection.Owner, session, inspect, inspection.Claim!.Generation)))!;
                var entry = Assert.Single(retained.Entries, e => e.ResponseId == cancelled.ResponseId && e.Role == ConversationRole.Assistant);
                Assert.Equal(EntryStatus.Interrupted, entry.Status); Assert.Equal("userStop", entry.InterruptReason); Assert.NotNull(entry.CompletedAt);
                Assert.DoesNotContain("LATE AFTER", entry.Text); Assert.Equal(0, entry.ReceivedTextEndExclusive);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{inspect}/cancel", new {
                    expectedRevision = await Revision(owner, tenant), expectedRunRevision = inspection.Revision })).StatusCode);
            }
            finally { model.Release.TrySetResult(); model.InspectionRelease.TrySetResult(); }
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
