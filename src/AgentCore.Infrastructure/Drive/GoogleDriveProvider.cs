using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
namespace AgentCore.Infrastructure.Drive;

internal sealed class GoogleDriveProvider(Guid credential, long revision, GoogleDriveOAuthService oauth, IHttpClientFactory clients,
    IDriveOperationStore operations, ICredentialProtector protector, TimeProvider time) : IGoogleDriveProvider
{
    public const string HttpClientName = "GoogleDrive";
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string Fields = "id,name,mimeType,size,version,md5Checksum,parents,trashed,webViewLink,driveId";
    private static string Enc(string value) => Uri.EscapeDataString(value);
    private static AgentCoreException Failure(string code, string message, int status) => new(code, message, status);
    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> create, bool readOnly, CancellationToken ct)
    {
        using var client = clients.CreateClient(HttpClientName); var token = await oauth.AccessTokenAsync(credential, revision, ct); var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = create(); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException) when (readOnly && attempt < 1) { await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct); continue; }
            catch (HttpRequestException) { throw Failure("drive_provider_unavailable", "Drive transport failed. Inspect any pending operation before retrying writes.", 502); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("drive_provider_unavailable", "Drive timed out. Inspect any pending operation before retrying writes.", 502); }
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            { response.Dispose(); token = await oauth.AccessTokenAsync(credential, revision, ct, true); refreshed = true; continue; }
            if (readOnly && attempt < 2 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
            { var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250); response.Dispose(); await Task.Delay(delay > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : delay, time, ct); continue; }
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 308) return response;
            var status = (int)response.StatusCode; response.Dispose();
            if (status == 401) { await oauth.RejectAuthorizationAsync(credential, revision, ct); throw Failure("drive_access_revoked", "Drive authorization was revoked. Reconnect the account.", 403); }
            throw status switch
            {
                403 => Failure("drive_scope_limited", "This item is not available under app-authorized Drive access, or permission was revoked. Check the account and file access; broad scope is not requested.", 403),
                404 => Failure("drive_not_found", "Drive file was not found in files available to Agent Core.", 404),
                409 or 412 => Failure("drive_remote_conflict", "The remote file or operation changed. Inspect it before another action.", 409),
                429 => Failure("drive_rate_limited", "Google Drive is rate limited. Retry after the provider delay.", 429),
                _ => Failure("drive_provider_unavailable", "Google Drive could not complete the request. Inspect any pending operation before retrying writes.", 502)
            };
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
    { using var response = await Send(() => new(HttpMethod.Post, Api + "files?fields=id") { Content = Json(new { id, name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parent } }) }, false, ct); }
    public async ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation operation, CancellationToken ct)
    {
        if (bytes.Length > GoogleDriveOptions.MaxTransferBytes) throw Failure("drive_transfer_too_large", "Drive file exceeds 50 MiB.", 413);
        using var start = await Send(() => { var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id") { Content = Json(new { id, name, mimeType = mime, parents = new[] { parent } }) }; request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mime); request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture)); return request; }, false, ct);
        var session = start.Headers.Location;
        if (session is null || session.Scheme != "https" || session.Host != "www.googleapis.com" || !session.IsDefaultPort || session.UserInfo != "" || session.Fragment != "" || !session.AbsolutePath.StartsWith("/upload/drive/", StringComparison.Ordinal)) throw Failure("drive_effect_indeterminate", "The resumable upload session could not be verified. Inspect the generated file ID.", 409);
        // Opaque upload-session authority is encrypted in the backend receipt, never sent to the model/browser or logged.
        operation = await operations.UpdateAsync(operation with { ProtectedUploadSession = protector.Protect(credential, 1, session.AbsoluteUri), Revision = operation.Revision + 1 }, operation.Revision, ct);
        using var response = await Send(() => { var content = new ByteArrayContent(bytes.ToArray()); content.Headers.ContentType = MediaTypeHeaderValue.Parse(mime); if (bytes.Length > 0) content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length); return new HttpRequestMessage(HttpMethod.Put, session) { Content = content }; }, false, ct);
        if ((int)response.StatusCode == 308) throw Failure("drive_effect_indeterminate", "Upload completion is uncertain. Reconcile the generated file ID before retrying.", 409);
    }
    public async ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct)
    {
        if (file.NativeDocument || file.Folder) throw Failure("drive_native_unsupported", "Download supports binary files. Google-native export and folder download are deferred.", 400);
        if (file.ByteSize > GoogleDriveOptions.MaxTransferBytes) throw Failure("drive_transfer_too_large", "Drive file exceeds 50 MiB.", 413);
        using var response = await Send(() => new(HttpMethod.Get, Api + "files/" + Enc(file.Id) + "?alt=media"), true, ct);
        if (response.Content.Headers.ContentLength > GoogleDriveOptions.MaxTransferBytes) throw Failure("drive_transfer_too_large", "Drive file exceeds 50 MiB.", 413);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[81920]; while (true) { var read = await input.ReadAsync(buffer, ct); if (read == 0) break; if (output.Length + read > GoogleDriveOptions.MaxTransferBytes) throw Failure("drive_transfer_too_large", "Drive file exceeds 50 MiB.", 413); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public async ValueTask RenameAsync(string id, string name, CancellationToken ct) { using var response = await Send(() => new(HttpMethod.Patch, Api + "files/" + Enc(id) + "?fields=id") { Content = Json(new { name }) }, false, ct); }
    public async ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) { using var response = await Send(() => new(HttpMethod.Patch, Api + "files/" + Enc(id) + "?fields=id&removeParents=" + Enc(source) + "&addParents=" + Enc(destination)) { Content = Json(new { }) }, false, ct); }
    public async ValueTask TrashAsync(string id, CancellationToken ct) { using var response = await Send(() => new(HttpMethod.Patch, Api + "files/" + Enc(id) + "?fields=id") { Content = Json(new { trashed = true }) }, false, ct); }
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
