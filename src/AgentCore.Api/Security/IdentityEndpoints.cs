using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace AgentCore.Api.Security;

public static class IdentityEndpoints
{
    public sealed record LoginRequest(string Login, string Password);
    public sealed record InvitationProof(Guid TenantId, Guid InvitationId, string Token);
    public sealed record RegistrationRequest(string Login, string DisplayName, string Password, InvitationProof Invitation);
    public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

    public static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/v2/auth");
        auth.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var pair = antiforgery.GetAndStoreTokens(http);
            return Results.Json(new { token = pair.RequestToken, headerName = IdentityHttp.CsrfHeader });
        });
        auth.MapPost("/login", (LoginRequest request, HttpContext http, IdentityService identity, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var account = await identity.SignInAsync(request.Login, request.Password, ct).ConfigureAwait(false);
            if (account is null) return Results.Json(new { reasonCode = "invalid_credentials", title = "The email or password is incorrect." }, statusCode: 401);
            // Validate the server-issued Session on every request. The encrypted ticket stores references only.
            await http.SignInAsync(IdentityHttp.Scheme, IdentityHttp.Claims(account), new AuthenticationProperties
            { IsPersistent = false, AllowRefresh = false, IssuedUtc = account.Session.CreatedAt, ExpiresUtc = account.Session.ExpiresAt }).ConfigureAwait(false);
            return Results.Json(SafeAccount(account.Account));
        })).RequireRateLimiting("account-login");
        auth.MapPost("/logout", (HttpContext http, IdentityService identity, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            if (IdentityHttp.SessionId(http.User) is { } id) await identity.SignOutAsync(id, ct).ConfigureAwait(false);
            await http.SignOutAsync(IdentityHttp.Scheme).ConfigureAwait(false);
            return Results.NoContent();
        }));
        auth.MapPost("/invitation", (InvitationProof proof, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var (invitation, tenant) = await access.InspectInvitationAsync(proof.TenantId, proof.InvitationId, proof.Token, ct).ConfigureAwait(false);
            return Results.Json(new { invitation.InvitationId, tenant.TenantId, organizationName = tenant.Name, login = invitation.LoginKey, invitation.ExpiresAt });
        })).RequireRateLimiting("account-login");
        auth.MapPost("/register", (RegistrationRequest request, IdentityService identity, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(request.Invitation);
            var (invitation, _) = await access.InspectInvitationAsync(request.Invitation.TenantId, request.Invitation.InvitationId, request.Invitation.Token, ct).ConfigureAwait(false);
            if (IdentityService.NormalizeLogin(request.Login) != invitation.LoginKey) throw new AccessDeniedException("invitation_account_mismatch");
            var account = await identity.RegisterAsync(request.Login, request.DisplayName, request.Password, ct).ConfigureAwait(false);
            await access.AcceptInvitationAsync(account.AccountId, request.Invitation.TenantId, request.Invitation.InvitationId, request.Invitation.Token, ct).ConfigureAwait(false);
            // Registration does not issue a web session; the explicit sign-in is protected against login CSRF too.
            return Results.Json(SafeAccount(account), statusCode: 201);
        })).RequireRateLimiting("account-login");

        var accounts = app.MapGroup("/api/v2/account").RequireAuthorization();
        accounts.MapGet("/me", (HttpContext http, IIamStore store, IdentityService identity, IamAuthorizer authorizer, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var human = await IdentityHttp.SystemHumanAsync(http, store, ct).ConfigureAwait(false);
            var account = await identity.RequireAccountHumanAsync(human, ct).ConfigureAwait(false);
            var system = await store.ReadSystemAsync(ct).ConfigureAwait(false);
            var principal = system.Principals.SingleOrDefault(p => p.AccountId == account.AccountId && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active);
            var permissions = new List<string>();
            if (principal is not null)
                foreach (var permission in PermissionCatalog.All.Where(p => p.SystemOnly))
                    if ((await authorizer.CheckAsync(principal.PrincipalId, permission.Key, AccessScope.System, ct).ConfigureAwait(false)).Allowed)
                        permissions.Add(permission.Key);
            _ = await identity.RequireAccountHumanAsync(human, ct).ConfigureAwait(false);
            return Results.Json(new { account = SafeAccount(account), systemPrincipalId = principal?.PrincipalId, systemPermissions = permissions });
        }));
        accounts.MapPost("/password", (PasswordChangeRequest request, HttpContext http, IdentityService identity, IIamStore store, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            await identity.ChangePasswordAsync(IdentityHttp.AccountId(http.User)!.Value, request.CurrentPassword, request.NewPassword, ct, await IdentityHttp.SystemHumanAsync(http, store, ct).ConfigureAwait(false)).ConfigureAwait(false);
            await http.SignOutAsync(IdentityHttp.Scheme).ConfigureAwait(false);
            return Results.NoContent();
        }));
        accounts.MapGet("/tenants", (HttpContext http, IIamStore store, IdentityService identity, int? page, int? pageSize, string? q, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var human = await IdentityHttp.SystemHumanAsync(http, store, ct).ConfigureAwait(false);
            _ = await identity.RequireAccountHumanAsync(human, ct).ConfigureAwait(false);
            var all = await store.ListMembershipsAsync(IdentityHttp.AccountId(http.User)!.Value, ct).ConfigureAwait(false);
            var query = q?.Trim();
            var memberships = string.IsNullOrEmpty(query) ? all : all.Where(m => m.Tenant.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.Tenant.Slug.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            var size = Math.Clamp(pageSize ?? 30, 1, 100); var index = Math.Clamp(page ?? 0, 0, 100_000);
            _ = await identity.RequireAccountHumanAsync(human, ct).ConfigureAwait(false);
            return Results.Json(new { items = memberships.Skip(index * size).Take(size).Select(m => new { m.Tenant.TenantId, m.Tenant.Name, m.Tenant.Slug, m.User.UserId, m.User.PrincipalId }), total = memberships.Count, page = index, pageSize = size });
        }));
        accounts.MapPost("/tenant-invitations/{invitationId:guid}/accept", (Guid invitationId, InvitationProof proof, HttpContext http, TenantAccessService access, IIamStore store, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            if (proof.InvitationId != invitationId) throw new AccessDeniedException("invitation_unavailable");
            var user = await access.AcceptInvitationAsync(IdentityHttp.AccountId(http.User)!.Value, proof.TenantId, invitationId, proof.Token, ct, await IdentityHttp.SystemHumanAsync(http, store, ct).ConfigureAwait(false)).ConfigureAwait(false);
            return Results.Json(new { user.TenantId, user.UserId, user.PrincipalId });
        }));


    }

    private static object SafeAccount(Account account) => new { account.AccountId, account.LoginKey, account.DisplayName };
}
