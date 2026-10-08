using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Execution;

public static class SessionCaptureRehydration
{
    public const string Unavailable = """{"error":"capture_unavailable","message":"The captured image is unavailable. Capture the page again before using it."}""";
    public static async ValueTask<List<ModelMessage>> ApplyAsync(Guid sessionId, List<ModelMessage> messages, IArtifactStore? artifacts, CancellationToken ct)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != ModelRole.Tool || message.Name != ToolCatalog.BrowserScreenshot || message.Parts?.OfType<ModelImageContent>().Any() == true
                || !ReadId(message.Text, out var id)) continue;
            var record = artifacts is null ? null : await artifacts.GetAsync(sessionId, id, ct).ConfigureAwait(false);
            if (record is null || record.SessionId != sessionId || record.ArtifactId != id || !record.ContentType.StartsWith("image/", StringComparison.Ordinal)
                || record.ByteSize < 0 || record.ByteSize > BrowserToolLimits.MaxDownloadBytes)
            { messages[index] = message with { Text = Unavailable, Parts = null }; continue; }
            try
            {
                await using var content = await artifacts!.OpenContentAsync(sessionId, id, ct).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[8192];
                while (bytes.Length <= BrowserToolLimits.MaxDownloadBytes)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, BrowserToolLimits.MaxDownloadBytes + 1 - bytes.Length)), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await bytes.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                if (bytes.Length != record.ByteSize || bytes.Length > BrowserToolLimits.MaxDownloadBytes)
                { messages[index] = message with { Text = Unavailable, Parts = null }; continue; }
                var payload = bytes.ToArray();
                if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), record.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                { messages[index] = message with { Text = Unavailable, Parts = null }; continue; }
                messages[index] = message with { Parts = [new ModelImageContent(record.ContentType, payload, record.DisplayName)] };
            }
            catch (Exception exception) when (exception is AgentCoreException or IOException)
            { messages[index] = message with { Text = Unavailable, Parts = null }; }
        }
        return messages;
    }
    private static bool ReadId(string text, out Guid id)
    {
        id = default;
        try { using var data = JsonDocument.Parse(text); return data.RootElement.ValueKind == JsonValueKind.Object
            && data.RootElement.TryGetProperty("artifactId", out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out id); }
        catch (JsonException) { return false; }
    }
}
