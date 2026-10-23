using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ExecutingTenantFactory : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => true;
    }

    [Fact]
    public async Task Canonical_tenant_worker_completes_actual_HTTP_text_without_a_global_profile()
    {
        await using var factory = new ExecutingTenantFactory();
        var application = typeof(AgentCore.Application.Sessions.SessionRuntimeFactory).Assembly;
        foreach (var retired in new[] { "Sessions.SessionManager", "Sessions.LocalUserProfileService", "Ports.ILocalUserProfileService", "Ports.IProfileLiveUpdateNotifier", "Events.ProfileUpdatedReceived" })
            Assert.Null(application.GetType("AgentCore.Application." + retired));
        Assert.Null(typeof(AgentCore.Application.Sessions.SessionRuntimeFactory).GetMethod("Create"));
        Assert.Null(typeof(AgentCore.Application.Sessions.SessionRuntime).GetMethod("ApplyProfileAsync"));
        Assert.DoesNotContain(typeof(AgentCore.Application.Sessions.SessionRuntimeFactory).GetConstructors().Single().GetParameters(),
            p => p.ParameterType == typeof(IMemoryStore) || p.ParameterType == typeof(IAgentRunStore)
                || p.ParameterType == typeof(IAttachmentStore) || p.ParameterType == typeof(IArtifactReferenceAuthorizer));
 using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var requester = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var executor = agent.GetProperty("principalId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid();
        var response = await owner.PostAsJsonAsync(api + $"/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Hello Synthetic examiner", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await Json(response); var runId = receipt.GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        AgentRun? run = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            run = await runs.FindTenantRunAsync(tenant, runId);
            if (run?.IsTerminal == true) break;
            await Task.Delay(50, timeout.Token);
        }
        Assert.NotNull(run); Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
        Assert.Null(run.Owner.ProfileIdOrNull); Assert.Equal(executor, run.Owner.ExecutorPrincipalId);
        Assert.Null(await factory.Services.GetRequiredService<IMemoryStore>().LoadProfileAsync(AgentCore.Domain.Conversation.LocalUserProfile.Id));
        Assert.Equal(requester, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.Equal(receipt.GetProperty("activationId").GetGuid(), run.Admission.ExecutionContext.RootActivationId);
        Assert.False(string.IsNullOrWhiteSpace(run.Result!.Text)); Assert.NotNull(run.Result.OutcomeEntryId);
        Assert.DoesNotContain(await factory.Services.GetRequiredService<IAgentRunStore>().ListRunnableAsync(DateTimeOffset.UtcNow, 100), r => r.AgentRunId == runId);
        using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v2/host/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Canonical_dispatch_rechecks_executor_authority_before_starting_a_model()
    {
        await using var factory = new AgentCoreApiFactory();
 using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid();
        var receipt = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Do not process after archive", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
        var runId = receipt.GetProperty("runId").GetGuid();
        var services = factory.Services;
        var agents = services.GetRequiredService<TenantAgentService>();
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var runs = services.GetRequiredService<ITenantRunStore>(); var run = (await runs.FindTenantRunAsync(tenant, runId))!;
        var storage = services.GetRequiredService<ITenantRuntimeStore>(); var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, runId, new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)), Guid.NewGuid());
        Assert.NotNull(await services.GetRequiredService<TenantRunExecutionAuthority>().CurrentDefinitionAsync(run));
        await agents.SetLifecycleAsync(human, tenant, instance, AgentCore.Domain.Definitions.AgentInstanceLifecycle.Archived, await Revision(owner, tenant), 1);
        Assert.Null(await services.GetRequiredService<TenantRunExecutionAuthority>().CurrentDefinitionAsync(run));
        Assert.True(await services.GetRequiredService<ITenantRunDispatcher>().DispatchAsync(run));
        var denied = (await runs.FindTenantRunAsync(tenant, runId))!;
        Assert.Equal(AgentRunStatus.Failed, denied.Status); Assert.Equal("authority-unavailable", denied.Failure!.Code); Assert.Null(denied.Result);
    }
}
