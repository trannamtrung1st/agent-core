using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Exact publication intent and factual references extend the incumbent Browser/Artifact/receipt owners.
public static class TenantBrowserArtifactRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal) { "browser_command_not_completed", "artifact_publication_unavailable",
        "permission_denied", "artifact_quota_exceeded", "capture_limit", "download_limit", "capture_invalid", "download_invalid" };
    public static bool HasCapture(IEnumerable<ModelMessage> messages) => messages.Any(message =>
    {
        if (message.Role != ModelRole.Tool || message.Name != ToolCatalog.RequestAuthorization) return false;
        if (message.Parts?.OfType<ModelImageContent>().Any() == true) return true;
        try
        {
            using var doc = JsonDocument.Parse(message.Text);
            var node = doc.RootElement;
            if (node.ValueKind != JsonValueKind.Object) return false;
            if (!node.TryGetProperty("artifactPublication", out var publication) || publication.ValueKind != JsonValueKind.Object) return false;
            return publication.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
                && files.EnumerateArray().Any(file => file.ValueKind == JsonValueKind.Object && file.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "capture");
        }
        catch (JsonException) { return false; }
    });
    public static PreparedBrowserArtifacts Policy(bool capture, bool downloads, BrowserHostPolicy host)
    {
        var limits = host.Limits; limits.Validate();
        if (capture && (!host.ScreenshotAvailable || host.ScreenshotPolicy.Mode == BrowserScreenshotPrivacyMode.Disabled)) throw new AccessDeniedException("browser_policy_changed");
        return new(capture, downloads, limits.CaptureBytes, limits.DownloadBytes, limits.CapturesPerScope, limits.DownloadsPerScope,
            capture ? host.ScreenshotPolicy.Mode.ToString() : null, capture ? PrivacyHash(host.ScreenshotPolicy) : null);
    }
    public static string PrivacyHash(BrowserScreenshotPolicy policy) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        policy.Mode, UnmaskedOrigins = policy.UnmaskedOrigins.Order(StringComparer.Ordinal), TrustedGraphicsOrigins = policy.TrustedGraphicsOrigins.Order(StringComparer.Ordinal), policy.Revision })));
    public static void Shape(PreparedBrowserCommand value, BrowserCommand command)
    {
        if (value.Artifacts is not { } policy)
        { if (command is BrowserScreenshot) throw new ArgumentException("Capture requires exact Artifact publication intent."); return; }
        if (policy.Capture != (command is BrowserScreenshot) || !policy.Capture && !policy.Downloads || value.ReadOnly
            || policy.CaptureBytes is < 1 or > BrowserToolLimits.MaxCaptureBytes || policy.DownloadBytes is < 1 or > BrowserToolLimits.MaxDownloadBytes
            || policy.Capture && (policy.CapturePrivacyMode is not ("Protected" or "Unmasked") || policy.CapturePrivacyHash is not { Length: 64 }
                || policy.CapturePrivacyHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            || !policy.Capture && (policy.CapturePrivacyMode is not null || policy.CapturePrivacyHash is not null)
            || policy.CapturesPerScope is < 1 or > BrowserToolLimits.MaxCapturesPerScope || policy.DownloadsPerScope is < 1 or > BrowserToolLimits.MaxDownloadsPerScope)
            throw new ArgumentException("Use bounded exact Browser file publication intent.");
    }
    public static Guid Id(Guid operation, string kind, int ordinal) => kind == "capture" ? operation
        : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{operation:D}\ndownload\n{ordinal}"))[..16]);
    public static TenantBrowserArtifactBatch Capture(TenantApprovalRequest request, BrowserResult result)
    {
        var policy = request.Operation.BrowserCommand?.Artifacts ?? throw new ArgumentException("Browser publication intent required.");
        var files = new List<TenantBrowserArtifactFile>(); string? reason = null;
        if (policy.Capture)
        {
            var format = TenantApprovalRules.BrowserIntent(request.SessionId, request.Operation.BrowserCommand!.ToolName,
                request.Operation.BrowserCommand.ArgumentsJson).Command is BrowserScreenshot image ? image.Format : "";
            var type = format switch { "png" => "image/png", "jpeg" => "image/jpeg", "webp" => "image/webp", _ => "" };
            if (result.Bytes is not { Length: > 0 } bytes || bytes.Length > policy.CaptureBytes || result.ContentType != type || !Image(type, bytes)) reason = "capture_invalid";
            else files.Add(new("capture", request.Operation.OperationId, "screenshot." + format, type, bytes.ToArray()));
        }
        if (policy.Downloads)
        {
            var ordinal = 0;
            foreach (var download in result.Downloads ?? [])
            {
                var index = ordinal++;
                if (index >= policy.DownloadsPerScope) { reason ??= "download_limit"; break; }
                if (download.ErrorCode is not null || download.Bytes is not { } bytes || bytes.Length > policy.DownloadBytes)
                { reason ??= "download_invalid"; continue; }
                var type = download.ContentType is { Length: > 0 and <= 200 } mime && !mime.Any(char.IsControl)
                    && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(mime, out _) ? mime : "application/octet-stream";
                files.Add(new("download", Id(request.Operation.OperationId, "download", index), WorkspaceFileNames.Sanitize(download.FileName ?? "download.bin"), type, bytes.ToArray()));
            }
        }
        return new(files.ToArray(), reason);
    }
    private static bool Image(string type, byte[] bytes) => type switch {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8), _ => false };
    public static TenantBrowserArtifactBatch Bound(PreparedBrowserCommand command, Guid operation, TenantBrowserArtifactBatch batch, int captures, int downloads)
    {
        var policy = command.Artifacts ?? throw new ArgumentException("Publication intent required."); var kept = new List<TenantBrowserArtifactFile>();
        var reason = batch.UnavailableReason; var ids = new HashSet<Guid>();
        if (reason is not null && !Reasons.Contains(reason)) throw new ArgumentException("Use a body-free publication reason.");
        var capture = TenantApprovalRules.BrowserIntent(command.SessionId, command.ToolName, command.ArgumentsJson).Command as BrowserScreenshot;
        if (batch.Files.Count > 3) throw new ArgumentException("Bounded Browser output required.");
        foreach (var file in batch.Files)
        {
            if (!ids.Add(file.ArtifactId) || file.Kind is not ("capture" or "download")
                || file.Kind == "capture" && (!policy.Capture || capture is null || file.ArtifactId != operation || file.DisplayName != "screenshot." + capture.Format
                    || file.ContentType != "image/" + capture.Format || file.Content.Length > policy.CaptureBytes || !Image(file.ContentType, file.Content.ToArray()))
                || file.Kind == "download" && (!policy.Downloads || file.Content.Length > policy.DownloadBytes
                    || !Enumerable.Range(0, policy.DownloadsPerScope).Any(i => Id(operation, "download", i) == file.ArtifactId)))
                throw new ArgumentException("Exact Browser output source required.");
            if (file.Kind == "capture" && captures++ >= policy.CapturesPerScope) { reason ??= "capture_limit"; continue; }
            if (file.Kind == "download" && downloads++ >= policy.DownloadsPerScope) { reason ??= "download_limit"; continue; }
            kept.Add(file);
        }
        return new(kept.ToArray(), reason);
    }
    public static string Result(IReadOnlyList<TenantBrowserArtifactFile> files, string? reason = null) => Serialize(new(
        files.Count > 0 ? "published" : reason is null ? "empty" : "unavailable", reason, files.Select(f => new TenantBrowserArtifactReference(f.Kind, f.ArtifactId)).ToArray()));
    public static string Unavailable(string reason = "artifact_publication_unavailable") => Serialize(new("unavailable", reason, []));
    private static string Serialize(TenantBrowserArtifactResult value) => JsonSerializer.Serialize(value, Json);
    public static TenantBrowserArtifactResult Read(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 4096) throw new ArgumentException("Bounded Browser Artifact receipt required.");
        TenantBrowserArtifactResult value;
        try { value = JsonSerializer.Deserialize<TenantBrowserArtifactResult>(json, Json) ?? throw new ArgumentException("Browser Artifact receipt required."); }
        catch (JsonException e) { throw new ArgumentException("Exact Browser Artifact receipt required.", e); }
        if (value.Files is null || value.Files.Count > 3 || value.Files.Any(f => f is null || f.Kind is not ("capture" or "download") || f.ArtifactId == Guid.Empty)
            || value.Files.Select(f => f.ArtifactId).Distinct().Count() != value.Files.Count || value.Files.Count(f => f.Kind == "capture") > 1
            || value.Files.Count(f => f.Kind == "download") > 2 || value.ReasonCode is not null && !Reasons.Contains(value.ReasonCode)
            || value.Status is not ("published" or "empty" or "unavailable") || (value.Status == "published") != (value.Files.Count > 0)
            || value.Status == "empty" && value.ReasonCode is not null || value.Status == "unavailable" && value.ReasonCode is null || Serialize(value) != json)
            throw new ArgumentException("Exact body-free Browser Artifact receipt required.");
        return value;
    }
    public static void RequireBudget(PreparedBrowserArtifacts? policy, (int Captures, int Downloads) usage)
    {
        if (policy?.Capture == true && usage.Captures >= policy.CapturesPerScope) throw new AccessDeniedException("capture_limit");
        if (policy?.Downloads == true && usage.Downloads >= policy.DownloadsPerScope) throw new AccessDeniedException("download_limit");
    }
    public static (int Captures, int Downloads) Usage(IEnumerable<TenantOperationReceipt> receipts)
    {
        var files = receipts.Where(r => r.BrowserArtifactsJson is not null).SelectMany(r => Read(r.BrowserArtifactsJson!).Files).ToArray();
        return (files.Count(f => f.Kind == "capture"), files.Count(f => f.Kind == "download"));
    }
}
