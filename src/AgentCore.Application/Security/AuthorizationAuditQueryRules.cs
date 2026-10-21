using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class AuthorizationAuditQueryRules
{
    public static AuthorizationAuditQuery Normalize(AuthorizationAuditQuery query)
    {
        if (query.ActorPrincipalId == Guid.Empty || query.From > query.Through
            || query.Action is { } action && (action.Length is < 1 or > 128 || action.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            || query.Decision is not (null or "allowed" or "denied" or "requiresApproval" or "indeterminate"))
            throw new ArgumentException("Invalid audit filters.");
        return query with { Page = Math.Clamp(query.Page, 0, 100_000), PageSize = Math.Clamp(query.PageSize, 1, 100),
            From = query.From?.ToUniversalTime(), Through = query.Through?.ToUniversalTime() };
    }

    public static IQueryable<AuthorizationAudit> Filter(IQueryable<AuthorizationAudit> rows, Guid? tenant, AuthorizationAuditQuery query)
    {
        rows = rows.Where(a => a.TenantId == tenant);
        if (tenant is null)
            rows = rows.Where(a => a.Execution == null && a.Decision == "allowed" &&
                (a.Action == "identity.bootstrap" && a.ReasonCode == "explicit_cli_bootstrap"
                 || a.Action == "account.password.change" && a.ReasonCode == "reauthenticated"
                 || a.Action == "system.accounts.status" && a.ReasonCode == "authorized"
                 || (a.Action == "system.operators.assign" || a.Action == "system.operators.revoke") && a.ReasonCode == "authorized"
                 || a.Action == "system.browser.privacy.update" && a.ReasonCode == "policy_saved_restart_required"));
        if (query.Action is { } action) rows = rows.Where(a => a.Action == action);
        if (query.Decision is { } decision) rows = rows.Where(a => a.Decision == decision);
        if (query.ActorPrincipalId is { } actor) rows = rows.Where(a => a.ActorPrincipalId == actor);
        if (query.From is { } from) rows = rows.Where(a => a.At >= from);
        if (query.Through is { } through) rows = rows.Where(a => a.At <= through);
        return rows;
    }

    // System audit never returns free-form target text, execution context, or a tenant ID.
    public static AuthorizationAudit SystemSafe(AuthorizationAudit audit) => audit with { TenantId = null, Execution = null,
        Target = audit.Action == "system.browser.privacy.update" ? "host"
            : Guid.TryParseExact(audit.Target, "D", out var id) && id != Guid.Empty ? id.ToString("D") : "unavailable" };
}
