using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed record ConnectedReadSetup(Guid Tenant, Guid Instance, Guid Credential, Guid Binding, string Alias, string Root);
    private static HttpClient ConnectedClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    private static async Task<ConnectedReadSetup> ConnectedReadAccount(WebApplicationFactory<Program> factory, HttpClient owner, string purpose, bool tools = false)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var created = await Json(await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("Exact connected account", "Token", purpose,
            await Revision(owner, tenant), EmailAccount: purpose == "EmailAccount" ? new("Gmail", "") : null)));
        var credential = created.GetProperty("credential").GetProperty("credentialId").GetGuid();
        var start = await owner.PostAsJsonAsync(api + $"/credentials/{credential}/oauth/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        var url = (await Json(start)).GetProperty("authorizationUrl").GetString()!;
        using var consent = ConnectedClient(factory); consent.DefaultRequestHeaders.Add("Cookie", Assert.Single(start.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.SeeOther, (await consent.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["state"] = Uri.UnescapeDataString(url.Split("?state=")[1]), ["decision"] = "authorize" }))).StatusCode);
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = tools ? "general-assistant" : "examiner", version = tools ? 36 : 1, expectedRevision = await Revision(owner, tenant) })))
            .GetProperty("instance").GetProperty("instanceId").GetGuid();
        if (tools) await TenantSessionAccessTests.GrantNativePrivateSpaceRead(owner, tenant, instance);
        const string alias = "exact-account";
        var binding = (await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/credential-bindings", new TenantBindCredentialRequest(credential, alias, await Revision(owner, tenant), 1, 2))))
            .GetProperty("binding").GetProperty("bindingId").GetGuid();
        return new(tenant, instance, credential, binding, alias, api + $"/agents/{instance}/connected-accounts");
    }

    [Theory] [InlineData(false, "EmailAccount")] [InlineData(true, "EmailAccount")]
    [InlineData(false, "GoogleDriveAccount")] [InlineData(true, "GoogleDriveAccount")]
    public async Task Tenant_connected_account_HTTP_reads_exact_bound_mail_or_Drive_original_without_legacy_owner_or_Agent_grants(bool sqlite, string purpose)
    {
        await using var factory = new OAuthFactory(sqlite: sqlite); using var owner = ConnectedClient(factory);
        var application = typeof(TenantConnectedAccountService).Assembly;
        foreach (var retired in new[] { "Email.EmailAccountService", "Email.EmailMonitoringService", "Events.CoreEventDispatcher", "Ports.IEmailProviderFactory", "Ports.IEmailOAuthService", "Ports.IEmailChangeFeed", "Ports.IGoogleDriveProviderFactory", "Ports.IGoogleDriveOAuthService" })
            Assert.Null(application.GetType("AgentCore.Application." + retired));
        var infrastructure = typeof(AgentCore.Infrastructure.Credentials.TenantConnectedAccountReader).Assembly;
        foreach (var retired in new[] { "AccountEmailProviderFactory", "AccountEmailChangeFeed", "GoogleEmailOAuthService" })
            Assert.Null(infrastructure.GetType("AgentCore.Infrastructure.Email." + retired));
        Assert.Null(infrastructure.GetType("AgentCore.Infrastructure.Drive.GoogleDriveOAuthService"));
        Assert.Null(infrastructure.GetType("AgentCore.Infrastructure.Drive.GoogleDriveProviderFactory"));
        Assert.Null(typeof(AgentCore.Application.Drive.DriveService).GetMethod("ResolveAsync"));
        Assert.Null(typeof(AgentCore.Application.Drive.DriveService).GetMethod("AccountsAsync"));
        var s = await ConnectedReadAccount(factory, owner, purpose);
        var inventory = await owner.GetAsync(s.Root); Assert.Equal(HttpStatusCode.OK, inventory.StatusCode); Assert.True(inventory.Headers.CacheControl?.NoStore);
        var page = await Json(inventory); Assert.Equal(1, page.GetProperty("total").GetInt32()); Assert.True(page.GetProperty("items")[0].GetProperty("readAvailable").GetBoolean()); Safe(page);
        var baseRead = s.Root + "/" + s.Alias;
        if (purpose == "EmailAccount")
        {
            var searched = await Json(await owner.GetAsync(baseRead + "/email/messages?query=harness&limit=1"));
            Assert.True(searched.GetProperty("untrusted").GetBoolean()); var message = searched.GetProperty("mailbox").GetProperty("results")[0].GetProperty("messageId").GetString()!;
            Assert.StartsWith("syn-" + s.Credential.ToString("N") + ":", message);
            var read = await Json(await owner.GetAsync(baseRead + "/email/message?messageId=" + Uri.EscapeDataString(message))); Safe(read);
            Assert.True(read.GetProperty("untrusted").GetBoolean()); Assert.Equal(message, read.GetProperty("message").GetProperty("messageId").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(baseRead + "/email/message?messageId=syn-other-account:msg")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(baseRead + "/drive/files")).StatusCode);
        }
        else
        {
            var bytes = Enumerable.Range(0, 65536).Select(i => (byte)(i % 256)).ToArray();
            await factory.Services.GetRequiredService<ISyntheticDriveStore>().SaveAsync(s.Credential,
                new("exact-file", "folder/original.bin", "application/octet-stream", bytes.Length, "1", Convert.ToHexStringLower(MD5.HashData(bytes)), ["root"], false), bytes, null, default);
            var listing = await Json(await owner.GetAsync(baseRead + "/drive/files?limit=1")); Assert.True(listing.GetProperty("untrusted").GetBoolean()); Assert.Single(listing.GetProperty("page").GetProperty("items").EnumerateArray());
            var file = await Json(await owner.GetAsync(baseRead + "/drive/files/exact-file")); Assert.Equal("1", file.GetProperty("file").GetProperty("version").GetString());
            using var binary = await owner.GetAsync(baseRead + "/drive/files/exact-file/content?expectedVersion=1");
            Assert.Equal(HttpStatusCode.OK, binary.StatusCode); Assert.True(binary.Headers.CacheControl?.NoStore); Assert.Equal(bytes, await binary.Content.ReadAsByteArrayAsync());
            Assert.Equal("folder_original.bin", binary.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync(baseRead + "/drive/files/exact-file/content?expectedVersion=old")).StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.GetAsync(baseRead + "/drive/files/exact-file/content?expectedVersion=1&maxBytes=1024")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(baseRead + "/email/messages")).StatusCode);
        }
        using var anonymous = ConnectedClient(factory); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(baseRead + (purpose == "EmailAccount" ? "/email/messages" : "/drive/files"))).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta", slug = "connected-beta" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(s.Root.Replace(s.Tenant.ToString(), beta.ToString()))).StatusCode);
        var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(s.Tenant))!;
        Assert.DoesNotContain(state.Assignments, a => a.PrincipalId == (factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance).Result)!.PrincipalId);
        var audit = await Json(await owner.GetAsync($"/api/v2/tenants/{s.Tenant}/audit")); Safe(audit); Assert.Contains(purpose == "EmailAccount" ? "email.read" : "drive.read", audit.GetRawText());
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_connected_account_inventory_filters_before_paging_and_remote_read_requires_independent_use(bool sqlite)
    {
        await using var factory = new OAuthFactory(sqlite: sqlite); using var owner = ConnectedClient(factory);
        var s = await ConnectedReadAccount(factory, owner, "EmailAccount"); var api = $"/api/v2/tenants/{s.Tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
        var account = await identity.RegisterAsync("connected-reader@example.test", "Connected reader", "strong-connected-reader-password");
        var role = await access.SaveRoleAsync(actor, s.Tenant, null, "Agent inventory", ["agents.read"], await Revision(owner, s.Tenant));
        var invite = await access.InviteAsync(actor, s.Tenant, account.LoginKey, role.RoleId, await Revision(owner, s.Tenant));
        var member = await access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-connected-reader-password");
        var readPath = s.Root + "/" + s.Alias + "/email/messages?query=harness";
        Assert.Equal(0, (await Json(await reader.GetAsync(s.Root + "?pageSize=1"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(readPath)).StatusCode);
        async Task<AgentCore.Domain.Security.RoleAssignment> Grant(string key)
        {
            var saved = await access.SaveRoleAsync(actor, s.Tenant, null, key, [key], await Revision(owner, s.Tenant));
            return await access.AssignRoleAsync(actor, s.Tenant, member.PrincipalId, saved.RoleId,
                AgentCore.Domain.Security.AccessScope.ForResource(s.Tenant, "credential", s.Credential.ToString("D")), await Revision(owner, s.Tenant));
        }
        await Grant("credentials.read"); Assert.Equal(0, (await Json(await reader.GetAsync(s.Root))).GetProperty("total").GetInt32());
        var mail = await Grant("email.read"); var visible = await Json(await reader.GetAsync(s.Root + "?pageSize=1"));
        Assert.Equal(1, visible.GetProperty("total").GetInt32()); Assert.False(visible.GetProperty("items")[0].GetProperty("readAvailable").GetBoolean());
        Assert.Empty((await Json(await reader.GetAsync(s.Root + "?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(readPath)).StatusCode);
        var use = await Grant("credentials.use"); Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(readPath)).StatusCode);
        await access.RevokeAssignmentAsync(actor, s.Tenant, use.AssignmentId, await Revision(owner, s.Tenant));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(readPath)).StatusCode);
        await access.RevokeAssignmentAsync(actor, s.Tenant, mail.AssignmentId, await Revision(owner, s.Tenant));
        var hidden = await Json(await reader.GetAsync(s.Root + "?pageSize=1")); Assert.Equal(0, hidden.GetProperty("total").GetInt32());
        Assert.DoesNotContain("Exact connected account", hidden.GetRawText()); Assert.Equal(0, factory.Outbound);
    }

    private sealed class ConnectedReadAfter(ITenantConnectedAccountReader inner) : ITenantConnectedAccountReader
    {
        public Func<Task>? After;
        public ConnectedAccountAvailability Availability(Credential c) => inner.Availability(c);
        public async ValueTask<ConnectedAccountReadResult> ReadAsync(TenantConnectedAccount c, ConnectedAccountRead r, Func<CancellationToken, ValueTask<Credential>> current,
            Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance, CancellationToken ct = default)
        {
            var result = await inner.ReadAsync(c, r, current, maintenance, ct);
            if (Interlocked.Exchange(ref After, null) is { } after) await after(); return result;
        }
    }
    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "relogin")] [InlineData(true, "relogin")]
    [InlineData(false, "unlink")] [InlineData(true, "unlink")] [InlineData(false, "disable")] [InlineData(true, "disable")]
    public async Task Tenant_connected_account_HTTP_withholds_captured_private_body_after_original_auth_or_source_changes(bool sqlite, string change)
    {
        await using var source = new OAuthFactory(sqlite: sqlite); ConnectedReadAfter? probe = null;
        await using var factory = source.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<ITenantConnectedAccountReader>(p => probe = new(p.GetRequiredService<TenantConnectedAccountReader>())))));
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, "EmailAccount");
        _ = factory.Services.GetRequiredService<ITenantConnectedAccountReader>();
        probe!.After = async () => {
            if (change is "logout" or "relogin")
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (change == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
            else if (change == "unlink") Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/v2/tenants/{s.Tenant}/agents/{s.Instance}/credential-bindings/{s.Binding}?expectedRevision={await Revision(owner, s.Tenant)}&expectedInstanceRevision=2&expectedBindingRevision=1")).StatusCode);
            else Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v2/tenants/{s.Tenant}/credentials/{s.Credential}", new TenantCredentialUpdateRequest("Disabled exact account", "Disabled", await Revision(owner, s.Tenant), 2))).StatusCode);
        };
        using var response = await owner.GetAsync(s.Root + "/" + s.Alias + "/email/messages?query=harness");
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("syn-msg", body); Assert.DoesNotContain("mailbox", body); Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
