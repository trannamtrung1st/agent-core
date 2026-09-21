using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed class SessionToolExecutor(
    RoleKnowledgeService? knowledge = null,
    IAttachmentStore? attachments = null,
    IAttachmentProcessor? processor = null,
    ISessionWorkspace? workspace = null,
    IArtifactStore? artifacts = null,
    ISandboxExecutor? sandbox = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(call.Name)
            || !ToolCatalog.IsPermittedForExecution(definition, call.Name)
            || (!string.Equals(call.Name, ToolCatalog.AttachmentsRead, StringComparison.Ordinal)
                && !ToolCatalog.For(definition).Any(item => string.Equals(item.Name, call.Name, StringComparison.Ordinal))))
        {
            return TextResult(Error("forbidden", "Tool is not permitted for this role."));
        }

        JsonElement args;
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                JsonOptions);
            if (args.ValueKind != JsonValueKind.Object)
            {
                return TextResult(Error("invalid", "Tool arguments must be a JSON object."));
            }
        }
        catch (JsonException)
        {
            return TextResult(Error("invalid", "Tool arguments were malformed."));
        }

        if (LooksLikeSessionMutation(args) || LooksLikeHostPath(args))
        {
            return TextResult(Error("forbidden", "Tool arguments are not permitted."));
        }

        try
        {
            var raw = call.Name switch
            {
                ToolCatalog.KnowledgeRetrieve => await RetrieveKnowledgeAsync(definition, args, remainingOutputBytes, cancellationToken)
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
                ToolCatalog.SandboxRun => await RunSandboxAsync(definition, sessionId, args, remainingOutputBytes, cancellationToken)
                    .ConfigureAwait(false),
                _ => Error("forbidden", "Tool is not permitted for this role.")
            };
            return FitResult(remainingOutputBytes, raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCoreException ex)
        {
            return FitResult(remainingOutputBytes, Error(ex.Code, ex.Message));
        }
    }

    private static ToolExecutionResult TextResult(string text) => ToolExecutionResult.FromText(text);

    private static ToolExecutionResult FitResult(int remainingOutputBytes, string raw) =>
        TextResult(ToolJsonResults.FitToBudget(remainingOutputBytes, raw));

    private async Task<string> RetrieveKnowledgeAsync(
        AgentDefinition definition,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (knowledge is null || !TryString(args, "identity", out var identity))
        {
            return Error("invalid", "identity is required.");
        }

        var document = await knowledge.RetrieveAsync(definition, identity, cancellationToken).ConfigureAwait(false);
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            document.Content,
            (content, truncated) => JsonSerializer.Serialize(new
            {
                identity = document.Identity,
                title = document.Title,
                citation = document.Citation,
                content,
                truncated
            }));
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

        if (AttachmentMedia.IsImage(record.ContentType))
        {
            return JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                byteSize = record.ByteSize,
                kind = "image",
                note = "Binary image bytes are already attached on the user turn for vision. Use that multimodal content; do not treat raw bytes as text."
            });
        }

        if (string.Equals(AttachmentMedia.NormalizeContentType(record.ContentType), "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (processor is null)
            {
                return JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind = "pdf",
                    note = "PDF extraction is unavailable."
                });
            }

            var processed = await processor.ProcessTurnAsync(sessionId, [attachmentId], cancellationToken)
                .ConfigureAwait(false);
            var extracted = processed.FirstOrDefault(item => item.Kind == AttachmentProcessKind.ExtractedText);
            if (extracted is null || string.IsNullOrEmpty(extracted.Text))
            {
                return JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind = "pdf",
                    note = "PDF content could not be extracted."
                });
            }

            return ToolJsonResults.FitJsonWithContentField(
                remainingOutputBytes,
                extracted.Text,
                (content, truncated) => JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    kind = "pdf",
                    provenance = extracted.Provenance,
                    truncated,
                    content
                }));
        }

        if (!AttachmentMedia.IsReadableText(record.ContentType))
        {
            return JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                byteSize = record.ByteSize,
                kind = "binary",
                note = "This attachment type is not readable as UTF-8 text through attachments.read."
            });
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
        var byteTruncated = buffer.Length < record.ByteSize;
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            text,
            (content, truncated) => JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                truncated = byteTruncated || truncated,
                content
            }));
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
        var decoded = DecodeText(content.Bytes.AsSpan(0, take).ToArray());
        var byteTruncated = take < content.Bytes.Length;
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            decoded,
            (body, truncated) => JsonSerializer.Serialize(new
            {
                path = content.LogicalPath,
                contentType = content.ContentType,
                truncated = byteTruncated || truncated,
                content = body
            }));
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

        var created = await artifacts.CreateAsync(
                sessionId,
                displayName,
                string.IsNullOrWhiteSpace(contentType) ? "text/markdown" : contentType,
                Encoding.UTF8.GetBytes(content),
                sourceAttachmentId: null,
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

    private async Task<string> RunSandboxAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (sandbox is null)
        {
            return Error("unavailable", "Container sandbox is unavailable.");
        }

        if (!TryString(args, "verb", out var verb))
        {
            return Error("invalid", "verb is required.");
        }

        var arguments = new List<string>();
        if (args.TryGetProperty("arguments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return Error("invalid", "arguments must be strings.");
                }

                arguments.Add(item.GetString() ?? "");
            }
        }

        TryString(args, "exportPath", out var export);
        var started = Stopwatch.GetTimestamp();
        var result = await sandbox.RunAsync(
                new SandboxRequest(
                    sessionId,
                    Guid.CreateVersion7(),
                    definition,
                    verb,
                    arguments,
                    string.IsNullOrWhiteSpace(export) ? null : export),
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("sandbox", RuntimeTelemetry.ElapsedMs(started), verb);
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            result.Output,
            (output, truncated) => JsonSerializer.Serialize(new
            {
                ok = result.Succeeded,
                exitCode = result.ExitCode,
                output,
                truncated = truncated || result.Truncated,
                artifactId = result.ArtifactId,
                message = result.SafeMessage
            }));
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

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Convert.ToBase64String(bytes);
        }
    }

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
