using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Work;

public static class WorkCaptureRehydration
{
    public const string Unavailable =
        """{"error":"capture_unavailable","message":"The captured image is no longer available. Capture the page again before using the picture."}""";

    public static async ValueTask<List<ModelMessage>> ApplyAsync(
        List<ModelMessage> messages,
        IWorkCaptureStore? captures,
        CancellationToken cancellationToken)
    {
        if (captures is null)
        {
            return messages;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != ModelRole.Tool
                || !string.Equals(message.Name, ToolCatalog.BrowserCapture, StringComparison.Ordinal)
                || message.Parts?.OfType<ModelImageContent>().Any() == true
                || !TryReadArtifactId(message.Text, out var captureId))
            {
                continue;
            }

            var content = await captures.ReadAsync(captureId, cancellationToken).ConfigureAwait(false);
            messages[index] = content is null
                ? message with { Text = Unavailable, Parts = null }
                : message with
                {
                    Parts = [new ModelImageContent(content.Capture.ContentType, content.Bytes, "capture.png")]
                };
        }

        return messages;
    }

    private static bool TryReadArtifactId(string text, out Guid captureId)
    {
        captureId = default;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (!document.RootElement.TryGetProperty("artifactId", out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return Guid.TryParse(property.GetString(), out captureId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
