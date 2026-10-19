namespace AgentCore.Contracts.Http;

public sealed record BrowserScreenshotPolicyResponse(string Mode, IReadOnlyList<string> UnmaskedOrigins,
    IReadOnlyList<string> TrustedGraphicsOrigins, long Revision);
public sealed record BrowserPrivacyDeploymentResponse(bool CaptureAllowed, bool UnmaskedAllowed,
    IReadOnlyList<string> UnmaskedOriginCeiling, IReadOnlyList<string> GraphicsOriginCeiling);
public sealed record BrowserPrivacyResponse(BrowserScreenshotPolicyResponse Saved, BrowserScreenshotPolicyResponse Effective,
    BrowserPrivacyDeploymentResponse Deployment, bool RestartRequired, string Activation, bool Durable, bool ConstrainedByDeployment);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveBrowserPrivacyRequest(long ExpectedRevision, string Mode,
    IReadOnlyList<string>? UnmaskedOrigins, IReadOnlyList<string>? TrustedGraphicsOrigins, bool AcknowledgeExposure);
