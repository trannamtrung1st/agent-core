using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
namespace AgentCore.Infrastructure.Drive;

internal sealed partial class GoogleDriveProvider(Guid credential, long revision, GoogleDriveOAuthService oauth, IHttpClientFactory clients,
    IDriveOperationStore operations, ICredentialProtector protector, TimeProvider time) : IGoogleDriveProvider
{
    public const string HttpClientName = "GoogleDrive";
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string Fields = "id,name,mimeType,size,version,md5Checksum,parents,trashed,webViewLink,driveId";
    private static string Enc(string value) => Uri.EscapeDataString(value);
    private static AgentCoreException Failure(string code, string message, int status) => new(code, message, status);
    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> create, bool readOnly, CancellationToken ct, bool effectStart = false)
    {
        using var client = clients.CreateClient(HttpClientName);
        string token;
        try { ct.ThrowIfCancellationRequested(); token = await oauth.AccessTokenAsync(credential, revision, ct); }
        catch (AgentCoreException ex) when (effectStart) { throw new DriveEffectNotExecutedException(ex.Code, ex.Message); }
        catch (OperationCanceledException) when (effectStart) { throw new DriveEffectNotExecutedException("drive_cancelled_before_dispatch", "The write was cancelled before its request was sent."); }
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = create(); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException) when (readOnly && attempt < 1) { await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct); continue; }
            catch (HttpRequestException) { throw Failure("drive_provider_unavailable", "Drive transport failed. Inspect any pending operation before retrying writes.", 502); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("drive_provider_unavailable", "Drive timed out. Inspect any pending operation before retrying writes.", 502); }
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                response.Dispose();
                try { token = await oauth.AccessTokenAsync(credential, revision, ct, true); }
                catch (AgentCoreException ex) when (effectStart) { throw new DriveEffectNotExecutedException(ex.Code, ex.Message); }
                catch (OperationCanceledException) when (effectStart) { throw new DriveEffectNotExecutedException("drive_cancelled_before_dispatch", "The rejected write was cancelled before authorization recovery."); }
                refreshed = true; continue;
            }
            if (readOnly && attempt < 2 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
            { var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250); response.Dispose(); await Task.Delay(delay > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : delay, time, ct); continue; }
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 308) return response;
            var status = (int)response.StatusCode; response.Dispose();
            if (status == 401)
            {
                await oauth.RejectAuthorizationAsync(credential, revision, ct);
                if (effectStart) throw new DriveEffectNotExecutedException("drive_access_revoked", "Drive rejected authorization. Reconnect the account.");
                throw Failure("drive_access_revoked", "Drive authorization was revoked. Reconnect the account.", 403);
            }
            var failure = status switch
            {
                400 => Failure("drive_invalid", "Google rejected the request. Check its name, type and destination.", 400),
                403 => Failure("drive_scope_limited", "This item is not available under app-authorized Drive access, or permission was revoked. Check the account and file access; broad scope is not requested.", 403),
                404 => Failure("drive_not_found", "Drive file was not found in files available to Agent Core.", 404),
                409 or 412 => Failure("drive_remote_conflict", "The remote file or operation changed. Inspect it before another action.", 409),
                429 => Failure("drive_rate_limited", "Google Drive is rate limited. Retry after the provider delay.", 429),
                413 => Failure("drive_transfer_too_large", "Google rejected the transfer size. Use a smaller file.", 413),
                _ => Failure("drive_provider_unavailable", "Google Drive could not complete the request. Inspect any pending operation before retrying writes.", 502)
            };
            // Only an explicit rejection of the initial effect request proves non-execution.
            // Missing metadata, conflicts, transport errors and expired upload sessions do not.
            if (effectStart && status is 400 or 403 or 404 or 413 or 429)
                throw new DriveEffectNotExecutedException(failure.Code, failure.Message);
            throw failure;
        }
    }
    public async ValueTask<string> GenerateIdAsync(CancellationToken ct)
    {
        using var response = await Send(() => new(HttpMethod.Get, Api + "files/generateIds?count=1&space=drive&type=files"), true, ct); using var json = await GoogleDriveOAuthService.ReadJson(response, 65536, ct);
        var id = json.RootElement.GetProperty("ids")[0].GetString(); return id is { Length: > 0 and <= 160 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : throw Failure("drive_provider_response_invalid", "Google returned an invalid file identity.", 502);
    }
    public async ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct)
    {
        static string Literal(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
        var filter = "trashed = false" + (folder is null ? "" : " and '" + Literal(folder) + "' in parents") + (query is null ? "" : " and name contains '" + Literal(query) + "'");
        var url = Api + "files?q=" + Enc(filter) + "&pageSize=" + limit + "&fields=" + Enc("nextPageToken,files(" + Fields + ")") + (cursor is null ? "" : "&pageToken=" + Enc(cursor));
        using var response = await Send(() => new(HttpMethod.Get, url), true, ct); using var json = await GoogleDriveOAuthService.ReadJson(response, 512 * 1024, ct); var root = json.RootElement;
        var files = root.GetProperty("files").EnumerateArray().Take(limit + 1).Select(Map).ToArray(); if (files.Length > limit) throw Failure("drive_provider_response_invalid", "Drive listing exceeded its bound.", 502);
        return new(files, root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null);
    }
    public async ValueTask<DriveFile> GetAsync(string id, CancellationToken ct)
    {
        using var response = await Send(() => new(HttpMethod.Get, Api + "files/" + Enc(id) + "?fields=" + Enc(Fields)), true, ct); using var json = await GoogleDriveOAuthService.ReadJson(response, 65536, ct); var file = Map(json.RootElement);
        if (file.Id != id) throw Failure("drive_provider_response_invalid", "Drive returned an unexpected identity.", 502); return file;
    }
    private static HttpContent Json(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    public async ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct)
    { using var response = await Send(() => new(HttpMethod.Post, Api + "files?fields=id") { Content = Json(new { id, name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parent } }) }, false, ct, effectStart: true); }
    public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => DownloadAsync(file, GoogleDriveOptions.MaxTransferBytes, ct);
    public async ValueTask<byte[]> DownloadAsync(DriveFile file, int maxBytes, CancellationToken ct)
    {
        if (maxBytes is <= 0 or > GoogleDriveOptions.MaxTransferBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var code = maxBytes < GoogleDriveOptions.MaxTransferBytes ? "preview_too_large" : "drive_transfer_too_large";
        var message = maxBytes < GoogleDriveOptions.MaxTransferBytes ? "Drive file exceeds its effective preview limit. Download the original." : "Drive file exceeds 50 MiB.";
        if (file.NativeDocument || file.Folder) throw Failure("drive_native_unsupported", "Download supports binary files. Google-native export and folder download are deferred.", 400);
        if (file.ByteSize > maxBytes) throw Failure(code, message, 413);
        using var response = await Send(() => new(HttpMethod.Get, Api + "files/" + Enc(file.Id) + "?alt=media"), true, ct);
        if (response.Content.Headers.ContentLength > maxBytes) throw Failure(code, message, 413);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[81920]; while (true) { var read = await input.ReadAsync(buffer, ct); if (read == 0) break; if (output.Length + read > maxBytes) throw Failure(code, message, 413); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public async ValueTask RenameAsync(string id, string name, CancellationToken ct) { using var response = await Send(() => new(HttpMethod.Patch, Api + "files/" + Enc(id) + "?fields=id") { Content = Json(new { name }) }, false, ct, effectStart: true); }
    public async ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) { using var response = await Send(() => new(HttpMethod.Patch, Api + "files/" + Enc(id) + "?fields=id&removeParents=" + Enc(source) + "&addParents=" + Enc(destination)) { Content = Json(new { }) }, false, ct, effectStart: true); }
    public async ValueTask TrashAsync(string id, CancellationToken ct) { using var response = await Send(() => new(HttpMethod.Patch, Api + "files/" + Enc(id) + "?fields=id") { Content = Json(new { trashed = true }) }, false, ct, effectStart: true); }
    internal static DriveFile Map(JsonElement root)
    {
        string Required(string property, int limit) { var value = root.GetProperty(property).GetString(); return value is { Length: > 0 } && value.Length <= limit && !value.Any(char.IsControl) ? value : throw Failure("drive_provider_response_invalid", "Drive metadata is invalid.", 502); }
        if (root.TryGetProperty("driveId", out var shared) && shared.ValueKind == JsonValueKind.String) throw Failure("drive_operation_unsupported", "Shared Drives are deferred.", 400);
        var id = Required("id", 160); var name = Required("name", 255); var mime = Required("mimeType", 255); var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        var size = root.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var parents = root.TryGetProperty("parents", out var p) ? p.EnumerateArray().Select(value => value.GetString() ?? "").Take(2).ToArray() : [];
        var link = root.TryGetProperty("webViewLink", out var w) ? w.GetString() : null;
        if (link is not null && (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.Host is not ("drive.google.com" or "docs.google.com"))) link = null;
        if (size < 0 || string.IsNullOrEmpty(version) || version.Length > 64 || parents.Any(parent => parent.Length > 160 || parent.Any(char.IsControl))) throw Failure("drive_provider_response_invalid", "Drive metadata exceeds limits.", 502);
        return new(id, name, mime, size, version, root.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() : null, parents, root.TryGetProperty("trashed", out var t) && t.GetBoolean(), link);
    }
}
