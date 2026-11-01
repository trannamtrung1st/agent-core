using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Api.Realtime;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Disable_cancels_active_generation_retains_history_and_reenable_never_replays_it(bool sqlite)
    {
        var model = new DisableGateModel();
        await using var factory = new RuntimeStateFactory(sqlite, model: model, nativeDefinitions: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var instance = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"))).GetProperty("items")[0].GetProperty("instanceId").GetGuid();
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Hold this active request until I disable the agent.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var output = factory.Services.GetRequiredService<TenantSessionLiveDelivery>().ObserveOutputSource(tenant,
            (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!.SessionId);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            while (output.Prefix != "Retained partial answer") await Task.Delay(10, deadline.Token);
        var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
        var metadata = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, active.SessionId))!;
        var auth = (await factory.Services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
        var principal = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("principalId").GetGuid();
        await factory.Services.GetRequiredService<TenantSessionRuntimeHost>().ReceiveAsync(new(metadata.Owner,
            new(auth.Account.AccountId, auth.Session.SessionId, principal), await Revision(owner, tenant),
            output.Started!.EntryId, output.Started.EntrySequence, active.ResponseId!.Value, output.Prefix.Length, output.Prefix.Length, []));
        var resource = $"/api/v2/tenants/{tenant}/agents/{instance}";
        var editor = await Json(await owner.GetAsync(resource + "/editor"));
        var disabled = await owner.PutAsJsonAsync(resource + "/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant),
            expectedInstanceRevision = editor.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await model.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        var stopped = (await runs.FindTenantRunAsync(tenant, submitted.Run))!;
        Assert.Equal(AgentRunStatus.Cancelled, stopped.Status); Assert.Null(stopped.Claim); Assert.True(stopped.CancellationRequested);
        var view = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{stopped.SessionId}/conversation"));
        Assert.False(view.GetProperty("agentEnabled").GetBoolean());
        var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{stopped.SessionId}/messages"));
        Assert.Contains("Hold this active request", history.ToString());
        Assert.Contains("Retained partial answer", history.ToString());
        Assert.Contains("agentDisabled", history.ToString());
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"))).GetProperty("total").GetInt32());
        var reenabled = await owner.PutAsJsonAsync(resource + "/lifecycle", new { lifecycle = 0, expectedRevision = await Revision(owner, tenant),
            expectedInstanceRevision = (await Json(disabled)).GetProperty("instance").GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, reenabled.StatusCode);
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        Assert.Equal(1, model.Calls); Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(tenant, stopped.AgentRunId))!.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Disable_fences_a_claim_loading_before_runtime_registration_even_after_reenable(bool sqlite)
    {
        var model = new DisableGateModel();
        await using var factory = new RuntimeStateFactory(sqlite, model: model, nativeDefinitions: true, manualDispatch: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var instance = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"))).GetProperty("items")[0].GetProperty("instanceId").GetGuid();
        var submitted = await SubmitProfileRequest(owner, tenant, instance);
        var host = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeDispatchRegistration = async _ => { entered.TrySetResult(); await release.Task; };
        var dispatch = factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var resource = $"/api/v2/tenants/{tenant}/agents/{instance}";
            var disabled = await owner.PutAsJsonAsync(resource + "/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            var enabled = await owner.PutAsJsonAsync(resource + "/lifecycle", new { lifecycle = 0, expectedRevision = await Revision(owner, tenant),
                expectedInstanceRevision = (await Json(disabled)).GetProperty("instance").GetProperty("revision").GetInt64() });
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        }
        finally { release.TrySetResult(); }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(15));
        var saved = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
        Assert.Equal(AgentRunStatus.Cancelled, saved.Status); Assert.Equal(0, model.Calls);
    }

    private sealed class DisableGateModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            yield return new ModelTextDelta("Retained partial answer");
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Stopped.TrySetResult(); }
            yield break;
        }
    }
}
