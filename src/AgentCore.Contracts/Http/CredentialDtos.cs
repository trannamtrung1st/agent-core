namespace AgentCore.Contracts.Http;

public sealed record AdminCredentialResponse(Guid CredentialId, string DisplayName, string Kind, string Status,
    IReadOnlyDictionary<string,string> Metadata, IReadOnlyList<string> AllowedOrigins, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int BindingCount);
public sealed record AdminCredentialBindingResponse(Guid BindingId, Guid CredentialId, string Reference, long Revision, AdminCredentialResponse Credential);
public sealed record AdminCreateCredentialRequest(string DisplayName, string Kind, IReadOnlyDictionary<string,string>? Metadata, IReadOnlyList<string>? AllowedOrigins, string ProtectedValue);
public sealed record AdminUpdateCredentialRequest(long ExpectedRevision, string DisplayName, string Status, IReadOnlyDictionary<string,string>? Metadata, IReadOnlyList<string>? AllowedOrigins);
public sealed record AdminReplaceCredentialRequest(long ExpectedRevision, string ProtectedValue);
public sealed record AdminBindCredentialRequest(Guid CredentialId, string Reference, long ExpectedInstanceRevision);
public sealed record AdminResetBrowserProfileRequest(long ExpectedInstanceRevision, bool Confirm);
