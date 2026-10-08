namespace AgentCore.Application.Ports;

public sealed record ArtifactRecord(
    Guid ArtifactId,
    Guid SessionId,
    string DisplayName,
    string ContentType,
    long ByteSize,
    string Sha256Hex,
    Guid? SourceAttachmentId,
    string? WorkspaceLogicalPath,
    DateTimeOffset CreatedAt,
    Guid? AgentRunId = null);

public sealed record ArtifactPage(IReadOnlyList<ArtifactRecord> Items, Guid? NextCursor, bool HasMore);

public interface IArtifactStore
{
    ValueTask<ArtifactPage> ListPageAsync(Guid sessionId, Guid? before, int limit, CancellationToken cancellationToken = default, Guid? agentRunId = null);
    bool Exists(Guid sessionId, Guid artifactId);

    ValueTask<ArtifactRecord> CreateAsync(
        Guid sessionId,
        string displayName,
        string contentType,
        ReadOnlyMemory<byte> bytes,
        Guid? sourceAttachmentId,
        string? workspaceLogicalPath,
        CancellationToken cancellationToken = default,
        Guid? agentRunId = null);

    ValueTask<ArtifactRecord?> GetAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ArtifactRecord>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenContentAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IArtifactReferenceAuthorizer
{
    bool IsAuthorized(Guid sessionId, string artifactId);
}

public sealed class FixtureArtifactReferenceAuthorizer : IArtifactReferenceAuthorizer
{
    public const string AuthorizedId = "fixture-artifact-1";

    public bool IsAuthorized(Guid sessionId, string artifactId) =>
        string.Equals(artifactId, AuthorizedId, StringComparison.Ordinal);
}

public sealed class SessionArtifactAuthorizer(IArtifactStore? store) : IArtifactReferenceAuthorizer
{
    public bool IsAuthorized(Guid sessionId, string artifactId)
    {
        if (string.Equals(artifactId, FixtureArtifactReferenceAuthorizer.AuthorizedId, StringComparison.Ordinal))
        {
            return true;
        }

        return Guid.TryParse(artifactId, out var id) && store is not null && store.Exists(sessionId, id);
    }
}
