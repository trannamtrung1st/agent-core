using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Context;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class IndependentInputModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel human = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => human.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.Context?.Sections.Any(s => s.Id == ContextSectionId.RunTask && s.Source == ContextSourceKind.Automation) == true)
            {
                Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); yield break;
            }
            await foreach (var item in human.GenerateAsync(request, ct)) yield return item;
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false, "scheduled")] [InlineData(true, "scheduled")] [InlineData(false, "manual")] [InlineData(true, "manual")]
    public async Task Tenant_interruption_of_independent_Automation_preserves_actual_source_and_new_human_attribution(bool sqlite, string origin)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-independent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new IndependentInputModel(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(factory, owner, submit: false, reportReads: true, independentTask: true, fileCapabilities: [ToolCatalog.WorkspaceRead]);
            var api = $"/api/v2/tenants/{setup.Tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var seed = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!;
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var role = await access.SaveRoleAsync(human, setup.Tenant, null, "Actual independent invocation", ["automations.run"], await Revision(owner, setup.Tenant));
            await access.AssignRoleAsync(human, setup.Tenant, seed.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant));
            var sourceId = await TenantCredentialAccessTests.AdmitIndependentTask(factory, owner, setup.Tenant, seed.Owner.AgentInstanceId, "Independent objective awaiting explicit human input", clock, origin);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var source = (await runs.FindTenantRunAsync(setup.Tenant, sourceId))!;
            var child = (await sessions.FindTenantSessionAsync(setup.Tenant, source.SessionId))!; var url = api + $"/sessions/{source.SessionId}";
            Assert.True((await owner.PostAsJsonAsync(url + "/continue-in-chat", new { expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = child.Snapshot.Revision })).IsSuccessStatusCode);
            source = (await runs.FindTenantRunAsync(setup.Tenant, sourceId))!;
            var accepted = await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello explicit new human objective", behavior = "interrupt", interruptedRunId = sourceId, expectedRunRevision = source.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, source.SessionId))!.Snapshot.Revision });
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync() + $"; sourceUpdated={source.UpdatedAtUtc:o}; inputClock={clock.GetUtcNow():o}"); var newId = (await Json(accepted)).GetProperty("runId").GetGuid();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ((TenantSessionRuntimeHost)factory.Services.GetRequiredService<ITenantRunDispatcher>()).WaitForGenerationReleaseAsync(source.SessionId, sourceId, source.Claim!.Generation, timeout.Token);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, timeout.Token);
            var next = await AwaitBrowserRestartRun(runs, setup.Tenant, newId, false); Assert.Equal(AgentRunStatus.Completed, next.Status);
            Assert.Equal(human, next.Admission.ExecutionContext!.OriginalInitiatorPrincipalId); Assert.Equal(next.ActivationId, next.Admission.ExecutionContext.RootActivationId); Assert.Null(next.Admission.ExecutionContext.ParentRunId);
            var old = (await runs.FindTenantRunAsync(setup.Tenant, sourceId))!; Assert.Equal(AgentRunStatus.Cancelled, old.Status);
            Assert.Equal(origin == "scheduled" ? (Guid?)null : human, old.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            var saved = (await sessions.FindTenantSessionAsync(setup.Tenant, source.SessionId))!;
            Assert.Equal(child.Owner, saved.Owner); Assert.Equal(seed.Owner.ExecutorPrincipalId, saved.Owner.CreatedByPrincipalId); Assert.Equal(child.Snapshot.Origin, saved.Snapshot.Origin);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
