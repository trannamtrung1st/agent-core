using System.Net;
using System.Text;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(ModelProviderKind.OpenAI)] [InlineData(ModelProviderKind.Anthropic)] [InlineData(ModelProviderKind.Google)]
    [InlineData(ModelProviderKind.OpenRouter)] [InlineData(ModelProviderKind.OpenAICompatible)]
    public async Task DynamicModels_native_metadata_uses_exact_provider_auth_and_bounded_pagination(ModelProviderKind kind)
    {
        await using var f = await Fixture.Create(false);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId,
            new("Inference", CredentialKind.ApiKey, CredentialPurpose.ModelInference, null, [], "private-metadata-sentinel"), await Revision(), initiator: human);
        var endpoint = kind == ModelProviderKind.OpenAICompatible ? "https://models.example.test/v1/" : null;
        var handler = new MetadataHttpFixture(kind); using var http = new HttpClient(handler);
        var transport = new ModelMetadataTransport(http, f.Credentials, f.Protector, new());
        var service = Models(f, transport);
        var connection = await service.SaveConnectionAsync(human, owner.TenantId, null,
            new(kind, "Native metadata", endpoint, credential.Credential.CredentialId, false, true), await Revision(), 0);
        var tested = await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision);
        Assert.True(tested.MetadataAvailable); Assert.Equal(2, tested.ModelsObserved); Assert.Equal(2, handler.Requests.Count);
        var header = kind == ModelProviderKind.Anthropic ? "x-api-key" : kind == ModelProviderKind.Google ? "x-goog-api-key" : "Authorization";
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(kind is ModelProviderKind.Anthropic or ModelProviderKind.Google ? "private-metadata-sentinel" : "Bearer private-metadata-sentinel", request.Headers[header]);
            Assert.DoesNotContain("private-metadata-sentinel", request.Url);
            Assert.StartsWith(connection.Endpoint, request.Url);
        });
        Assert.Contains(kind == ModelProviderKind.Anthropic ? "after_id=" : kind == ModelProviderKind.Google ? "pageToken=" : "after=", handler.Requests[1].Url);
        var view = await service.ReadAsync(human, owner.TenantId);
        Assert.All(view.Offerings, o => Assert.False(o.Enabled)); Assert.Equal(2, view.Offerings.Count);
        Assert.DoesNotContain("private-metadata-sentinel", System.Text.Json.JsonSerializer.Serialize(view));
        handler.FailSecondPage = true; handler.Requests.Clear();
        var failed = await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision);
        Assert.False(failed.MetadataAvailable); Assert.Equal("authentication_failed", failed.Result);
        Assert.Equal(2, (await service.ReadAsync(human, owner.TenantId)).Offerings.Count);
        handler.FailSecondPage = false; handler.Requests.Clear();
        handler.PayloadOverride = kind == ModelProviderKind.Google ? "{\"models\":[{\"name\":\"models/\\u0070rivate-metadata-sentinel\"}]}"
            : "{\"data\":[{\"id\":\"\\u0070rivate-metadata-sentinel\"}]}";
        var reflectedSecret = await service.TestAsync(human, owner.TenantId, connection.ConnectionId, true, await Revision(), connection.Revision);
        Assert.False(reflectedSecret.MetadataAvailable);
        Assert.DoesNotContain("private-metadata-sentinel", System.Text.Json.JsonSerializer.Serialize(await service.ReadAsync(human, owner.TenantId)));
    }
    [Fact]
    public async Task DynamicModels_metadata_redirects_are_not_followed_and_provider_bodies_are_not_projected()
    {
        using var http = new HttpClient(new RedirectMetadataFixture());
        var endpointPolicy = new ModelEndpointPolicy(["http://localhost:8080"]);
        await using var f = await Fixture.Create(false);
        var connection = new ProviderConnection(Guid.NewGuid(), Guid.NewGuid(), ModelProviderKind.OpenAICompatible, "Local", "http://localhost:8080/v1/", null, true, true, 1, f.Clock.GetUtcNow(), f.Clock.GetUtcNow());
        var transport = new ModelMetadataTransport(http, f.Credentials, f.Protector, endpointPolicy); var checks = 0;
        var result = await transport.ReadAsync(connection, new(null, null, null), true, _ => { checks++; return ValueTask.CompletedTask; });
        Assert.False(result.Success); Assert.Empty(result.Models); Assert.Equal("metadata_unavailable", result.Result); Assert.True(checks >= 2);
        using var guarded = (DelegatingHandler)ModelProviderHttp.CreateHandler(endpointPolicy);
        var sockets = Assert.IsType<SocketsHttpHandler>(guarded.InnerHandler);
        Assert.False(sockets.AllowAutoRedirect); Assert.False(sockets.UseProxy); Assert.False(sockets.UseCookies);
    }
    private sealed class MetadataHttpFixture(ModelProviderKind kind) : HttpMessageHandler
    {
        internal readonly List<(string Url, Dictionary<string, string> Headers)> Requests = [];
        internal bool FailSecondPage;
        internal string? PayloadOverride;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (PayloadOverride is { } payload) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
            if (Requests.Count == 2 && FailSecondPage) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-metadata-sentinel provider body") });
            var second = Requests.Count == 2;
            var json = kind == ModelProviderKind.Google
                ? second ? "{\"models\":[{\"name\":\"models/second-model\"}]}" : "{\"models\":[{\"name\":\"models/first-model\"}],\"nextPageToken\":\"next\"}"
                : second ? "{\"data\":[{\"id\":\"second-model\"}],\"has_more\":false}" : "{\"data\":[{\"id\":\"first-model\"}],\"has_more\":true,\"last_id\":\"first-model\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class RedirectMetadataFixture : HttpMessageHandler
    {
        private int count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(1, ++count); var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("http://169.254.169.254/secret"); return Task.FromResult(response);
        }
    }
}
