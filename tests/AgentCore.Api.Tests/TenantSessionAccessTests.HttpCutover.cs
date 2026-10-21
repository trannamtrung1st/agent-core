using AgentCore.Api.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_HTTP_cutover_removes_all_228_retired_routes_even_for_original_tokens_and_platform_operators(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-http-cutover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel())
                : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            using var anonymous = NewClient(); using var oldToken = NewClient(); using var human = NewClient();
            oldToken.DefaultRequestHeaders.Add("X-AgentCore-Owner-Capability", RetiredOwnerHeader.Token);
            await Login(human, "owner@example.test", "explicit-synthetic-test-password");

            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
            var actual = endpoints.SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (method, route: e.RoutePattern.RawText!))).ToHashSet();
            using var manifest = JsonDocument.Parse(typeof(TenantSessionAccessTests).Assembly.GetManifestResourceStream(
                "AgentCore.Api.Tests.Fixtures.retired-http-routes.json")!);
            var retired = manifest.RootElement.GetProperty("retired").EnumerateArray().ToArray();
            Assert.Equal(228, retired.Length);
            var retained = manifest.RootElement.GetProperty("retained").EnumerateArray()
                .Select(item => (method: item.GetProperty("method").GetString()!, route: item.GetProperty("route").GetString()!)).ToHashSet();
            Assert.Equal(232, retained.Count);
            Assert.True(retained.SetEquals(actual.Where(item => item.route.StartsWith("/api/", StringComparison.Ordinal))),
                "All canonical method/path pairs must survive removal of the obsolete HTTP surface.");
            var payload = Guid.NewGuid().ToString("D");
            foreach (var item in retired)
            {
                var method = item.GetProperty("method").GetString()!; var route = item.GetProperty("route").GetString()!;
                Assert.DoesNotContain((method, route), actual);
                var path = Regex.Replace(route, @"\{([^}]+)\}", match => match.Value.Contains(":int", StringComparison.Ordinal) ? "1" : payload);
                foreach (var client in new[] { anonymous, oldToken, human })
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), path);
                    if (method is not ("GET" or "HEAD")) request.Content = JsonContent.Create(new { });
                    using var response = await client.SendAsync(request);
                    Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {route} returned {(int)response.StatusCode}");
                    Assert.DoesNotContain("text/html", response.Content.Headers.ContentType?.MediaType ?? "");
                }
            }
            // Allow only explicit canonical API families. A future private API cannot silently
            // reintroduce an unscoped root beside the current identity/tenant boundaries.
            foreach (var endpoint in endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true))
                Assert.True(IsCanonicalHttpRoute(endpoint.RoutePattern.RawText!), endpoint.RoutePattern.RawText);
            foreach (var family in new[] { "agents", "sessions", "credentials", "agent-definitions", "automations", "operation-policies", "audit" })
            {
                using var refused = await oldToken.GetAsync($"/api/v2/tenants/{Guid.NewGuid()}/{family}");
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static bool IsCanonicalHttpRoute(string route) => route.StartsWith("/api/v2/auth/", StringComparison.Ordinal)
        || route.StartsWith("/api/v2/account/", StringComparison.Ordinal) || route.StartsWith("/api/v2/system/", StringComparison.Ordinal)
        || route.StartsWith("/api/v2/tenants/{tenantId:guid}/", StringComparison.Ordinal)
        || route == "/api/v2/hooks/{tenantId:guid}/{sourceId:guid}"
        || route.StartsWith("/api/v2/integrations/tenant-google/oauth/", StringComparison.Ordinal);
}
