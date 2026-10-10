namespace AgentCore.Contracts.Http;

public sealed record AdminBrowserScreenshotPolicy(string Mode, IReadOnlyList<string> UnmaskedOrigins,
    IReadOnlyList<string> TrustedGraphicsOrigins, long Revision);
public sealed record AdminBrowserPrivacyDeployment(bool CaptureAllowed, bool UnmaskedAllowed,
    IReadOnlyList<string> UnmaskedOriginCeiling, IReadOnlyList<string> GraphicsOriginCeiling);
public sealed record AdminBrowserPrivacyResponse(AdminBrowserScreenshotPolicy Saved, AdminBrowserScreenshotPolicy Effective,
    AdminBrowserPrivacyDeployment Deployment, bool RestartRequired, string Activation, bool Durable);
public sealed record AdminSaveBrowserPrivacyRequest(long ExpectedRevision, string Mode,
    IReadOnlyList<string>? UnmaskedOrigins, IReadOnlyList<string>? TrustedGraphicsOrigins, bool AcknowledgeExposure);
