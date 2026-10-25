using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(ModelProviderKind.OpenAI, false)] [InlineData(ModelProviderKind.Anthropic, false)] [InlineData(ModelProviderKind.Google, false)]
    [InlineData(ModelProviderKind.OpenRouter, false)] [InlineData(ModelProviderKind.OpenAICompatible, false)]
    [InlineData(ModelProviderKind.OpenAI, true)] [InlineData(ModelProviderKind.Anthropic, true)]
    public async Task DynamicModels_guarded_factory_uses_exact_connection_secret_and_never_falls_back_after_revocation(ModelProviderKind kind, bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, kind);
        using var http = new ProviderHttpFixture(FinalWire(setup.Route));
        var model = ProviderFactory(f, http).Create(setup.Route, setup.Offering.Capabilities, Invocation(setup.Route), ModelPurpose.Conversation);
        var result = await ProviderEvents(model, ProviderRequest());
        Assert.DoesNotContain(result, e => e is ModelFailed);
        Assert.Equal("Done ☃", Assert.Single(result.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
        Assert.Single(http.Requests);
        var sent = http.Requests[0]; Assert.StartsWith(setup.Connection.Endpoint, sent.Url);
        Assert.Contains(kind switch { ModelProviderKind.OpenAI => "/responses", ModelProviderKind.Anthropic => "/messages", ModelProviderKind.Google => ":streamGenerateContent", _ => "/chat/completions" }, sent.Url);
        Assert.DoesNotContain("factory-private-key", sent.Body.GetRawText()); Assert.Equal("factory-private-key", sent.Secret);
        Assert.True(sent.HasDispatchAuthority);
        if (kind != ModelProviderKind.Google) Assert.Equal(setup.Offering.ExternalModelId, sent.Body.GetProperty("model").GetString());
        var state = (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!;
        await f.Service.UpdateAsync(setup.Human.PrincipalId, setup.Route.TenantId, setup.Connection.CredentialId!.Value,
            new("Revoked inference", CredentialStatus.Disabled, null, []), state.Tenant.Revision, 1, initiator: setup.Human);
        var denied = await ProviderEvents(model, ProviderRequest());
        Assert.Equal(ProviderErrorCode.Authentication, Assert.Single(denied.OfType<ModelFailed>()).Failure.Code);
        Assert.DoesNotContain(denied, e => e is ModelSemanticResponseReady or ModelToolCallEvent or ModelCompleted);
        Assert.Single(http.Requests);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_route_edits_and_cross_tenant_invocations_refuse_dispatch_without_retargeting(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.OpenAI);
        using var http = new ProviderHttpFixture(FinalWire(setup.Route)); var factory = ProviderFactory(f, http);
        var invocation = Invocation(setup.Route);
        Assert.Throws<AccessDeniedException>(() => factory.Create(setup.Route, setup.Offering.Capabilities,
            Invocation(setup.Route with { TenantId = Guid.NewGuid() }), ModelPurpose.Conversation));
        var state = (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!;
        await Models(f).SaveConnectionAsync(setup.Human, setup.Route.TenantId, setup.Connection.ConnectionId,
            new(setup.Connection.Kind, "Changed connection revision", null, setup.Connection.CredentialId, false, true), state.Tenant.Revision, 1);
        var result = await ProviderEvents(factory.Create(setup.Route, setup.Offering.Capabilities, invocation, ModelPurpose.Conversation), ProviderRequest());
        Assert.Equal(ProviderErrorCode.InvalidRequest, Assert.Single(result.OfType<ModelFailed>()).Failure.Code); Assert.Empty(http.Requests);
        Assert.Throws<AccessDeniedException>(() => factory.Create(setup.Route with { ConnectionRevision = 2 }, setup.Offering.Capabilities, invocation, ModelPurpose.Conversation));
    }
    [Theory]
    [InlineData(ModelProviderKind.OpenAI)] [InlineData(ModelProviderKind.OpenRouter)] [InlineData(ModelProviderKind.OpenAICompatible)]
    public async Task DynamicModels_compatible_continuation_is_protected_and_bound_to_exact_connection(ModelProviderKind kind)
    {
        await using var f = await Fixture.Create(false); var setup = await ProviderSetup(f, kind);
        using var http = new ProviderHttpFixture(CompatibleToolWire(setup.Route)); var factory = ProviderFactory(f, http);
        var request = ProviderRequest() with { Tools = [new("fixture.echo", "Echo", """{"type":"object","properties":{"value":{"type":"string"}},"additionalProperties":false}""")] };
        var initial = await ProviderEvents(factory.Create(setup.Route, setup.Offering.Capabilities, Invocation(setup.Route), ModelPurpose.Conversation), request);
        var call = Assert.Single(initial.OfType<ModelToolCallEvent>()).Call;
        Assert.NotNull(call.ContinuationToken); Assert.DoesNotContain("vendor-private-opaque", call.ContinuationToken!);
        var state = (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!; var service = Models(f);
        var other = await service.SaveConnectionAsync(setup.Human, setup.Route.TenantId, null,
            new(kind, "Other connection", kind == ModelProviderKind.OpenAICompatible ? setup.Connection.Endpoint : null, setup.Connection.CredentialId, false, true), state.Tenant.Revision, 0);
        state = (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!;
        var offering = await service.SaveOfferingAsync(setup.Human, setup.Route.TenantId, null,
            new(other.ConnectionId, setup.Offering.PresetId, setup.Offering.ExternalModelId, "Same model, another connection", setup.Offering.Transport, setup.Offering.Capabilities, true, false), state.Tenant.Revision, 0);
        var route = setup.Route with { OfferingId = offering.OfferingId, ConnectionId = other.ConnectionId };
        var resumed = request with { Messages = request.Messages.Concat([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, "Observed result", ToolCallId: call.Id)]).ToArray() };
        var denied = await ProviderEvents(factory.Create(route, offering.Capabilities, Invocation(route), ModelPurpose.Conversation), resumed);
        Assert.Equal(ProviderErrorCode.InvalidRequest, Assert.Single(denied.OfType<ModelFailed>()).Failure.Code);
        Assert.Single(http.Requests);
    }
    private sealed record ProviderSetupState(ModelRoutePin Route, ProviderConnection Connection, ModelOffering Offering, HumanSessionReference Human);
    private static async Task<ProviderSetupState> ProviderSetup(Fixture f, ModelProviderKind kind)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId,
            new("Inference", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], "factory-private-key"), await Revision(), initiator: human);
        var service = Models(f); var connection = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(kind, "Exact connection", kind == ModelProviderKind.OpenAICompatible ? "https://local.example.test/v1/" : null, credential.Credential.CredentialId, false, true), await Revision(), 0);
        var preset = BuiltinModelPresets.All.FirstOrDefault(p => p.Kind == kind);
        var profile = preset?.Capabilities ?? new(true, false, ModelInstructionHierarchy.NativeDeveloper, ModelSemanticMode.NativeStructuredOutput, true, true, [], null, ModelMetadataSource.Manual);
        var offering = await service.SaveOfferingAsync(human, owner.TenantId, null, new(connection.ConnectionId, preset?.PresetId, preset?.ExternalModelId ?? "local-model", "Exact offering",
            preset?.Transport ?? ModelTransport.ChatCompletions, profile, true, false), await Revision(), 0);
        var source = (await ((ITenantModelStore)f.Iam).FindModelRouteAsync(owner.TenantId, offering.OfferingId))!;
        return new(new(owner.TenantId, offering.OfferingId, offering.Revision, connection.ConnectionId, connection.Revision,
            credential.Credential.CredentialId, source.Credential.Revision, source.Credential.SecretRevision, kind, offering.ExternalModelId, offering.Transport), connection, offering, human);
    }
    // This fixture exercises the provider boundary. Real Run admission/claim integration is verified separately.
    private static ModelInvocation Invocation(ModelRoutePin route)
    {
        var activation = Guid.NewGuid();
        return new(new(route.TenantId, Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.Initiative), route,
            ct => { ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; });
    }
    private static IModelProviderFactory ProviderFactory(Fixture f, ProviderHttpFixture http) => new TenantModelProviderFactory(http,
        new TenantModelRouteAuthority(f.Iam, (ITenantModelStore)f.Iam), f.Credentials, f.Protector, new(), f.Ids, f.Clock);
    private static ModelRequest ProviderRequest() => new(Guid.NewGuid(), [new(ModelRole.System, "Core instructions"), new(ModelRole.Developer, "Runtime instructions"), new(ModelRole.User, "Hello")], ResponseContract: new(false, true));
    private static async Task<List<ModelGenerationEvent>> ProviderEvents(ILanguageModel model, ModelRequest request)
    { var result = new List<ModelGenerationEvent>(); await foreach (var item in model.GenerateAsync(request)) result.Add(item); return result; }
    private static string FinalWire(ModelRoutePin route) => route.Kind is ModelProviderKind.Anthropic or ModelProviderKind.Google
        ? NativeLanguageModelTests.FinalRound(route.Kind, NativeLanguageModelTests.Final)
        : route.Transport == ModelTransport.Responses
            ? NativeLanguageModelTests.Data(new { type = "response.output_text.delta", delta = NativeLanguageModelTests.Final })
                + NativeLanguageModelTests.Data(new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 7, output_tokens = 9 } } })
            : NativeLanguageModelTests.Data(new { choices = new[] { new { delta = new { content = NativeLanguageModelTests.Final }, finish_reason = "stop" } } }) + "data: [DONE]\n\n";
    private static string CompatibleToolWire(ModelRoutePin route) => route.Transport == ModelTransport.Responses
        ? NativeLanguageModelTests.Data(new { type = "response.completed", response = new { status = "completed", output = new object[] {
            new { type = "reasoning", encrypted_content = "vendor-private-opaque" },
            new { type = "function_call", call_id = "call-1", name = "fixture_echo", arguments = "{\"value\":\"first\"}" } } } })
        : NativeLanguageModelTests.Data(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "call-1", type = "function", function = new { name = "fixture_echo", arguments = "{\"value\":\"first\"}" } } },
            reasoning_details = new[] { new { type = "reasoning.encrypted", data = "vendor-private-opaque" } } }, finish_reason = "tool_calls" } } }) + "data: [DONE]\n\n";
    private sealed class ProviderHttpFixture(params string[] bodies) : HttpMessageHandler, IHttpClientFactory
    {
        internal readonly List<(string Url, JsonElement Body, string Secret, bool HasDispatchAuthority)> Requests = [];
        internal Func<int, CancellationToken, Task>? BeforeResponse;
        public HttpClient CreateClient(string name) { Assert.Equal(ModelProviderHttp.ClientName, name); return new(this, disposeHandler: false); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var secret = request.Headers.Contains("x-api-key") ? request.Headers.GetValues("x-api-key").Single()
                : request.Headers.Contains("x-goog-api-key") ? request.Headers.GetValues("x-goog-api-key").Single() : request.Headers.Authorization!.Parameter!;
            var hasAuthority = request.Options.TryGetValue(ModelProviderHttp.DispatchAuthority, out var authority); if (authority is not null) await authority(ct);
            Requests.Add((request.RequestUri!.AbsoluteUri, json.RootElement.Clone(), secret, hasAuthority));
            if (BeforeResponse is not null) await BeforeResponse(Requests.Count, ct);
            Assert.True(Requests.Count <= bodies.Length, "Unexpected repeated provider POST.");
            return new(HttpStatusCode.OK) { Content = new StringContent(bodies[Requests.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }
}
