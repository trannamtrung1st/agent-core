using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Security;

public static class TenantAccessEndpoints
{
    public sealed record InviteRequest(string Login, Guid RoleId, long ExpectedRevision);
    public sealed record RevisionRequest(long ExpectedRevision);
    public sealed record MemberStatusRequest(IdentityStatus Status, long ExpectedRevision);
    public sealed record RoleRequest(string Name, string[] Permissions, long ExpectedRevision);
    public sealed record AssignmentRequest(Guid PrincipalId, Guid RoleId, long ExpectedRevision);
    public sealed record CheckRequest(Guid PrincipalId, string Permission, string? ResourceType, string? ResourceId);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}").RequireAuthorization();
        group.MapGet("/access/me", (HttpContext http, IIamStore store, IamAuthorizer authorizer, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var state = await store.ReadTenantAsync(actor.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("not_member");
            var permissions = new List<string>();
            foreach (var permission in PermissionCatalog.All.Where(p => !p.SystemOnly))
                if ((await authorizer.CheckAsync(actor.PrincipalId, permission.Key, AccessScope.ForTenant(actor.TenantId), ct).ConfigureAwait(false)).Allowed)
                    permissions.Add(permission.Key);
            var roleIds = state.Assignments.Where(a => a.PrincipalId == actor.PrincipalId && a.Scope.Kind == AccessScopeKind.Tenant).Select(a => a.RoleId).ToHashSet();
            return Results.Json(new { tenant = state.Tenant, actor.UserId, actor.PrincipalId, permissions,
                roles = state.Roles.Where(r => roleIds.Contains(r.RoleId)).Select(r => new { r.RoleId, r.Name }), revision = state.Tenant.Revision });
        }));
        group.MapGet("/members", (HttpContext http, TenantAccessService access, IIamStore store, int? page, int? pageSize, string? q, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var state = await access.ReadAsync(actor.PrincipalId, actor.TenantId, ct: ct).ConfigureAwait(false);
            var size = Math.Clamp(pageSize ?? 30, 1, 100); var index = Math.Clamp(page ?? 0, 0, 100_000);
            var rows = new List<object>();
            IReadOnlyList<TenantUser> visible = state.Users;
            if (!string.IsNullOrWhiteSpace(q))
            {
                var matches = new List<TenantUser>();
                foreach (var user in state.Users)
                {
                    var account = await store.ReadAccountAsync(user.AccountId, ct).ConfigureAwait(false);
                    if (account is not null && (account.DisplayName.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
                        || account.LoginKey.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))) matches.Add(user);
                }
                visible = matches;
            }
            foreach (var member in visible.OrderBy(u => u.CreatedAt).ThenBy(u => u.UserId).Skip(index * size).Take(size))
            {
                var account = await store.ReadAccountAsync(member.AccountId, ct).ConfigureAwait(false);
                rows.Add(new { member.UserId, member.PrincipalId, member.Status, displayName = account?.DisplayName, login = account?.LoginKey,
                    roles = state.Assignments.Where(a => a.PrincipalId == member.PrincipalId).Select(a => new { a.AssignmentId, a.RoleId, a.Scope }) });
            }
            return Results.Json(new { items = rows, total = visible.Count, revision = state.Tenant.Revision, page = index, pageSize = size });
        }));
        group.MapGet("/members/invitations", (HttpContext http, TenantAccessService access, TimeProvider time, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var state = await access.ReadAsync(actor.PrincipalId, actor.TenantId, "members.invite", ct).ConfigureAwait(false);
            var now = time.GetUtcNow();
            return Results.Json(new { items = state.Invitations.OrderByDescending(i => i.ExpiresAt).Take(100).Select(i => new
            {
                i.InvitationId, i.LoginKey, i.ExpiresAt, roleName = state.Roles.SingleOrDefault(r => r.RoleId == i.RoleId)?.Name,
                status = i.Revoked ? "Revoked" : i.AcceptedAt is not null ? "Accepted" : i.ExpiresAt <= now ? "Expired" : "Pending"
            }), revision = state.Tenant.Revision });
        }));
        group.MapPost("/members/invite", (InviteRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await access.InviteAsync(actor.PrincipalId, actor.TenantId, body.Login, body.RoleId, body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(new { result.Invitation.InvitationId, result.Invitation.TenantId, result.Invitation.LoginKey,
                result.Invitation.ExpiresAt, token = result.Token }, statusCode: 201);
        }));
        group.MapPost("/members/invitations/{invitationId:guid}/revoke", (Guid invitationId, RevisionRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await access.RevokeInvitationAsync(actor.PrincipalId, actor.TenantId, invitationId, body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
        group.MapPut("/members/{userId:guid}/status", (Guid userId, MemberStatusRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await access.SetMemberStatusAsync(actor.PrincipalId, actor.TenantId, userId, body.Status, body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
        group.MapGet("/roles", (HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var state = await access.ReadAsync(actor.PrincipalId, actor.TenantId, "iam.roles.read", ct).ConfigureAwait(false);
            return Results.Json(new { items = state.Roles, revision = state.Tenant.Revision });
        }));
        group.MapGet("/permissions", (HttpContext http, IamAuthorizer authorizer, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await authorizer.RequireAsync(actor.PrincipalId, "iam.roles.read", AccessScope.ForTenant(actor.TenantId), ct).ConfigureAwait(false);
            return Results.Json(new { items = PermissionCatalog.All.Where(p => !p.SystemOnly) });
        }));
        group.MapPost("/roles", (RoleRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => SaveRole(null, body, http, access, ct));
        group.MapPut("/roles/{roleId:guid}", (Guid roleId, RoleRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => SaveRole(roleId, body, http, access, ct));
        group.MapPost("/role-assignments", (AssignmentRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var assignment = await access.AssignRoleAsync(actor.PrincipalId, actor.TenantId, body.PrincipalId, body.RoleId,
                AccessScope.ForTenant(actor.TenantId), body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(assignment, statusCode: 201);
        }));
        group.MapDelete("/role-assignments/{assignmentId:guid}", (Guid assignmentId, long expectedRevision, HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await access.RevokeAssignmentAsync(actor.PrincipalId, actor.TenantId, assignmentId, expectedRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
        group.MapPost("/access/check", (CheckRequest body, HttpContext http, TenantAccessService access, IamAuthorizer authorizer, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            if (body.PrincipalId != actor.PrincipalId)
            {
                var state = await access.ReadAsync(actor.PrincipalId, actor.TenantId, "iam.roles.read", ct).ConfigureAwait(false);
                if (!state.Principals.Any(p => p.PrincipalId == body.PrincipalId)) throw new AccessDeniedException("principal_unavailable");
            }
            // Resource-specific inspection is added by the owning resource's Access endpoint after an
            // ownership lookup. This endpoint cannot fabricate a tenant for an arbitrary resource ID.
            if (body.ResourceType is not null || body.ResourceId is not null) throw new AccessDeniedException("resource_scope_required");
            return Results.Json(await authorizer.CheckAsync(body.PrincipalId, body.Permission, AccessScope.ForTenant(actor.TenantId), ct).ConfigureAwait(false));
        }));
        group.MapGet("/audit", (HttpContext http, IIamStore store, IamAuthorizer authorizer, int? limit, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await authorizer.RequireAsync(actor.PrincipalId, "security.audit.read", AccessScope.ForTenant(actor.TenantId), ct).ConfigureAwait(false);
            return Results.Json(new { items = await store.ReadAuditAsync(actor.TenantId, limit ?? 50, ct).ConfigureAwait(false) });
        }));
    }
    private static Task<IResult> SaveRole(Guid? roleId, RoleRequest body, HttpContext http, TenantAccessService access, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        return Results.Json(await access.SaveRoleAsync(actor.PrincipalId, actor.TenantId, roleId, body.Name, body.Permissions, body.ExpectedRevision, ct).ConfigureAwait(false), statusCode: roleId is null ? 201 : 200);
    });
}
