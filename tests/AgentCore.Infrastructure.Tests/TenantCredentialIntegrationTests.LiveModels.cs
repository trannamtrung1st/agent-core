using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Models;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [LiveTenantModelFact(ModelProviderKind.OpenAI)]
    public Task LiveModels_OpenAI_tenant_tool_and_final() => LiveTenantModel(ModelProviderKind.OpenAI);
    [LiveTenantModelFact(ModelProviderKind.Anthropic)]
    public Task LiveModels_Anthropic_tenant_auto_tool_and_structured_final() => LiveTenantModel(ModelProviderKind.Anthropic);
    [LiveTenantModelFact(ModelProviderKind.Google)]
    public Task LiveModels_Google_tenant_tool_and_final() => LiveTenantModel(ModelProviderKind.Google);
    [LiveTenantModelFact(ModelProviderKind.OpenRouter)]
    public Task LiveModels_OpenRouter_tenant_tool_and_final() => LiveTenantModel(ModelProviderKind.OpenRouter);
    [LiveTenantModelFact(ModelProviderKind.OpenAICompatible)]
    public Task LiveModels_compatible_tenant_tool_and_final() => LiveTenantModel(ModelProviderKind.OpenAICompatible);

    private static async Task LiveTenantModel(ModelProviderKind kind)
    {
        // Separate test-only opt-in and per-kind inputs; ambient application/speech keys never opt in.
        Assert.True(LiveTenantModelFactAttribute.Enabled);
        var prefix = LiveTenantModelFactAttribute.Prefix(kind);
        var externalId = Environment.GetEnvironmentVariable(prefix + "_ID")!;
        var key = Environment.GetEnvironmentVariable(prefix + "_KEY")!;
        var compatible = kind == ModelProviderKind.OpenAICompatible;
        var endpoint = compatible ? Environment.GetEnvironmentVariable(prefix + "_ENDPOINT")! : null;
        var allowedOrigin = compatible ? Environment.GetEnvironmentVariable(prefix + "_LOCAL_ORIGIN") : null;
        var endpoints = new ModelEndpointPolicy(allowedOrigin is null ? [] : [allowedOrigin]);
        var preset = BuiltinModelPresets.All.SingleOrDefault(p => p.Kind == kind && p.ExternalModelId == externalId);
        ModelCapabilityProfile capabilities;
        ModelTransport transport;
        if (compatible)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            options.Converters.Add(new JsonStringEnumConverter());
            var profile = JsonSerializer.Deserialize<LiveCompatibleProfile>(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable(prefix + "_PROFILE")!), options)
                ?? throw new InvalidOperationException("A compatible transport/capability profile is required.");
            capabilities = profile.Capabilities with { Source = ModelMetadataSource.Manual };
            transport = profile.Transport;
        }
        else
        {
            Assert.NotNull(preset); // Select an existing verified native preset, never guess protocol support.
            capabilities = preset.Capabilities; transport = preset.Transport;
        }
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Live model fixture", "live-model-fixture");
        var original = await SandboxHuman(f, owner.OwnerPrincipalId);
        var human = new TenantSessionInitiator(original.AccountId, original.AuthenticationSessionId, original.PrincipalId);
        var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, tenant,
            new("Explicit live inference fixture", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], key), await Revision(), initiator: human);
        var models = new TenantModelManagementService(f.Iam, (ITenantModelStore)f.Iam, f.Credentials, f.Identity,
            f.Authorizer, endpoints, f.Ids, f.Clock);
        var connection = await models.SaveConnectionAsync(human, tenant, null,
            new(kind, "Explicit live connection", endpoint, credential.Credential.CredentialId, false, true), await Revision(), 0);
        var offering = await models.SaveOfferingAsync(human, tenant, null,
            new(connection.ConnectionId, preset?.PresetId, externalId, "Explicit live offering", transport, capabilities, true, false), await Revision(), 0);
        await models.SaveDefaultAsync(human, tenant, offering.OfferingId, null, await Revision(), 0);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        using var clients = new LiveModelHttpClients(endpoints);
        var probe = new GlobalModelProbe();
        await using var services = RuntimeServices(f, clients, probe, modelEndpoints: endpoints);
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId,
            human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        const string marker = "LIVE_MODEL_VERIFIED";
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human,
            f.Ids.NewId(), $"Call iam_check_access exactly once for permission agents.read, resourceType agent, resourceId {agent.Instance.InstanceId:D}. Then reply with {marker}. Do not call any other tool.", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
        var admitted = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        var run = await storage.ApplyTenantRuntimeCommandAsync(admitted.Owner, admitted.AgentRunId,
            new AgentRunCommand.Claim(admitted.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var output = new CapturingSessionOutput();
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!,
            output, frame, storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>());
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token);
        await runtime.WaitUntilIdleAsync(deadline.Token);
        var completed = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Contains(marker, completed.Result!.Text, StringComparison.Ordinal);
        Assert.Equal(admitted.PinnedModel.Binding, completed.PinnedModel.Binding);
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 100), a => a.Execution?.Context.RunId == run.AgentRunId && a.Execution.Permission == "agents.read");
        Assert.InRange(clients.Requests, 2, 4);
        Assert.Equal(0, probe.Resolutions);
    }

    private sealed record LiveCompatibleProfile(ModelTransport Transport, ModelCapabilityProfile Capabilities);
    private sealed class LiveModelHttpClients(ModelEndpointPolicy endpoints) : IHttpClientFactory, IDisposable
    {
        private readonly LiveRequestBudget handler = new() { InnerHandler = ModelProviderHttp.CreateHandler(endpoints) };
        public int Requests => handler.Requests;
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ModelProviderHttp.ClientName, name);
            return new(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
        }
        public void Dispose() => handler.Dispose();
        private sealed class LiveRequestBudget : DelegatingHandler
        {
            private int requests;
            public int Requests => Volatile.Read(ref requests);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method != HttpMethod.Post || Interlocked.Increment(ref requests) > 4)
                    throw new HttpRequestException("The explicit live fixture request budget was exhausted.");
                return base.SendAsync(request, ct);
            }
        }
    }
}

public sealed class LiveTenantModelFactAttribute : FactAttribute
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("AGENTCORE_LIVE_MODEL_TESTS") == "1";
    internal static string Prefix(ModelProviderKind kind) => "AGENTCORE_LIVE_MODEL_" + (kind == ModelProviderKind.OpenAICompatible ? "COMPATIBLE" : kind.ToString().ToUpperInvariant());
    public LiveTenantModelFactAttribute(ModelProviderKind kind)
    {
        if (!Enabled) { Skip = "Explicit AGENTCORE_LIVE_MODEL_TESTS=1 is required; default tests make no inference calls."; return; }
        var prefix = Prefix(kind);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prefix + "_KEY")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prefix + "_ID")))
            Skip = "This provider's explicit test-only model ID and key are missing.";
        else if (kind == ModelProviderKind.OpenAICompatible && (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prefix + "_ENDPOINT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prefix + "_PROFILE"))))
            Skip = "The compatible test-only endpoint and declared capability profile are missing.";
    }
}
