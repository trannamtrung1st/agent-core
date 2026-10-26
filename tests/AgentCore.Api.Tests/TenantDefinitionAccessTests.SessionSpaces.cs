using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Context;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    private sealed class ContextTurnModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public TaskCompletionSource<ModelRequest> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (Entered.TrySetResult(request)) await Release.Task.WaitAsync(cancellationToken);
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    private sealed class SpaceRuntimeFactory : AgentCoreApiFactory
    {
        public ContextTurnModel Model { get; } = new();
        protected override bool EnableTenantRunExecution => true;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<ILanguageModel>(Model)));
        }
    }
    [Fact]
    public async Task Context_HTTP_switches_one_Session_and_completes_a_Synthetic_turn_in_its_captured_Shared_Space()
    {
        await using var factory = new SpaceRuntimeFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        // The same Runs must preserve an owned inference route while switching Space context.
        var catalog = await Json(await owner.GetAsync(api + "/models"));
        var preset = catalog.GetProperty("presets").EnumerateArray().First(p => p.GetProperty("kind").GetString() == "OpenAI");
        var credential = await Json(await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("Context model key", "ApiKey", "ModelInference",
            await Revision(owner, tenant), ProtectedValue: "offline-context-model-placeholder")));
        var connection = await Json(await owner.PostAsJsonAsync(api + "/model-connections", new ModelConnectionRequest("OpenAI", "Context inference", null,
            credential.GetProperty("credential").GetProperty("credentialId").GetGuid(), false, true, await Revision(owner, tenant), 0)));
        var connectionId = connection.GetProperty("connectionId").GetGuid();
        var offering = await Json(await owner.PostAsJsonAsync(api + "/models", new ModelOfferingRequest(connectionId, preset.GetProperty("presetId").GetGuid(),
            preset.GetProperty("externalModelId").GetString()!, "Context model", "Responses",
            new ModelCapabilityInput(true, true, "NativeDeveloper", "NativeStructuredOutput", true, true, ["low", "medium", "high"], "medium"),
            true, false, await Revision(owner, tenant), 0)));
        var offeringId = offering.GetProperty("offeringId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/models/default", new TenantModelDefaultRequest(offeringId, "high", await Revision(owner, tenant), 0))).StatusCode);
        var createdAgent = await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, createdAgent.StatusCode); var agent = await Json(createdAgent);
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var principal = agent.GetProperty("principalId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Read bound conversation context", permissions = new[] { "spaces.read", "sessions.read", "agents.read" }, expectedRevision = await Revision(owner, tenant) }));
        var grant = await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }); Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        async Task<Guid> Session()
        {
            var result = await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
            Assert.Equal(HttpStatusCode.Created, result.StatusCode); return (await Json(result)).GetProperty("sessionId").GetGuid();
        }
        var first = await Session(); var second = await Session();
        var before = await Json(await owner.GetAsync($"{api}/sessions/{first}/context"));
        var privateId = before.GetProperty("selection").GetProperty("spaceId").GetGuid();
        var shared = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/spaces", new { spaceId = shared, name = "Shared runtime", description = "", instructions = "Use this review context while retaining your identity.", expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var denied = await owner.PostAsJsonAsync($"{api}/sessions/{first}/context/switch", new { transitionId = Guid.NewGuid(), spaceId = shared, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1, expectedSelectionRevision = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var agentAbsent = await Json(await owner.GetAsync($"{api}/agents/{instance}/context-options?spaceId={shared}"));
        Assert.False(agentAbsent.GetProperty("targetAvailable").GetBoolean());
        Assert.DoesNotContain(agentAbsent.GetProperty("spaces").EnumerateArray(), s => s.GetProperty("spaceId").GetGuid() == shared);
        var absent = await Json(await owner.GetAsync($"{api}/sessions/{first}/context/options"));
        Assert.DoesNotContain(absent.GetProperty("spaces").EnumerateArray(), s => s.GetProperty("spaceId").GetGuid() == shared);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{api}/spaces/{shared}/memberships/{instance}", new { responsibility = "Examiner", instructions = "Keep continuity", disabledInheritedBindingKeys = Array.Empty<string>(), addedBindings = Array.Empty<object>(), expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1, expectedMembershipRevision = 0 })).StatusCode);
        var choices = await Json(await owner.GetAsync($"{api}/sessions/{first}/context/options?spaceId={shared}"));
        Assert.Contains(choices.GetProperty("spaces").EnumerateArray(), s => s.GetProperty("spaceId").GetGuid() == shared);
        var agentChoices = await Json(await owner.GetAsync($"{api}/agents/{instance}/context-options?spaceId={shared}"));
        Assert.True(agentChoices.GetProperty("targetAvailable").GetBoolean());
        Assert.Contains(agentChoices.GetProperty("spaces").EnumerateArray(), s => s.GetProperty("spaceId").GetGuid() == shared);
        Assert.Empty(agentChoices.GetProperty("environments").EnumerateArray());
        Assert.False(choices.GetProperty("environmentsEnabled").GetBoolean()); Assert.Empty(choices.GetProperty("environments").EnumerateArray());
        var changed = await owner.PostAsJsonAsync($"{api}/sessions/{first}/context/switch", new { transitionId = Guid.NewGuid(), spaceId = shared, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1, expectedSelectionRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); var switched = await Json(changed); Assert.Equal("applied", switched.GetProperty("state").GetString());
        Assert.Equal(privateId, (await Json(await owner.GetAsync($"{api}/sessions/{second}/context"))).GetProperty("selection").GetProperty("spaceId").GetGuid());
        var submitted = await owner.PostAsJsonAsync($"{api}/sessions/{first}/messages", new { messageId = Guid.NewGuid(), text = "Hello", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = switched.GetProperty("sessionRevision").GetInt64() });
        Assert.True(submitted.StatusCode == HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync());
        var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
        var modelRequest = await factory.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Contains("Shared runtime", modelRequest.Context!.Text(ContextSectionId.Space));
            var active = await Json(await owner.GetAsync($"{api}/sessions/{first}/context"));
            var pending = await owner.PostAsJsonAsync($"{api}/sessions/{first}/context/switch", new {
                transitionId = Guid.NewGuid(), spaceId = privateId, expectedRevision = await Revision(owner, tenant),
                expectedSessionRevision = active.GetProperty("sessionRevision").GetInt64(),
                expectedSelectionRevision = active.GetProperty("selection").GetProperty("revision").GetInt64() });
            Assert.True(pending.IsSuccessStatusCode, await pending.Content.ReadAsStringAsync());
            var result = await Json(pending);
            Assert.Equal("pending_next_turn", result.GetProperty("state").GetString());
            Assert.Equal(runId, result.GetProperty("selection").GetProperty("effectiveAfterRunId").GetGuid());
            Assert.Equal(privateId, result.GetProperty("selection").GetProperty("spaceId").GetGuid());
        }
        finally { factory.Model.Release.TrySetResult(); }
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? run = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            run = await runs.FindTenantRunAsync(tenant, runId);
            if (run!.IsTerminal) break;
            await Task.Delay(20);
        }
        Assert.NotNull(run); Assert.True(run.Status == AgentRunStatus.Completed, $"Observed Run state: {run.Status}; failure: {run.Failure}");
        Assert.Equal(shared, run.Admission.Configuration!.SpaceContext!.SpaceId); Assert.Equal(privateId, run.Admission.Configuration.SpaceContext.PrivateSpaceId);
        Assert.Equal(offeringId.ToString("D"), run.PinnedModel.OfferingId);
        Assert.Equal(connectionId.ToString("D"), run.PinnedModel.ConnectionId);
        Assert.Equal("high", run.PinnedModel.ReasoningEffort);
        Assert.Equal(tenant, run.PinnedModel.Binding!.Route.TenantId);
        var runMetadata = await Json(await owner.GetAsync($"{api}/agent-runs/{runId}"));
        Assert.Equal(shared, runMetadata.GetProperty("spaceContext").GetProperty("spaceId").GetGuid());
        Assert.False(runMetadata.GetProperty("spaceContext").TryGetProperty("spaceInstructions", out _));
        var historyResponse = await owner.GetAsync($"{api}/sessions/{first}/messages");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await Json(historyResponse);
        Assert.Contains(history.GetProperty("items").EnumerateArray(), item => item.GetProperty("role").GetString() == "assistant" && item.GetProperty("text").GetString()!.Length > 0);
        var nextContext = await Json(await owner.GetAsync($"{api}/sessions/{first}/context"));
        Assert.Equal("applied", nextContext.GetProperty("state").GetString());
        var next = await owner.PostAsJsonAsync($"{api}/sessions/{first}/messages", new { messageId = Guid.NewGuid(), text = "Continue in private context",
            expectedRevision = await Revision(owner, tenant), expectedSessionRevision = nextContext.GetProperty("sessionRevision").GetInt64() });
        Assert.True(next.StatusCode == HttpStatusCode.Accepted, await next.Content.ReadAsStringAsync());
        var nextRunId = (await Json(next)).GetProperty("runId").GetGuid();
        deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        do
        {
            run = await runs.FindTenantRunAsync(tenant, nextRunId);
            if (run!.IsTerminal) break;
            await Task.Delay(20);
        } while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal(AgentRunStatus.Completed, run!.Status);
        Assert.Equal(privateId, run.Admission.Configuration!.SpaceContext!.SpaceId);
        Assert.Equal(offeringId.ToString("D"), run.PinnedModel.OfferingId);
        Assert.Equal(connectionId.ToString("D"), run.PinnedModel.ConnectionId);
        Assert.DoesNotContain(factory.Model.Requests.Last().Context!.Sections,
            s => s.Id == ContextSectionId.Space && s.Message.Text.Contains("Shared runtime", StringComparison.Ordinal));
    }
}
