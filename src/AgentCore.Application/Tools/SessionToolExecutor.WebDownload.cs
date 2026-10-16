using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private async Task<string> DownloadWebAsync(AgentDefinition definition, Guid sessionId, JsonElement args, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (publicWebFetcher?.SupportsDownloads != true || agentWorkspace is null || workspace is null)
            return Error("unavailable", "Public downloads require an owned writable Workspace.");
        if (admission?.AgentInstanceId is not Guid owner || admission.OwnedSessionId != sessionId
            || owner != await agentWorkspace.SessionOwnerAsync(sessionId, ct) || !await agentWorkspace.SessionWritableAsync(sessionId, ct))
            return Error("forbidden", "Download requires the active owning Session and Agent Instance.");
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => p.Name is not ("url" or "path"))
            || args.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != args.EnumerateObject().Count()
            || !TryString(args, "url", out var urlText) || urlText.Length > 4096
            || !Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
            return Error("invalid", "Use an absolute public HTTP(S) URL without credentials and only the documented download arguments.");
        var isolation = await IsolationAsync(definition, sessionId, ct);
        string? path = null;
        if (args.TryGetProperty("path", out _))
        {
            if (!TryString(args, "path", out var raw) || raw.Length > 1024) return Error("invalid", "path must be a Workspace file path.");
            path = DownloadPath(raw);
        }
        var started = Stopwatch.GetTimestamp();
        var result = await publicWebFetcher.DownloadAsync(new(url), ct);
        RuntimeTelemetry.Record("web.download", RuntimeTelemetry.ElapsedMs(started));
        ct.ThrowIfCancellationRequested();
        if (result.ErrorCode is not null) return JsonSerializer.Serialize(new { status = "failed", untrusted = true, sourceUrl = url.AbsoluteUri, finalUrl = result.FinalUrl, error = result.ErrorCode, message = result.ErrorMessage });
        path ??= DownloadPath((isolation.HomeAccess == HomeAccess.ReadWrite ? "/home/downloads/" : "/working/downloads/") + SafeDownloadName(result.SuggestedFileName ?? new Uri(result.FinalUrl).AbsolutePath.Split('/').Last()));
        var mismatch = DownloadTypeMismatch(path, result.Bytes);
        if (mismatch is not null) return JsonSerializer.Serialize(new { status = "failed", untrusted = true, finalUrl = result.FinalUrl, contentType = result.ContentType, error = "file_type_mismatch", message = mismatch });
        var contentType = DownloadContentType(path, result.ContentType);
        await agentWorkspace.CommitBinaryAsync(sessionId, path, contentType, result.Bytes, ct);
        return JsonSerializer.Serialize(new { status = "completed", untrusted = true, path = AgentWorkspacePaths.Public(path), sourceUrl = url.AbsoluteUri, finalUrl = result.FinalUrl,
            contentType, declaredContentType = result.ContentType, byteSize = result.Bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(result.Bytes)),
            published = false, formatValidation = "Basic signature checks only; inspect with the appropriate tool before claiming format validity or editing support." });

        string DownloadPath(string raw)
        {
            var resolved = AgentWorkspacePaths.Resolve(raw, sessionId, admission.WorkspaceCwd ?? "/home", structural: true);
            if (!resolved.StartsWith("/home/", StringComparison.Ordinal) && !resolved.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal))
                throw AgentCoreErrors.Forbidden("Download destinations must be files in /home or current Session /working.");
            AgentCore.Application.Agents.StateIsolationAccess.RequireHome(isolation, resolved, true);
            return resolved;
        }
    }

    private static string SafeDownloadName(string suggestion)
    {
        var leaf = Uri.UnescapeDataString(suggestion).Replace('\\', '/').Split('/').Last();
        var safe = new string(leaf.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('.');
        if (safe.Length == 0) safe = "download.bin";
        if (safe.Length > 120) safe = safe[..100] + Path.GetExtension(safe)[..Math.Min(20, Path.GetExtension(safe).Length)];
        return safe;
    }

    private static string? DownloadTypeMismatch(string path, byte[] bytes)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".html" or ".htm" or ".txt" or ".xml") && LooksLikeHtml(bytes))
            return "Server returned an HTML page instead of the requested file, possibly an error or login page. No file was committed; browser state, login and CAPTCHA are unavailable.";
        var data = bytes.AsSpan();
        var valid = extension switch
        {
            ".doc" => data.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }),
            ".pdf" => data.StartsWith("%PDF-"u8),
            ".png" => data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => data.StartsWith(new byte[] { 255, 216, 255 }),
            ".docx" or ".xlsx" or ".zip" => IsDownloadPackage(bytes, extension),
            _ => true
        };
        return valid ? null : "Response bytes do not match the requested file extension or the package is corrupt. No file was committed.";
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        while (!bytes.IsEmpty && bytes[0] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') bytes = bytes[1..];
        return StartsWithAscii(bytes, "<!doctype html"u8) || StartsWithAscii(bytes, "<html"u8);
        static bool StartsWithAscii(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (var index = 0; index < prefix.Length; index++)
            {
                var value = data[index];
                if (value is >= (byte)'A' and <= (byte)'Z') value += 32;
                if (value != prefix[index]) return false;
            }
            return true;
        }
    }

    private static bool IsDownloadPackage(byte[] bytes, string extension)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return extension == ".zip" || zip.GetEntry("[Content_Types].xml") is not null
                && zip.GetEntry(extension == ".docx" ? "word/document.xml" : "xl/workbook.xml") is not null;
        }
        catch (InvalidDataException) { return false; }
    }

    private static string DownloadContentType(string path, string? declared) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => OfficeLimits.DocumentMime, ".xlsx" => OfficeLimits.SpreadsheetMime, ".doc" => "application/msword",
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".zip" => "application/zip",
        _ => string.IsNullOrWhiteSpace(declared) ? "application/octet-stream" : declared.Split(';', 2)[0].Trim()
    };
}
