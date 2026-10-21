using AgentCore.Application.Events;

namespace AgentCore.Api.Http;

internal static class WebhookHttp
{
    internal static bool TryBearer(HttpRequest request, out string token)
    {
        token = "";
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal) || header.Length == prefix.Length)
        {
            return false;
        }

        token = header[prefix.Length..].Trim();
        return token.Length > 0 && !token.Contains(' ', StringComparison.Ordinal);
    }

    internal static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[ExternalEventEnvelope.MaxRawBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await body.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read > ExternalEventEnvelope.MaxRawBytes ? null : buffer[..read];
    }

}
