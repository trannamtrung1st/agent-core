using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteArtifactStore
{
    // Reject the whole batch's quota/collision before writing any private blob.
    internal async ValueTask RequireBrowserBatchCapacityAsync(AgentCoreDbContext db, TenantRuntimeFrame frame,
        IReadOnlyList<TenantBrowserArtifactFile> files, CancellationToken ct)
    {
        await RequireFrame(db, frame, time.GetUtcNow(), ct).ConfigureAwait(false); ThrowIfDeleted(frame.SessionId);
        var ids = files.Select(f => f.ArtifactId.ToString("D")).ToArray();
        if (await db.Artifacts.AnyAsync(a => ids.Contains(a.ArtifactId), ct).ConfigureAwait(false)) throw new SecurityConflictException("artifact_publication_mismatch");
        var used = await db.Artifacts.Where(a => a.SessionId == frame.SessionId.ToString("D")).SumAsync(a => a.ByteSize, ct).ConfigureAwait(false);
        used += db.Artifacts.Local.Where(a => a.SessionId == frame.SessionId.ToString("D") && db.Entry(a).State == EntityState.Added).Sum(a => a.ByteSize);
        if (files.Any(f => f.Content.Length > Math.Min(maxBytesEach, ArtifactLimits.MaxBytesEach)) || used + files.Sum(f => (long)f.Content.Length) > maxBytesSession)
            throw AgentCore.Application.Sessions.AgentCoreErrors.ArtifactQuotaExceeded();
    }
}
