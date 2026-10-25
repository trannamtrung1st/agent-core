namespace AgentCore.Contracts.Http;

public sealed record SecretCreateRequest(Guid SecretId, string Name, string Description, string ProtectedValue, long ExpectedRevision);
public sealed record SecretUpdateRequest(string Name, string Description, string Status, long ExpectedRevision, long ExpectedSecretRevision);
public sealed record SecretReplaceRequest(string ProtectedValue, long ExpectedRevision, long ExpectedSecretRevision);
public sealed record EnvironmentCreateRequest(Guid EnvironmentId, string Name, string Description, long ExpectedRevision, long ExpectedSpaceRevision);
public sealed record EnvironmentUpdateRequest(string Name, string Description, long ExpectedRevision, long ExpectedSpaceRevision, long ExpectedEnvironmentRevision);
public sealed record EnvironmentVariableRequest(string Kind, string Description, string? PlainValue, Guid? SecretId, Guid? CredentialId,
    long ExpectedRevision, long ExpectedSpaceRevision, long ExpectedEnvironmentRevision, long ExpectedVariableRevision);
