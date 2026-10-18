using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class BrowserCommandIdentity
{
    public static BrowserCommand Freeze(BrowserCommand command) => command switch
    {
        BrowserClick click => click with { Modifiers = click.Modifiers?.ToArray() },
        BrowserFillForm form => form with { Fields = form.Fields.ToArray() },
        BrowserSelectOption select => select with { Values = select.Values.ToArray() },
        BrowserUploadCommand upload => upload with { ArtifactIds = upload.ArtifactIds?.ToArray(),
            Uploads = upload.Uploads?.Select(item => item with { Content = item.Content.ToArray() }).ToArray() },
        _ => command // Remaining closed-family records contain immutable scalar/target values.
    };

    public static string Hash(BrowserCommand command)
    {
        // Bind the actual upload bytes without materializing them again as base64 JSON.
        var bytes = command is BrowserUploadCommand upload
            ? JsonSerializer.SerializeToUtf8Bytes(new { upload.Kind, upload.Target, upload.ArtifactIds,
                uploads = upload.Uploads?.Select(item => new { item.FileName, item.MediaType, length = item.Content.Length,
                    hash = Convert.ToHexStringLower(SHA256.HashData(item.Content.Span)) }).ToArray() }, BrowserToolArguments.JsonOptions)
            : JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), BrowserToolArguments.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
