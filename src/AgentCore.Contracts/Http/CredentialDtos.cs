namespace AgentCore.Contracts.Http;

public sealed record AdminCredentialResponse(Guid CredentialId, string DisplayName, string Kind, string Status,
    IReadOnlyDictionary<string,string> Metadata, IReadOnlyList<string> AllowedOrigins, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int BindingCount, string Purpose = "General", EmailAccountProfileDto? EmailAccount = null, DriveAccountProfileDto? DriveAccount = null, ContainerRegistryProfileDto? ContainerRegistry = null);
public sealed record AdminCredentialBindingResponse(Guid BindingId, Guid CredentialId, string Reference, long Revision, AdminCredentialResponse Credential);
public sealed record AdminCreateCredentialRequest(string DisplayName, string Kind, IReadOnlyDictionary<string,string>? Metadata, IReadOnlyList<string>? AllowedOrigins, string ProtectedValue);
public sealed record AdminUpdateCredentialRequest(long ExpectedRevision, string DisplayName, string Status, IReadOnlyDictionary<string,string>? Metadata, IReadOnlyList<string>? AllowedOrigins);
public sealed record AdminReplaceCredentialRequest(long ExpectedRevision, string ProtectedValue);
public sealed record AdminBindCredentialRequest(Guid CredentialId, string Reference, long ExpectedInstanceRevision);
public sealed record AdminResetBrowserProfileRequest(long ExpectedInstanceRevision, bool Confirm);

public sealed record MailEndpointDto(string Host, int Port, string TlsMode);
public sealed record EmailAccountProfileDto(string Preset, string EmailAddress = "", string ImapUsername = "", string SmtpUsername = "",
    MailEndpointDto? ImapEndpoint = null, MailEndpointDto? SmtpEndpoint = null,
    string? AuthMethod = null, string? GoogleSubject = null, IReadOnlyList<string>? GrantedScopes = null,
    string ConnectionStatus = "NotConnected", long AuthEpoch = 0, DateTimeOffset? ConnectedAtUtc = null, bool Synthetic = false);
public sealed record AdminCreateEmailAccountRequest(string DisplayName, EmailAccountProfileDto Profile, string ProtectedValue = "");
public sealed record AdminUpdateEmailAccountRequest(long ExpectedRevision, string DisplayName, string Status, EmailAccountProfileDto Profile);
public sealed record AdminTestEmailAccountRequest(long ExpectedRevision, string Protocol);

public sealed record AdminEmailOAuthRequest(long ExpectedRevision, bool Confirm = false);

public sealed record AdminEmailMonitoringRequest(long ExpectedRevision, bool Enabled, int IntervalSeconds);
public sealed record AdminEmailMonitoringCheckRequest(long ExpectedRevision);

public sealed record DriveAccountProfileDto(string EmailAddress, string ConnectionStatus, long AuthEpoch, DateTimeOffset? ConnectedAtUtc, DateTimeOffset? ReconnectRequiredAtUtc, bool Synthetic);

public sealed record ContainerRegistryProfileDto(string Registry, string Username, IReadOnlyList<string> RepositoryPrefixes, DateTimeOffset? ExpiresAtUtc = null);
public sealed record AdminCreateContainerRegistryRequest(string DisplayName, string Kind, ContainerRegistryProfileDto Profile, string ProtectedValue);

public sealed record AdminUpdateContainerRegistryRequest(long ExpectedRevision, string DisplayName, string Status, ContainerRegistryProfileDto Profile);
