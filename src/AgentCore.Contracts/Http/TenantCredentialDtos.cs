namespace AgentCore.Contracts.Http;

public sealed record TenantEmailProfileInput(string Preset, string EmailAddress, string ImapUsername = "", string SmtpUsername = "",
    MailEndpointDto? ImapEndpoint = null, MailEndpointDto? SmtpEndpoint = null);
public sealed record TenantCredentialCreateRequest(string DisplayName, string Kind, string Purpose, long ExpectedRevision,
    IReadOnlyDictionary<string, string>? Metadata = null, IReadOnlyList<string>? AllowedOrigins = null, string? ProtectedValue = null,
    TenantEmailProfileInput? EmailAccount = null, ContainerRegistryProfileDto? ContainerRegistry = null);
public sealed record TenantCredentialUpdateRequest(string DisplayName, string Status, long ExpectedRevision, long ExpectedCredentialRevision,
    IReadOnlyDictionary<string, string>? Metadata = null, IReadOnlyList<string>? AllowedOrigins = null,
    TenantEmailProfileInput? EmailAccount = null, ContainerRegistryProfileDto? ContainerRegistry = null);
public sealed record TenantCredentialReplaceRequest(string ProtectedValue, long ExpectedRevision, long ExpectedCredentialRevision);
