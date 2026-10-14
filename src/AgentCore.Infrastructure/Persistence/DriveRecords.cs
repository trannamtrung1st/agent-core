namespace AgentCore.Infrastructure.Persistence;

public sealed class DriveOperationRecord
{
    public string OperationId { get; set; } = ""; public string AgentInstanceId { get; set; } = ""; public string CredentialId { get; set; } = "";
    public string EffectHash { get; set; } = ""; public long AuthEpoch { get; set; }
    public string Tool { get; set; } = ""; public string FileId { get; set; } = "";
    public string SnapshotJson { get; set; } = ""; public string State { get; set; } = ""; public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public string? ResultJson { get; set; }
    public string? ProtectedUploadSession { get; set; }
}
public sealed class SyntheticDriveFileRecord
{
    public string CredentialId { get; set; } = ""; public string FileId { get; set; } = ""; public string MetadataJson { get; set; } = ""; public byte[] Bytes { get; set; } = [];
    public string Version { get; set; } = "1";
}
