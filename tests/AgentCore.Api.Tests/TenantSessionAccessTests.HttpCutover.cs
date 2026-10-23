using AgentCore.Api.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Every_current_private_HTTP_route_refuses_original_tokens_and_every_tenant_route_refuses_foreign_humans_including_platform_operators(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-current-ingress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel())
                : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            using var anonymous = NewClient(); using var oldToken = NewClient();
            using var platformOperator = NewClient(); using var foreignHuman = NewClient();
            oldToken.DefaultRequestHeaders.Add("X-AgentCore-Owner-Capability", RetiredOwnerHeader.Token);
            await Login(platformOperator, "owner@example.test", "explicit-synthetic-test-password");
            var alpha = (await Json(await platformOperator.GetAsync("/api/v2/account/tenants")))
                .GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await platformOperator.GetAsync("/api/v2/system/accounts")).StatusCode);
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var account = await identity.RegisterAsync("ingress-foreign@example.test", "Foreign human", "synthetic-ingress-password");
            async Task<string> OperatorVersion()
            {
                var page = await Json(await platformOperator.GetAsync("/api/v2/system/operators?pageSize=100"));
                return page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("accountId").GetGuid() == account.AccountId)
                    .GetProperty("operatorVersion").GetString()!;
            }
            var operatorPath = $"/api/v2/system/operators/{account.AccountId}";
            Assert.Equal(HttpStatusCode.NoContent, (await platformOperator.PutAsJsonAsync(operatorPath,
                new { enabled = true, expectedVersion = await OperatorVersion() })).StatusCode);
            await Login(foreignHuman, account.LoginKey, "synthetic-ingress-password");
            var created = await foreignHuman.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Foreign organization", slug = "foreign-ingress" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var beta = (await Json(created)).GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await platformOperator.PutAsJsonAsync(operatorPath,
                new { enabled = false, expectedVersion = await OperatorVersion() })).StatusCode);
            await Login(foreignHuman, account.LoginKey, "synthetic-ingress-password");
            Assert.Equal(HttpStatusCode.Forbidden, (await foreignHuman.GetAsync("/api/v2/system/accounts")).StatusCode);
            var security = factory.Services.GetRequiredService<IIamStore>();
            var alphaBefore = (await security.ReadTenantAsync(alpha))!;
            var betaBefore = (await security.ReadTenantAsync(beta))!;
            var alphaAudit = (await security.ReadAuditAsync(alpha, 200)).Count;
            var betaAudit = (await security.ReadAuditAsync(beta, 200)).Count;
            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v2/", StringComparison.Ordinal) == true).ToArray();
            var privatePairs = 0; var tenantPairs = 0;
            foreach (var endpoint in endpoints)
            {
                var route = endpoint.RoutePattern.RawText!;
                var tenantRoute = route.StartsWith("/api/v2/tenants/", StringComparison.Ordinal);
                if (!tenantRoute && !route.StartsWith("/api/v2/account/", StringComparison.Ordinal)
                    && !route.StartsWith("/api/v2/system/", StringComparison.Ordinal)) continue;
                Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
                {
                    privatePairs++;
                    async Task Refused(HttpClient client, Guid tenant, HttpStatusCode status, string reason)
                    {
                        var path = Regex.Replace(route, @"\{([^}]+)\}", match =>
                            match.Groups[1].Value.StartsWith("tenantId:", StringComparison.Ordinal) ? tenant.ToString("D")
                            : match.Value.Contains(":int", StringComparison.Ordinal) ? "1" : Guid.NewGuid().ToString("D"));
                        using var request = new HttpRequestMessage(new HttpMethod(method), path);
                        if (method is not ("GET" or "HEAD")) request.Content = JsonContent.Create(new { });
                        using var response = await client.SendAsync(request);
                        Assert.True(response.StatusCode == status, $"{method} {route}: expected {(int)status}, observed {(int)response.StatusCode}");
                        Assert.Equal(reason, (await Json(response)).GetProperty("reasonCode").GetString());
                    }
                    await Refused(anonymous, alpha, HttpStatusCode.Unauthorized, "not_authenticated");
                    await Refused(oldToken, alpha, HttpStatusCode.Unauthorized, "not_authenticated");
                    if (!tenantRoute) continue;
                    tenantPairs++;
                    await Refused(foreignHuman, alpha, HttpStatusCode.Forbidden, "not_member");
                    await Refused(platformOperator, beta, HttpStatusCode.Forbidden, "not_member");
                }
            }
            Assert.Equal(241, privatePairs); Assert.Equal(227, tenantPairs);
            Assert.Equal(alphaBefore.Tenant.Revision, (await security.ReadTenantAsync(alpha))!.Tenant.Revision);
            Assert.Equal(betaBefore.Tenant.Revision, (await security.ReadTenantAsync(beta))!.Tenant.Revision);
            Assert.Equal(alphaAudit, (await security.ReadAuditAsync(alpha, 200)).Count);
            Assert.Equal(betaAudit, (await security.ReadAuditAsync(beta, 200)).Count);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
            var added = manifest.RootElement.GetProperty("addedCanonical").EnumerateArray()
                .Select(item => (method: item.GetProperty("method").GetString()!, route: item.GetProperty("route").GetString()!)).ToHashSet();
            Assert.Equal(18, added.Count); Assert.DoesNotContain(added, retained.Contains);
            var supported = retained.Union(added).ToHashSet();
            var mapped = actual.Where(item => item.route.StartsWith("/api/", StringComparison.Ordinal)).ToHashSet();
            Assert.True(supported.SetEquals(mapped), "Canonical route inventory differs. Missing: "
                + string.Join(", ", supported.Except(mapped).OrderBy(p => p.route).ThenBy(p => p.method))
                + ". Unexpected: " + string.Join(", ", mapped.Except(supported).OrderBy(p => p.route).ThenBy(p => p.method)));
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
