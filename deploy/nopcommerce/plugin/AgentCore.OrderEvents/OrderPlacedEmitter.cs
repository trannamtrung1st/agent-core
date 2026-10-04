using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentCore.OrderEvents;

public static class OrderPlacedEmitter
{
    public const string EventType = "order.placed";

    public const int MaxAttempts = 3;

    public static string EventIdForOrder(int orderId)
    {
        if (orderId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderId));
        }

        return $"order-{orderId}-placed";
    }

    public static string BuildEnvelope(string eventId, string orderReference, DateTimeOffset occurredAt)
    {
        RequireField(eventId, nameof(eventId));
        RequireField(orderReference, nameof(orderReference));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("eventId", eventId);
            writer.WriteString("type", EventType);
            writer.WriteString("occurredAt", occurredAt.ToUniversalTime().ToString("o"));
            writer.WriteStartObject("data");
            writer.WriteString("orderReference", orderReference);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static async Task<bool> TryDeliverAsync(
        HttpClient http,
        string? url,
        string? token,
        string envelope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(envelope, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
            }

            if (attempt == MaxAttempts)
            {
                return false;
            }
        }

        return false;
    }

    private static void RequireField(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Event fields must be 1..64 characters without spaces.", name);
        }
    }
}
