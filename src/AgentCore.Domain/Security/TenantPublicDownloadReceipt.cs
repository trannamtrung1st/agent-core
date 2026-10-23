namespace AgentCore.Domain.Security;

// Factual completed transfer, never a read grant. File bytes remain only in the owned filesystem.
public sealed record TenantPublicDownloadReceipt(string RequestedUrl, string FinalUrl, string Path,
    string ContentType, long ByteCount, string Sha256, string? SuggestedFileName = null);
