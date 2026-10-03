using AgentCore.Application.Triggers;

namespace AgentCore.Api;

internal static class HookEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/hooks/{webhookKey:guid}/order-placed", ReceiveAsync);
    }

    private static async Task<IResult> ReceiveAsync(
        Guid webhookKey,
        HttpRequest request,
        OrderPlacedWebhook webhook,
        CancellationToken cancellationToken)
    {
        var token = Bearer(request);
        if (!await webhook.CredentialsMatchAsync(webhookKey, token, cancellationToken).ConfigureAwait(false))
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (request.ContentLength is > OrderPlacedPayload.MaxRawBytes)
        {
            return Error(StatusCodes.Status400BadRequest, "payload_too_large");
        }

        var body = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Error(StatusCodes.Status400BadRequest, "payload_too_large");
        }

        var result = await webhook.AdmitAsync(webhookKey, token, body, cancellationToken).ConfigureAwait(false);
        return result.Kind switch
        {
            OrderPlacedAdmissionKind.Admitted => Accepted(StatusCodes.Status202Accepted, result.OccurrenceId),
            OrderPlacedAdmissionKind.Duplicate => Accepted(StatusCodes.Status200OK, result.OccurrenceId),
            OrderPlacedAdmissionKind.NotAdmitted => Error(StatusCodes.Status403Forbidden, "not_admitted"),
            OrderPlacedAdmissionKind.ModelRejected => Error(
                StatusCodes.Status409Conflict,
                result.Code is "model-unavailable" or "model-capability-unsupported" ? result.Code : "model-unavailable"),
            OrderPlacedAdmissionKind.Unauthorized => Error(StatusCodes.Status401Unauthorized, "unauthorized"),
            _ => Error(StatusCodes.Status400BadRequest, "invalid_payload")
        };
    }

    private static IResult Accepted(int statusCode, Guid? occurrenceId) =>
        Results.Json(new OrderPlacedAcceptedResponse(occurrenceId?.ToString("D") ?? ""), statusCode: statusCode);

    private static IResult Error(int statusCode, string error) =>
        Results.Json(new OrderPlacedErrorResponse(error), statusCode: statusCode);

    private static string Bearer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var value))
        {
            return "";
        }

        var header = value.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.Ordinal)
            ? header[prefix.Length..].Trim()
            : "";
    }

    private static async ValueTask<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[OrderPlacedPayload.MaxRawBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read > OrderPlacedPayload.MaxRawBytes ? null : buffer[..read];
    }

    private sealed record OrderPlacedAcceptedResponse(string OccurrenceId);

    private sealed record OrderPlacedErrorResponse(string Error);
}
