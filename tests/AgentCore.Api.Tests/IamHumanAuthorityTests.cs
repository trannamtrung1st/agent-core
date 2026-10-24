using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class IamHumanAuthorityTests
{
    private const string Password = "explicit-synthetic-test-password";
    private const string Replacement = "replacement-synthetic-password";
    private const string IntendedPassword = "explicit-human-next-password";
    public static IEnumerable<object[]> Mutations() => from sqlite in new[] { false, true } from boundary in new[] { "logout", "password" }
        from action in new[] { "invite", "revoke_invite", "member_suspend", "member_remove", "role_create", "role_update", "assign", "revoke_assignment", "accept", "password" }
        select new object[] { sqlite, boundary, action };
    [Theory, MemberData(nameof(Mutations))]
    public async Task IAM_HTTP_commit_rechecks_original_login_without_partial_membership_roles_or_password(bool sqlite, string boundary, string action)
    {
        await using var factory = new AuthorityFactory(sqlite); using var owner = Client(factory); var s = await Prepare(factory, owner, action == "accept");
        using var accepting = Client(factory); var actor = action == "accept" ? accepting : owner;
        var login = action == "accept" ? "reader@example.test" : "owner@example.test";
        if (action == "accept") await Login(actor, login, Password);
        var root = $"/api/v2/tenants/{s.Tenant}"; var store = factory.Actual; var before = await Facts(store, s.Tenant);
        var revision = (await store.ReadTenantAsync(s.Tenant))!.Tenant.Revision; Account? retiredAccount = null;
        async Task Retire() { await Retirement(actor, boundary); retiredAccount = await store.ReadAccountAsync(action == "accept" ? s.ReaderAccount : s.OwnerAccount); }
        if (action == "password") factory.BeforeAccountWrite = Retire; else factory.BeforeTenantWrite = Retire;
        var currentPassword = Password;
        Task<HttpResponseMessage> Invoke() => action switch {
            "invite" => owner.PostAsJsonAsync(root + "/members/invite", new { login = "fresh@example.test", roleId = s.ReaderRole, expectedRevision = revision }),
            "revoke_invite" => owner.PostAsJsonAsync(root + $"/members/invitations/{s.Pending.Invitation.InvitationId}/revoke", new { expectedRevision = revision }),
            "member_suspend" or "member_remove" => owner.PutAsJsonAsync(root + $"/members/{s.ReaderUser}/status", new { status = action == "member_suspend" ? IdentityStatus.Suspended : IdentityStatus.Removed, expectedRevision = revision }),
            "role_create" => owner.PostAsJsonAsync(root + "/roles", new { name = "Explicit new role", permissions = new[] { "agents.read" }, expectedRevision = revision }),
            "role_update" => owner.PutAsJsonAsync(root + $"/roles/{s.CustomRole}", new { name = "Explicit edited role", permissions = new[] { "agents.read", "agents.chat" }, expectedRevision = revision }),
            "assign" => owner.PostAsJsonAsync(root + "/role-assignments", new { principalId = s.OwnerPrincipal, roleId = s.ReaderRole, expectedRevision = revision }),
            "revoke_assignment" => owner.DeleteAsync(root + $"/role-assignments/{s.CustomAssignment}?expectedRevision={revision}"),
            "accept" => actor.PostAsJsonAsync($"/api/v2/account/tenant-invitations/{s.Accept.Invitation.InvitationId}/accept", new { tenantId = s.Tenant, invitationId = s.Accept.Invitation.InvitationId, token = s.Accept.Token }),
            _ => actor.PostAsJsonAsync("/api/v2/account/password", new { currentPassword, newPassword = IntendedPassword })
        };
        using var refused = await Invoke(); Assert.Null(factory.BeforeTenantWrite); Assert.Null(factory.BeforeAccountWrite);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode); Assert.Equal("initiator_unavailable", (await Json(refused)).GetProperty("reasonCode").GetString());
        Assert.Equal(before, await Facts(store, s.Tenant)); Assert.Equal(retiredAccount, await store.ReadAccountAsync(action == "accept" ? s.ReaderAccount : s.OwnerAccount));
        Assert.Equal(HttpStatusCode.Unauthorized, (await actor.GetAsync("/api/v2/account/me")).StatusCode);
        await Login(actor, login, boundary == "password" ? Replacement : Password); currentPassword = boundary == "password" ? Replacement : Password;
        using var allowed = await Invoke(); Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        Assert.Equal(revision + (action == "password" ? 0 : 1), (await store.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        if (action == "password") Assert.NotNull(await factory.Services.GetRequiredService<IdentityService>().SignInAsync(login, IntendedPassword));
    }
    public static IEnumerable<object[]> Reads() => from sqlite in new[] { false, true } from boundary in new[] { "logout", "password" }
        from action in new[] { "access_me", "members", "members_search", "invitations", "roles", "permissions", "resource_access", "check_other", "check_self", "account_me", "account_tenants" }
        from phase in action is "members" or "members_search" ? new[] { "state", "account" } : new[] { "state" }
        select new object[] { sqlite, boundary, action, phase };
    [Theory, MemberData(nameof(Reads))]
    public async Task IAM_HTTP_read_withholds_captured_access_account_and_member_metadata_after_original_login_retirement(bool sqlite, string boundary, string action, string phase)
    {
        await using var factory = new AuthorityFactory(sqlite); using var owner = Client(factory); var s = await Prepare(factory, owner, false);
        var root = $"/api/v2/tenants/{s.Tenant}"; var before = await Facts(factory.Actual, s.Tenant);
        Func<Task> retire = () => Retirement(owner, boundary);
        if (action == "account_tenants") factory.AfterMembershipRead = retire;
        else if (action == "account_me") { factory.AfterSystemRead = retire; factory.SystemReadSkip = 2; }
        else if (phase == "account") { factory.AfterAccountRead = retire; factory.AccountReadId = s.ReaderAccount; }
        else { factory.AfterTenantRead = retire; factory.TenantReadSkip = 2; }
        var path = action switch { "access_me" => root + "/access/me", "members" => root + "/members", "members_search" => root + "/members?q=Reader",
            "invitations" => root + "/members/invitations", "roles" => root + "/roles", "permissions" => root + "/permissions",
            "resource_access" => root + $"/access/resources/role/{s.CustomRole}", "account_me" => "/api/v2/account/me", "account_tenants" => "/api/v2/account/tenants", _ => root + "/access/check" };
        Task<HttpResponseMessage> Invoke() => action.StartsWith("check_", StringComparison.Ordinal) ? owner.PostAsJsonAsync(path, new { principalId = action == "check_self" ? s.OwnerPrincipal : s.ReaderPrincipal, permission = "agents.read" }) : owner.GetAsync(path);
        using var refused = await Invoke(); Assert.Null(factory.AfterTenantRead); Assert.Null(factory.AfterAccountRead); Assert.Null(factory.AfterSystemRead); Assert.Null(factory.AfterMembershipRead);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode); var error = await Json(refused); Assert.Equal("initiator_unavailable", error.GetProperty("reasonCode").GetString());
        Assert.False(error.TryGetProperty("items", out _)); Assert.False(error.TryGetProperty("permissions", out _)); Assert.False(error.TryGetProperty("allowed", out _));
        Assert.DoesNotContain("Private Reader", error.GetRawText()); Assert.DoesNotContain("Existing private role", error.GetRawText());
        Assert.Equal(before, await Facts(factory.Actual, s.Tenant));
        await Login(owner, "owner@example.test", boundary == "password" ? Replacement : Password);
        using var allowed = await Invoke(); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); Assert.Equal(before, await Facts(factory.Actual, s.Tenant));
    }
    private sealed record Setup(Guid Tenant, Guid OwnerAccount, Guid OwnerPrincipal, Guid ReaderAccount, Guid ReaderUser, Guid ReaderPrincipal, Guid ReaderRole, Guid CustomRole, Guid CustomAssignment, IssuedTenantInvitation Pending, IssuedTenantInvitation Accept);
    private static async Task<Setup> Prepare(AuthorityFactory factory, HttpClient owner, bool accepting)
    {
        await Login(owner, "owner@example.test", Password); var store = factory.Actual; var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
        var membership = (await store.ListMembershipsAsync((await Json(await owner.GetAsync("/api/v2/account/me"))).GetProperty("account").GetProperty("accountId").GetGuid())).Single();
        var tenant = membership.Tenant.TenantId; var principal = membership.User.PrincipalId; var state = (await store.ReadTenantAsync(tenant))!; var role = state.Roles.Single(r => r.Key == "reader").RoleId;
        var reader = await identity.RegisterAsync("reader@example.test", "Private Reader", Password); var invitation = await access.InviteAsync(principal, tenant, reader.LoginKey, role, state.Tenant.Revision);
        TenantUser? user = null; if (!accepting) user = await access.AcceptInvitationAsync(reader.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        state = (await store.ReadTenantAsync(tenant))!; var custom = await access.SaveRoleAsync(principal, tenant, null, "Existing private role", ["agents.read"], state.Tenant.Revision);
        var assignment = await access.AssignRoleAsync(principal, tenant, principal, custom.RoleId, AccessScope.ForTenant(tenant), (await store.ReadTenantAsync(tenant))!.Tenant.Revision);
        var pending = await access.InviteAsync(principal, tenant, "pending@example.test", role, (await store.ReadTenantAsync(tenant))!.Tenant.Revision);
        return new(tenant, membership.User.AccountId, principal, reader.AccountId, user?.UserId ?? Guid.Empty, user?.PrincipalId ?? Guid.Empty, role, custom.RoleId, assignment.AssignmentId, pending, invitation);
    }
    private static HttpClient Client(AgentCoreApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    private static async Task<JsonElement> Json(HttpResponseMessage response) { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.Clone(); }
    private static async Task Login(HttpClient client, string login, string password) { await Csrf(client); using var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { login, password }); Assert.Equal(HttpStatusCode.OK, response.StatusCode); await Csrf(client); }
    private static async Task Csrf(HttpClient client) { var value = await Json(await client.GetAsync("/api/v2/auth/csrf")); client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", value.GetProperty("token").GetString()); }
    private static async Task Retirement(HttpClient client, string boundary) { using var response = boundary == "logout" ? await client.PostAsync("/api/v2/auth/logout", null) : await client.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = Password, newPassword = Replacement }); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); }
    private static async Task<string> Facts(IIamStore store, Guid tenant) => JsonSerializer.Serialize(new { state = await store.ReadTenantAsync(tenant), audit = await store.ReadAuditAsync(tenant, 200) });
    private sealed record ActualStore(IIamStore Value);
    private sealed class AuthorityFactory(bool sqlite) : AgentCoreApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "iam-human-authority-" + Guid.NewGuid().ToString("N"));
        public Func<Task>? BeforeTenantWrite, BeforeAccountWrite, AfterTenantRead, AfterAccountRead, AfterSystemRead, AfterMembershipRead;
        public int TenantReadSkip, SystemReadSkip; public Guid AccountReadId;
        public IIamStore Actual => Services.GetRequiredService<ActualStore>().Value;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces") };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.ConfigureTestServices(services => {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys"); options.ArtifactRoot = Path.Combine(root, "artifacts"); options.DefinitionResourceRoot = Path.Combine(root, "resources");
                if (sqlite) { Directory.CreateDirectory(root); var connection = $"Data Source={Path.Combine(root, "state.db")};Foreign Keys=True;Default Timeout=10"; services.AddDbContextFactory<AgentCoreDbContext>(o => o.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(root, "keys")); }
                var original = services.Last(d => d.ServiceType == typeof(IIamStore));
                services.AddSingleton<ActualStore>(p => new((IIamStore)(original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(p) ?? ActivatorUtilities.CreateInstance(p, original.ImplementationType!))));
                foreach (var contract in typeof(InMemoryIamStore).GetInterfaces().Concat(typeof(SqliteIamStore).GetInterfaces()).Distinct().Where(t => t != typeof(IIamStore) && t.Namespace == typeof(IIamStore).Namespace))
                    if (services.Any(d => d.ServiceType == contract)) services.Replace(ServiceDescriptor.Singleton(contract, p => p.GetRequiredService<ActualStore>().Value));
                services.Replace(ServiceDescriptor.Singleton<IIamStore>(p => new AuthorityStore(p.GetRequiredService<ActualStore>().Value, this)));
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class AuthorityStore(IIamStore inner, AuthorityFactory factory) : IIamStore
    {
        public async ValueTask<bool> OwnsResourceAsync(AccessScope scope, CancellationToken ct = default) { var value = await inner.OwnsResourceAsync(scope, ct); return value; }
        public async ValueTask<Account?> FindAccountAsync(string loginKey, CancellationToken ct = default) { var value = await inner.FindAccountAsync(loginKey, ct); return value; }
        public async ValueTask<Account?> ReadAccountAsync(Guid accountId, CancellationToken ct = default) { var value = await inner.ReadAccountAsync(accountId, ct); if (accountId == factory.AccountReadId && Interlocked.Exchange(ref factory.AfterAccountRead, null) is { } hook) await hook(); return value; }
        public async ValueTask<bool> HasBootstrapAsync(CancellationToken ct = default) { var value = await inner.HasBootstrapAsync(ct); return value; }
        public async ValueTask<bool> TryBootstrapAsync(Account account, SystemSecurityState system, TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default) { var value = await inner.TryBootstrapAsync(account, system, tenant, audit, ct); return value; }
        public async ValueTask<bool> TryCreateAccountAsync(Account account, Principal accountPrincipal, CancellationToken ct = default) { var value = await inner.TryCreateAccountAsync(account, accountPrincipal, ct); return value; }
        public async ValueTask<bool> TryUpdateAccountAsync(Account account, string expectedSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) { if (Interlocked.Exchange(ref factory.BeforeAccountWrite, null) is { } hook) await hook(); var value = await inner.TryUpdateAccountAsync(account, expectedSecurityStamp, audit, ct, initiator); return value; }
        public async ValueTask<SystemSecurityState> ReadSystemAsync(CancellationToken ct = default) { var value = await inner.ReadSystemAsync(ct); if (factory.AfterSystemRead is not null && factory.SystemReadSkip-- <= 0 && Interlocked.Exchange(ref factory.AfterSystemRead, null) is { } hook) await hook(); return value; }
        public async ValueTask<TenantSecurityState?> ReadTenantAsync(Guid tenantId, CancellationToken ct = default) { var value = await inner.ReadTenantAsync(tenantId, ct); if (factory.AfterTenantRead is not null && factory.TenantReadSkip-- <= 0 && Interlocked.Exchange(ref factory.AfterTenantRead, null) is { } hook) await hook(); return value; }
        public async ValueTask<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid accountId, CancellationToken ct = default) { var value = await inner.ListMembershipsAsync(accountId, ct); if (Interlocked.Exchange(ref factory.AfterMembershipRead, null) is { } hook) await hook(); return value; }
        public async ValueTask<bool> TryCreateTenantAsync(TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default) { var value = await inner.TryCreateTenantAsync(tenant, audit, ct); return value; }
        public async ValueTask<bool> TrySaveTenantAsync(TenantSecurityState tenant, long expectedRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) { if (Interlocked.Exchange(ref factory.BeforeTenantWrite, null) is { } hook) await hook(); var value = await inner.TrySaveTenantAsync(tenant, expectedRevision, audit, ct, initiator); return value; }
        public async ValueTask SaveSessionAsync(AuthenticationSession session, CancellationToken ct = default) { await inner.SaveSessionAsync(session, ct);  }
        public async ValueTask<AuthenticationSession?> ReadSessionAsync(Guid sessionId, CancellationToken ct = default) { var value = await inner.ReadSessionAsync(sessionId, ct); return value; }
        public async ValueTask RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default) { await inner.RevokeSessionAsync(sessionId, now, ct);  }
        public async ValueTask<IReadOnlyList<AuthorizationAudit>> ReadAuditAsync(Guid tenantId, int limit, CancellationToken ct = default) { var value = await inner.ReadAuditAsync(tenantId, limit, ct); return value; }
    }
}
