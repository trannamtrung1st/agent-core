using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class WorkCaptureRow
{
    public string CaptureId { get; set; } = "";

    public string WorkItemId { get; set; } = "";

    public string AgentInstanceId { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long ByteSize { get; set; }

    public string Sha256Hex { get; set; } = "";

    public long CreatedAtUtc { get; set; }

    public long RetainUntilUtc { get; set; }

    public string RelativePath { get; set; } = "";
}

public sealed class InMemoryWorkCaptureStore(TimeProvider time) : IWorkCaptureStore
{
    private readonly object _gate = new();
    private readonly List<Stored> _items = [];

    public ValueTask<WorkCaptureSaveResult> SaveAsync(
        Guid workItemId,
        Guid agentInstanceId,
        string contentType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || bytes.Length > BrowserToolLimits.MaxDownloadBytes)
        {
            return new(new WorkCaptureSaveResult(null, "capture_too_large"));
        }

        lock (_gate)
        {
            var now = time.GetUtcNow();
            _items.RemoveAll(item => item.Capture.RetainUntil <= now);
            if (_items.Count(item => item.Capture.WorkItemId == workItemId) >= BrowserToolLimits.MaxWorkCaptures)
            {
                return new(new WorkCaptureSaveResult(null, "capture_limit"));
            }

            var capture = new WorkCapture(
                Guid.CreateVersion7(),
                workItemId,
                agentInstanceId,
                string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(),
                now,
                WorkCaptureRetention.Until(now, null));
            _items.Add(new Stored(capture, bytes.ToArray()));
            return new(new WorkCaptureSaveResult(capture, null));
        }
    }

    public ValueTask ExtendRetentionAsync(
        Guid workItemId,
        DateTimeOffset terminalAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            for (var index = 0; index < _items.Count; index++)
            {
                var item = _items[index];
                if (item.Capture.WorkItemId != workItemId)
                {
                    continue;
                }

                var until = WorkCaptureRetention.Until(item.Capture.CreatedAt, terminalAt);
                if (until > item.Capture.RetainUntil)
                {
                    _items[index] = item with
                    {
                        Capture = item.Capture with { RetainUntil = until }
                    };
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private sealed record Stored(WorkCapture Capture, byte[] Bytes);
}

public sealed class SqliteWorkCaptureStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    TimeProvider time,
    string blobRoot) : IWorkCaptureStore
{
    public async ValueTask<WorkCaptureSaveResult> SaveAsync(
        Guid workItemId,
        Guid agentInstanceId,
        string contentType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0 || bytes.Length > BrowserToolLimits.MaxDownloadBytes)
        {
            return new WorkCaptureSaveResult(null, "capture_too_large");
        }

        Directory.CreateDirectory(blobRoot);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var owner = workItemId.ToString("D");
        var expired = await db.WorkCaptures
            .Where(row => row.RetainUntilUtc <= now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expired.Count > 0)
        {
            db.WorkCaptures.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in expired)
            {
                TryDelete(Path.Combine(blobRoot, row.RelativePath));
            }
        }

        var count = await db.WorkCaptures.CountAsync(row => row.WorkItemId == owner, cancellationToken)
            .ConfigureAwait(false);
        if (count >= BrowserToolLimits.MaxWorkCaptures)
        {
            return new WorkCaptureSaveResult(null, "capture_limit");
        }

        var created = time.GetUtcNow();
        var capture = new WorkCapture(
            Guid.CreateVersion7(),
            workItemId,
            agentInstanceId,
            string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(),
            created,
            WorkCaptureRetention.Until(created, null));
        var relative = Path.Combine(
            "work-captures",
            workItemId.ToString("N"),
            capture.CaptureId.ToString("N") + ExtensionFor(capture.ContentType));
        var full = Path.Combine(blobRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
        db.WorkCaptures.Add(new WorkCaptureRow
        {
            CaptureId = capture.CaptureId.ToString("D"),
            WorkItemId = owner,
            AgentInstanceId = agentInstanceId.ToString("D"),
            ContentType = capture.ContentType,
            ByteSize = capture.ByteSize,
            Sha256Hex = capture.Sha256Hex,
            CreatedAtUtc = created.ToUnixTimeMilliseconds(),
            RetainUntilUtc = capture.RetainUntil.ToUnixTimeMilliseconds(),
            RelativePath = relative
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(full);
            throw;
        }

        return new WorkCaptureSaveResult(capture, null);
    }

    public async ValueTask ExtendRetentionAsync(
        Guid workItemId,
        DateTimeOffset terminalAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var owner = workItemId.ToString("D");
        var rows = await db.WorkCaptures.Where(row => row.WorkItemId == owner).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            var created = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc);
            var until = WorkCaptureRetention.Until(created, terminalAt).ToUnixTimeMilliseconds();
            if (until > row.RetainUntilUtc)
            {
                row.RetainUntilUtc = until;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/png" => ".png",
        "text/csv" => ".csv",
        "text/plain" => ".txt",
        _ => ".bin"
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
