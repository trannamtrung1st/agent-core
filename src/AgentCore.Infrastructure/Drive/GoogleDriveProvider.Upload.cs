using System.Globalization;
using System.Net.Http.Headers;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Drive;

internal sealed partial class GoogleDriveProvider
{
    internal const int UploadChunkBytes = 1024 * 1024;
    private const int MaxUploadRecoveryChecks = 3;

    private static Uri UploadSession(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var session) || session.Scheme != "https"
            || session.Host != "www.googleapis.com" || !session.IsDefaultPort || session.UserInfo != ""
            || session.Fragment != "" || !session.AbsolutePath.StartsWith("/upload/drive/", StringComparison.Ordinal))
            throw Failure("drive_effect_indeterminate", "The upload session cannot be verified. Check the known operation; do not create another file.", 409);
        return session;
    }

    private static (bool Complete, int Offset) UploadProgress(HttpResponseMessage response, int total)
    {
        if ((int)response.StatusCode is 200 or 201) return (true, total);
        if ((int)response.StatusCode != 308)
            throw Failure("drive_effect_indeterminate", "Upload status is unverified. Check the known operation before acting.", 409);
        if (!response.Headers.TryGetValues("Range", out var values)) return (false, 0);
        var ranges = values.ToArray();
        if (ranges.Length != 1 || !ranges[0].StartsWith("bytes=0-", StringComparison.Ordinal)
            || !int.TryParse(ranges[0].AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || last < 0 || last >= total - 1)
            throw Failure("drive_provider_response_invalid", "Drive returned an invalid upload offset. No unverified continuation was sent.", 502);
        return (false, last + 1);
    }

    private async Task ExpiredUpload(DriveOperation operation)
    {
        var current = await operations.GetAsync(operation.AgentInstanceId, operation.OperationId, CancellationToken.None);
        if (current is { ProtectedUploadSession: not null, State: "Dispatched" or "Indeterminate" })
            await operations.UpdateAsync(current with { ProtectedUploadSession = null, Revision = current.Revision + 1 }, current.Revision, CancellationToken.None);
    }
    private async Task ReleaseCompletedUploadSession(DriveOperation operation)
    {
        var current = await operations.GetAsync(operation.AgentInstanceId, operation.OperationId, CancellationToken.None);
        if (current is { ProtectedUploadSession: not null, State: "Dispatched" or "Indeterminate" })
            await operations.UpdateAsync(current with { ProtectedUploadSession = null, Revision = current.Revision + 1 }, current.Revision, CancellationToken.None);
    }
    private static AgentCore.Application.Sessions.AgentCoreException ExpiredUploadFailure() => Failure(
        "drive_upload_session_expired", "The upload session expired. Inspect the known file and account access in Drive. Completion remains unknown; no replacement upload was started.", 409);

    private async Task<(bool Complete, int Offset)> InspectUpload(Uri session, int total, DriveOperation operation, CancellationToken ct)
    {
        try
        {
            using var response = await Send(() =>
            {
                var content = new ByteArrayContent([]);
                content.Headers.ContentRange = new ContentRangeHeaderValue(total);
                return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
            }, true, ct);
            return UploadProgress(response, total);
        }
        catch (AgentCore.Application.Sessions.AgentCoreException ex) when (ex.Code == "drive_not_found")
        {
            await ExpiredUpload(operation);
            throw ExpiredUploadFailure();
        }
    }

    public async ValueTask UploadAsync(string id, string name, string parent, string mime,
        ReadOnlyMemory<byte> bytes, DriveOperation operation, CancellationToken ct)
    {
        if (bytes.Length > GoogleDriveOptions.MaxTransferBytes)
            throw Failure("drive_transfer_too_large", "Drive file exceeds 50 MiB.", 413);
        if (!MediaTypeHeaderValue.TryParse(mime, out var uploadType) || uploadType.MediaType is null)
            throw new DriveEffectNotExecutedException("drive_invalid", "Upload media type is invalid; no content request was sent.");
        Uri session;
        var offset = 0;
        if (operation.ProtectedUploadSession is { } protectedSession)
        {
            session = UploadSession(protector.Unprotect(credential, 1, protectedSession));
            var progress = await InspectUpload(session, bytes.Length, operation, ct);
            if (progress.Complete) { await ReleaseCompletedUploadSession(operation); return; }
            offset = progress.Offset;
        }
        else
        {
            using var start = await Send(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id")
                { Content = Json(new { id, name, mimeType = uploadType.MediaType, parents = new[] { parent } }) };
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mime);
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
                return request;
            }, false, ct, effectStart: true);
            session = UploadSession(start.Headers.Location?.ToString() ?? "");
            // Persist authority before any content PUT; restart uses this exact session.
            await operations.UpdateAsync(operation with
            {
                ProtectedUploadSession = protector.Protect(credential, 1, session.AbsoluteUri),
                Revision = operation.Revision + 1
            }, operation.Revision, ct);
        }

        var recoveryChecks = 0;
        var stalled = 0;
        var remainingRequests = Math.Max(1, (bytes.Length + UploadChunkBytes - 1) / UploadChunkBytes) + MaxUploadRecoveryChecks + 1;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (--remainingRequests < 0)
                throw Failure("drive_effect_indeterminate", "The bounded upload attempt ended. Check or continue this operation; do not create another file.", 409);
            var length = Math.Min(UploadChunkBytes, bytes.Length - offset);
            (bool Complete, int Offset) progress;
            try
            {
                using var response = await Send(() =>
                {
                    var content = new ByteArrayContent(bytes.Slice(offset, length).ToArray());
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
                    content.Headers.ContentRange = length == 0 ? new ContentRangeHeaderValue(0)
                        : new ContentRangeHeaderValue(offset, offset + length - 1, bytes.Length);
                    return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
                }, false, ct);
                progress = UploadProgress(response, bytes.Length);
                if (!progress.Complete && progress.Offset > offset + length)
                    throw Failure("drive_provider_response_invalid", "Drive acknowledged bytes outside the sent chunk. No continuation was sent.", 502);
            }
            catch (AgentCore.Application.Sessions.AgentCoreException ex) when (ex.Code == "drive_not_found")
            {
                await ExpiredUpload(operation);
                throw ExpiredUploadFailure();
            }
            catch (AgentCore.Application.Sessions.AgentCoreException ex)
                when ((ex.Code is "drive_provider_unavailable" or "drive_rate_limited") && recoveryChecks < MaxUploadRecoveryChecks)
            {
                recoveryChecks++;
                // Observe accepted bytes before continuation, never replay a blind PUT.
                progress = await InspectUpload(session, bytes.Length, operation, ct);
            }
            if (progress.Complete) { await ReleaseCompletedUploadSession(operation); return; }
            if (progress.Offset < offset || progress.Offset == offset && ++stalled > MaxUploadRecoveryChecks)
                throw Failure("drive_effect_indeterminate", "Drive upload made no verified progress. Check or continue this operation; do not create another file.", 409);
            if (progress.Offset > offset) stalled = 0;
            offset = progress.Offset;
        } while (true);
    }
}
