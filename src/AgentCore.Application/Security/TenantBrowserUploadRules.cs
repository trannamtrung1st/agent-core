using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

// Durable consent contains immutable file facts, never file bytes or a filesystem path.
public sealed record TenantBrowserUploadSource(Guid ArtifactId, string DisplayName, string ContentType, long ByteSize, string Sha256Hex);

public static class TenantBrowserUploadRules
{
    public static bool HasSources(BrowserCommand command) => command is BrowserUploadCommand or BrowserDrop { ArtifactId: not null };
    public static IReadOnlyList<Guid> Ids(BrowserCommand command) => command switch
    {
        BrowserUploadCommand upload => Ids(upload),
        BrowserDrop { ArtifactId: { } id, Text: null, MimeType: null } => CanonicalIds([id]),
        _ => throw new ArgumentException("Use either exact file-drop Artifact authority or literal text, never both.")
    };
    private static IReadOnlyList<Guid> CanonicalIds(IReadOnlyList<string> ids)
    {
        var parsed = ids.Select(id => Guid.TryParseExact(id, "D", out var value) && value != Guid.Empty && id == value.ToString("D")
            ? value : throw new ArgumentException("Use canonical Session Artifact IDs.")).ToArray();
        if (parsed.Distinct().Count() != parsed.Length) throw new ArgumentException("Use distinct Session Artifact IDs.");
        return parsed;
    }
    public static IReadOnlyList<Guid> Ids(BrowserUploadCommand upload)
    {
        if (upload.Uploads is not null || upload.ArtifactIds is not { Count: > 0 and <= 8 } ids)
            throw new ArgumentException("Use one to eight canonical Session Artifact IDs.");
        return CanonicalIds(ids);
    }
    public static IReadOnlyList<TenantBrowserUploadSource> Sources(PreparedBrowserCommand value)
    {
        var command = TenantApprovalRules.BrowserIntent(value.SessionId, value.ToolName, value.ArgumentsJson).Command;
        if (!HasSources(command))
        {
            if (value.ArtifactSourcesJson is not null || value.FileInputOrigin is not null) throw new ArgumentException("File sources require Browser upload or Artifact drop.");
            return [];
        }
        var ids = Ids(command);
        if (value.ArtifactSourcesJson is not { Length: > 0 and <= 16384 } json) throw new ArgumentException("Upload requires exact file sources.");
        TenantBrowserUploadSource[] sources;
        try { sources = JsonSerializer.Deserialize<TenantBrowserUploadSource[]>(json) ?? throw new ArgumentException("Missing upload sources."); }
        catch (JsonException ex) { throw new ArgumentException("Invalid upload sources.", ex); }
        if (sources.Any(s => s is null) || !sources.Select(s => s.ArtifactId).SequenceEqual(ids) || JsonSerializer.Serialize(sources) != json
            || sources.Any(s => string.IsNullOrWhiteSpace(s.DisplayName) || s.DisplayName.Length > 200 || s.DisplayName.Any(char.IsControl)
                || s.ContentType is not { Length: > 0 and <= 200 } || s.ContentType.Any(char.IsControl)
                || s.ByteSize is < 0 or > ArtifactLimits.MaxBytesEach || s.Sha256Hex is not { Length: 64 } || s.Sha256Hex.Any(c => !char.IsAsciiHexDigitLower(c)))
            || sources.Sum(s => s.ByteSize) > ArtifactLimits.MaxBytesEach)
            throw new ArgumentException("Use exact bounded immutable upload sources.");
        return sources;
    }
    public static TenantBrowserUploadSource Source(ArtifactRecord item) => new(item.ArtifactId, item.DisplayName, item.ContentType, item.ByteSize, item.Sha256Hex);
    public static void Require(Guid tenant, Guid session, TenantBrowserUploadSource source, ArtifactRecord? item)
    {
        if (item is null || item.TenantId != tenant || item.SessionId != session || Source(item) != source)
            throw new AccessDeniedException("browser_file_source_changed");
    }
}

public sealed class TenantBrowserUploadTarget(ITenantArtifactStore artifacts)
{
    public async ValueTask<string> CaptureAsync(Guid tenant, Guid session, BrowserCommand command, CancellationToken ct)
    {
        var sources = new List<TenantBrowserUploadSource>();
        foreach (var id in TenantBrowserUploadRules.Ids(command))
        {
            var item = await artifacts.FindTenantAsync(tenant, session, id, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_unavailable");
            if (item.TenantId != tenant || item.SessionId != session) throw new AccessDeniedException("resource_unavailable");
            sources.Add(TenantBrowserUploadRules.Source(item));
        }
        return JsonSerializer.Serialize(sources);
    }
    public async ValueTask RequireAsync(Guid tenant, PreparedBrowserCommand value, CancellationToken ct)
    {
        foreach (var source in TenantBrowserUploadRules.Sources(value))
            TenantBrowserUploadRules.Require(tenant, value.SessionId, source,
                await artifacts.FindTenantAsync(tenant, value.SessionId, source.ArtifactId, ct).ConfigureAwait(false));
    }
    public async ValueTask<IReadOnlyList<BrowserUpload>> ReadAsync(Guid tenant, PreparedBrowserCommand value, CancellationToken ct)
    {
        var uploads = new List<BrowserUpload>();
        foreach (var source in TenantBrowserUploadRules.Sources(value))
        {
            var item = await artifacts.FindTenantAsync(tenant, value.SessionId, source.ArtifactId, ct).ConfigureAwait(false);
            TenantBrowserUploadRules.Require(tenant, value.SessionId, source, item);
            var bytes = await artifacts.ReadTenantContentAsync(tenant, value.SessionId, source.ArtifactId, ct).ConfigureAwait(false);
            TenantArtifactRules.RequireContent(item!, bytes);
            uploads.Add(new(source.DisplayName, source.ContentType, bytes));
        }
        return uploads;
    }
}
