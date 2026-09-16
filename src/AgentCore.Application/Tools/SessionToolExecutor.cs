using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed class SessionToolExecutor(
    RoleKnowledgeService? knowledge = null,
    IAttachmentStore? attachments = null,
    ISessionWorkspace? workspace = null,
    IArtifactStore? artifacts = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<string> ExecuteAsync(
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(call.Name)
            || !RolePermissions.AllowsTool(definition, call.Name)
            || !ToolCatalog.For(definition).Any(item => string.Equals(item.Name, call.Name, StringComparison.Ordinal)))
        {
            return Error("forbidden", "Tool is not permitted for this role.");
        }

        JsonElement args;
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                JsonOptions);
            if (args.ValueKind != JsonValueKind.Object)
            {
                return Error("invalid", "Tool arguments must be a JSON object.");
            }
        }
        catch (JsonException)
        {
            return Error("invalid", "Tool arguments were malformed.");
        }

        if (LooksLikeSessionMutation(args) || LooksLikeHostPath(args))
        {
            return Error("forbidden", "Tool arguments are not permitted.");
        }

        try
        {
            var raw = call.Name switch
            {
                ToolCatalog.KnowledgeRetrieve => await RetrieveKnowledgeAsync(definition, args, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.AttachmentsRead => await ReadAttachmentAsync(sessionId, args, remainingOutputBytes, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.WorkspaceRead => await ReadWorkspaceAsync(definition, sessionId, args, remainingOutputBytes, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.WorkspaceWrite => await WriteWorkspaceAsync(definition, sessionId, args, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.ArtifactsCreate => await CreateArtifactAsync(sessionId, args, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.ArtifactsVerify => await VerifyArtifactAsync(sessionId, args, cancellationToken)
                    .ConfigureAwait(false),
                _ => Error("forbidden", "Tool is not permitted for this role.")
            };
            return Clip(raw, remainingOutputBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCoreException ex)
        {
            return Error(ex.Code, ex.Message);
        }
    }

    private async Task<string> RetrieveKnowledgeAsync(
        AgentDefinition definition,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (knowledge is null || !TryString(args, "identity", out var identity))
        {
            return Error("invalid", "identity is required.");
        }

        var document = await knowledge.RetrieveAsync(definition, identity, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            identity = document.Identity,
            title = document.Title,
            citation = document.Citation,
            content = document.Content
        });
    }

    private async Task<string> ReadAttachmentAsync(
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (attachments is null || !TryString(args, "attachmentId", out var raw) || !Guid.TryParse(raw, out var attachmentId))
        {
            return Error("invalid", "attachmentId is required.");
        }

        var record = await attachments.GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Error("notFound", "Attachment was not found.");
        }

        await using var stream = await attachments.OpenContentAsync(sessionId, attachmentId, cancellationToken)
            .ConfigureAwait(false);
        var budget = Math.Max(0, Math.Min(remainingOutputBytes, ToolLimits.MaxOutputBytes));
        using var buffer = new MemoryStream();
        var block = new byte[Math.Min(8192, Math.Max(1, budget))];
        while (buffer.Length < budget)
        {
            var read = await stream.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, budget - buffer.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(block, 0, read);
        }

        var text = DecodeText(buffer.ToArray());
        return JsonSerializer.Serialize(new
        {
            attachmentId = record.AttachmentId,
            displayName = record.DisplayName,
            contentType = record.ContentType,
            truncated = buffer.Length < record.ByteSize,
            content = text
        });
    }

    private async Task<string> ReadWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (workspace is null || !TryString(args, "path", out var path))
        {
            return Error("invalid", "path is required.");
        }

        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var content = await workspace.ReadAsync(sessionId, definition, path, cancellationToken).ConfigureAwait(false);
        var take = Math.Min(content.Bytes.Length, Math.Max(0, remainingOutputBytes));
        return JsonSerializer.Serialize(new
        {
            path = content.LogicalPath,
            contentType = content.ContentType,
            truncated = take < content.Bytes.Length,
            content = DecodeText(content.Bytes.AsSpan(0, take).ToArray())
        });
    }

    private async Task<string> WriteWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null
            || !TryString(args, "path", out var path)
            || !TryString(args, "content", out var content))
        {
            return Error("invalid", "path and content are required.");
        }

        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(content);
        await workspace.WriteAsync(sessionId, path, bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { path, bytes = bytes.Length });
    }

    private async Task<string> CreateArtifactAsync(
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (artifacts is null || !TryString(args, "displayName", out var displayName) || !TryString(args, "content", out var content))
        {
            return Error("invalid", "displayName and content are required.");
        }

        TryString(args, "contentType", out var contentType);
        Guid? source = null;
        if (TryString(args, "sourceAttachmentId", out var sourceRaw) && Guid.TryParse(sourceRaw, out var parsed))
        {
            source = parsed;
        }

        var created = await artifacts.CreateAsync(
                sessionId,
                displayName,
                string.IsNullOrWhiteSpace(contentType) ? "text/markdown" : contentType,
                Encoding.UTF8.GetBytes(content),
                source,
                workspaceLogicalPath: null,
                cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            artifactId = created.ArtifactId,
            displayName = created.DisplayName,
            sha256Hex = created.Sha256Hex,
            sourceAttachmentId = created.SourceAttachmentId
        });
    }

    private async Task<string> VerifyArtifactAsync(
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (artifacts is null || !TryString(args, "artifactId", out var raw) || !Guid.TryParse(raw, out var artifactId))
        {
            return Error("invalid", "artifactId is required.");
        }

        var record = await artifacts.GetAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Error("notFound", "Artifact was not found.");
        }

        return JsonSerializer.Serialize(new
        {
            artifactId = record.ArtifactId,
            displayName = record.DisplayName,
            sha256Hex = record.Sha256Hex,
            sourceAttachmentId = record.SourceAttachmentId
        });
    }

    private static bool LooksLikeSessionMutation(JsonElement args)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (property.Name.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("mutate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("revision", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("persist", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("history", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeHostPath(JsonElement args)
    {
        if (!TryString(args, "path", out var path) && !TryString(args, "file", out path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            return true;
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        return Path.IsPathRooted(normalized) && !normalized.StartsWith('/');
    }

    private static bool TryString(JsonElement args, string name, out string value)
    {
        value = "";
        if (!args.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Convert.ToBase64String(bytes);
        }
    }

    private static string Clip(string value, int remainingOutputBytes)
    {
        var budget = Math.Max(0, remainingOutputBytes);
        var utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length <= budget)
        {
            return value;
        }

        return Encoding.UTF8.GetString(utf8.AsSpan(0, budget));
    }

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
