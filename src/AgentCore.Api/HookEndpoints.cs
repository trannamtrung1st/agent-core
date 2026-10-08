using AgentCore.Application.Events;

namespace AgentCore.Api;

public static class HookEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/hooks/{eventKey}", ReceiveAsync);
    }

    private static async Task<IResult> ReceiveAsync(
        string eventKey,
        HttpRequest request,
        ExternalEventIngress ingress,
        CancellationToken cancellationToken)
    {
        if (!TryBearer(request, out var token)
            || !await ingress.CredentialsMatchAsync(eventKey, token, cancellationToken).ConfigureAwait(false))
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (request.ContentLength is > ExternalEventEnvelope.MaxRawBytes)
        {
            return Error(StatusCodes.Status400BadRequest, "payload_too_large");
        }

        var body = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Error(StatusCodes.Status400BadRequest, "payload_too_large");
        }

        var result = await ingress.AdmitAsync(eventKey, token, body, cancellationToken).ConfigureAwait(false);
        return result.Kind switch
        {
            ExternalEventIngressKind.Admitted => Accepted(StatusCodes.Status202Accepted, result.EventId),
            ExternalEventIngressKind.Duplicate => Accepted(StatusCodes.Status200OK, result.EventId),
            ExternalEventIngressKind.Invalid => Error(StatusCodes.Status400BadRequest, result.Code ?? "invalid_payload"),
            _ => Error(StatusCodes.Status401Unauthorized, "unauthorized")
        };
    }

    private static IResult Accepted(int statusCode, Guid? eventId) =>
        Results.Json(new ExternalEventAcceptedResponse(eventId?.ToString("D") ?? ""), statusCode: statusCode);

    private static IResult Error(int statusCode, string error) =>
        Results.Json(new ExternalEventErrorResponse(error), statusCode: statusCode);

    private static bool TryBearer(HttpRequest request, out string token)
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

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
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

    private sealed record ExternalEventAcceptedResponse(string EventId);

    private sealed record ExternalEventErrorResponse(string Error);
}
