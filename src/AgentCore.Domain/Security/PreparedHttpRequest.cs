namespace AgentCore.Domain.Security;

// Contains only an exact public request. No Credential/browser identity or authority snapshot.
public sealed record PreparedHttpRequest(Guid SessionId, string ArgumentsJson, string RequestHash);

// Factual metadata only; response text belongs to an authorized continuation checkpoint.
public sealed record TenantHttpReceipt(int StatusCode, string FinalUrl, string? ContentType,
    long ProjectedBodyBytes, string BodySha256, bool Truncated);
