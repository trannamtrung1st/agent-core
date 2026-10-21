using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory]
    [InlineData(false, "currentSession", false)]
    [InlineData(true, "currentSession", false)]
    [InlineData(false, "instanceSummary", false)]
    [InlineData(true, "instanceSummary", false)]
    [InlineData(false, "currentSession", true)]
    [InlineData(true, "currentSession", true)]
    [InlineData(false, "currentSession", false, true)]
    [InlineData(true, "currentSession", false, true)]
    public async Task Tenant_runtime_state_native_tool_and_trusted_facts_keep_current_claim_scope_policy_and_rate_limit(bool sqlite, string scope, bool review, bool narrowAdmission = false)
    {
        var model = new RuntimeObservationModel { Arguments = JsonSerializer.Serialize(new { scope }) }; var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var (tenant, id, isolation) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.GetAsync(api + $"/agents/{id}"));
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Runtime observation executor", permissions = new[] { "sessions.read", "agent-runs.read", "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = agent.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.read", new { mode = "always", independentReview = false,
            expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        var instanceRevision = 1;
        if (narrowAdmission)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{id}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                set = new { agentStateVisibility = "CurrentSession" }, clear = Array.Empty<string>() })).StatusCode);
            instanceRevision++;
        }
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = id, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
        var admitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Observe this Run without allocating native resources", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
        var runId = admitted.GetProperty("runId").GetGuid();
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Read.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var requests = model.Requests.ToArray();
        var result = requests.Last().Messages.Last(message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.AgentStateInspect);
        using var json = JsonDocument.Parse(result.Text); var observed = json.RootElement;
        Assert.Equal(scope, observed.GetProperty("scope").GetString());
        Assert.Equal(1, observed.GetProperty("sessions").GetProperty("open").GetInt32());
        if (review) Assert.Equal(JsonValueKind.Null, observed.GetProperty("runs").ValueKind);
        else Assert.Equal(1, observed.GetProperty("runs").GetProperty("executing").GetInt32());
        Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
        Assert.Equal(1, observed.GetProperty("hostedSessions").GetProperty("runtimes").GetInt32());
        Assert.All(observed.GetProperty("resources").EnumerateArray(), resource => Assert.Equal("restricted", resource.GetProperty("availability").GetString()));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Text) <= 4096); Assert.Equal(0, factory.Browser.Calls);
        Assert.Contains(requests.First().Context!.Sections, s => s.Message.Text.Contains("Trusted runtime observation:", StringComparison.Ordinal));
        var saved = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
        var frame = new TenantRuntimeFrame(saved.Owner, session, runId, saved.Claim!.Generation);
        var service = factory.Services.GetRequiredService<TenantRuntimeStateService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.InspectForRunAsync(frame, new("forged", ToolCatalog.AgentStateInspect,
            "{\"agentInstanceId\":\"" + Guid.NewGuid().ToString("D") + "\"}"), 4096).AsTask());
        var limited = await Assert.ThrowsAsync<AgentCoreException>(() => service.ObserveForRunAsync(frame, scope).AsTask()); Assert.Equal("state_refresh_limited", limited.Code);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ObserveForRunAsync(frame with { Generation = Guid.NewGuid() }, scope).AsTask());
        clock.Advance(TimeSpan.FromSeconds(1)); var fresh = await service.ObserveForRunAsync(frame, scope); Assert.Equal(scope, fresh.Scope);
        if (narrowAdmission)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{id}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                set = new { agentStateVisibility = "InstanceSummary" }, clear = Array.Empty<string>() })).StatusCode);
            instanceRevision++;
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.ObserveForRunAsync(frame, "instanceSummary", false).AsTask());
            Assert.Equal("currentSession", (await service.ObserveForRunAsync(frame, "currentSession", false)).Scope);
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{id}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
            set = new { agentStateVisibility = "Off" }, clear = Array.Empty<string>() })).StatusCode);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ObserveForRunAsync(frame, scope, false).AsTask());
        model.Release.TrySetResult();
        await factory.Services.GetRequiredService<TenantSessionRuntimeHost>().WaitForGenerationReleaseAsync(session, runId, frame.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True((await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!.IsTerminal);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ObserveForRunAsync(frame, scope, false).AsTask());
        Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
    }

    private sealed class RuntimeObservationModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(["Runtime observation complete."]);
        public string Arguments = "{}";
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AgentStateInspect))
            {
                Assert.Contains(request.Tools!, tool => tool.Name == ToolCatalog.AgentStateInspect);
                yield return new ModelToolCallEvent(new("observe-runtime", ToolCatalog.AgentStateInspect, Arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Read.TrySetResult(); await Release.Task.WaitAsync(ct);
            await foreach (var value in inner.GenerateAsync(request, ct)) yield return value;
        }
    }
    private sealed class RuntimeDefinitions(IBuiltInAgentDefinitionStore inner) : IBuiltInAgentDefinitionStore
    {
        private static AgentDefinition View(AgentDefinition definition) => definition with {
            StateIsolationPolicy = new(AgentStateVisibility: AgentStateVisibility.InstanceSummary, MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary),
            Environment = new(Capabilities: new("Selected", ["chat.respond", ToolCatalog.AgentStateInspect]), Projection: new([ToolCatalog.AgentStateInspect])) };
        public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => (await inner.ListAsync(ct)).Select(View).ToArray();
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) => await inner.GetAsync(id, version, ct) is { } value ? View(value) : null;
    }
}
