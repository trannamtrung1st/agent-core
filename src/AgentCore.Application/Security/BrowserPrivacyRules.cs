using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class BrowserPrivacyRules
{
    public static BrowserPrivacyAuthority FreezeAuthority(BrowserPrivacyAuthority authority) => authority with {
        UnmaskedOriginCeiling = BrowserPrivacyService.ExactOrigins(authority.UnmaskedOriginCeiling), GraphicsOriginCeiling = BrowserPrivacyService.ExactOrigins(authority.GraphicsOriginCeiling) };
    public static BrowserScreenshotPolicy Prepare(SystemBrowserPrivacyChange change, BrowserPrivacyAuthority authority)
    {
        if (change.ExpectedRevision < 0 || change.ExpectedRevision == long.MaxValue || !Enum.GetNames<BrowserScreenshotPrivacyMode>().Contains(change.Mode, StringComparer.Ordinal))
            throw AgentCoreErrors.Validation("Use Protected, Unmasked or Disabled and a nonnegative expected revision.");
        var selected = Enum.Parse<BrowserScreenshotPrivacyMode>(change.Mode);
        var unmasked = BrowserPrivacyService.ExactOrigins(change.UnmaskedOrigins ?? []); var graphics = BrowserPrivacyService.ExactOrigins(change.TrustedGraphicsOrigins ?? []);
        if (!authority.CaptureAllowed && selected != BrowserScreenshotPrivacyMode.Disabled) throw AgentCoreErrors.Forbidden("Deployment policy prohibits screenshots.");
        if (selected == BrowserScreenshotPrivacyMode.Unmasked && (!change.AcknowledgeExposure || unmasked.Count == 0))
            throw AgentCoreErrors.Validation("Unmasked capture requires exact origins and explicit acknowledgement of exposure to model providers and Session artifacts.");
        if (selected != BrowserScreenshotPrivacyMode.Unmasked && unmasked.Count > 0) throw AgentCoreErrors.Validation("Unmasked origins apply only to Unmasked mode.");
        if (selected == BrowserScreenshotPrivacyMode.Unmasked && !authority.UnmaskedAllowed
            || unmasked.Except(authority.UnmaskedOriginCeiling, StringComparer.OrdinalIgnoreCase).Any()
            || graphics.Except(authority.GraphicsOriginCeiling, StringComparer.OrdinalIgnoreCase).Any()) throw AgentCoreErrors.Forbidden("The requested capture exception exceeds deployment authority.");
        return new(selected, unmasked, graphics, checked(change.ExpectedRevision + 1));
    }
    public static void ValidateAudit(HumanSessionReference human, AuthorizationAudit audit)
    {
        if (audit.AuditId == Guid.Empty || audit.TenantId is not null || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "system.browser.privacy.update"
            || audit.Target != "host" || audit.Decision != "allowed" || audit.ReasonCode != "policy_saved_restart_required" || audit.Execution is not null || audit.At.Offset != TimeSpan.Zero)
            throw new ArgumentException("Use exact body-free system Browser privacy audit facts.");
    }
}
