using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Agents;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed class SessionToolExecutor(
    RoleKnowledgeService? knowledge = null,
    IAttachmentStore? attachments = null,
    IAttachmentProcessor? processor = null,
    ISessionWorkspace? workspace = null,
    IArtifactStore? artifacts = null,
    ISandboxExecutor? sandbox = null,
    IWebSearchProvider? webSearch = null,
    IPublicWebFetcher? publicWebFetcher = null,
    IToolConfigurationGate? configurationGate = null)
{
    private readonly IToolConfigurationGate _configurationGate =
        configurationGate ?? ToolConfigurationGates.Unconfigured;

    public ToolPolicyDecision EvaluateExecutionPolicy(
        AgentDefinition definition,
        string toolName,
        ToolApprovalGrant? grant = null) =>
        ToolPolicy.EvaluateExecution(definition, toolName, _configurationGate, grant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes,
        CancellationToken cancellationToken = default,
        ToolApprovalGrant? approvalGrant = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = ToolPolicy.EvaluateExecution(definition, call.Name, _configurationGate, approvalGrant);
        if (policy == ToolPolicyDecision.Deny
            || string.IsNullOrWhiteSpace(call.Name))
        {
            return TextResult(Error("forbidden", "Tool is not permitted for this role."));
        }

        if (policy == ToolPolicyDecision.RequireApproval)
        {
            return TextResult(Error("approval_required", "Tool execution requires explicit approval."));
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

        if (approvalGrant is not null)
        {
            var boundHash = ToolActionHash.Compute(call.Name, args);
            if (!string.Equals(boundHash, approvalGrant.ActionHash, StringComparison.Ordinal)
                || !string.Equals(call.Name, approvalGrant.ToolName, StringComparison.Ordinal))
            {
                return TextResult(Error("stale_approval", "Approval no longer matches the requested action."));
            }
        }

        try
        {
            return call.Name switch
            {
                ToolCatalog.AttachmentsRead => await ReadAttachmentAsync(sessionId, args, remainingOutputBytes, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.KnowledgeRetrieve => FitResult(
                    remainingOutputBytes,
                    await RetrieveKnowledgeAsync(definition, args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceRead => FitResult(
                    remainingOutputBytes,
                    await ReadWorkspaceAsync(definition, sessionId, args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceList => TextResult(
                    await ListWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceWrite => TextResult(
                    await WriteWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspacePatch => TextResult(
                    await PatchWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.ArtifactsCreate => TextResult(
                    await CreateArtifactAsync(sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.ArtifactsCreateFromWorkspace => TextResult(
                    await CreateArtifactFromWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.ArtifactsVerify => TextResult(
                    await VerifyArtifactAsync(sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.SandboxRun => FitResult(
                    remainingOutputBytes,
                    await RunSandboxAsync(definition, sessionId, args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WebSearch => FitResult(
                    remainingOutputBytes,
                    await SearchWebAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WebFetch => FitResult(
                    remainingOutputBytes,
                    await FetchWebAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.DemoSensitiveAction => TextResult(
                    ExecuteDemoSensitiveAction(sessionId, args, approvalGrant)),
                _ => TextResult(Error("forbidden", "Tool is not permitted for this role."))
            };
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

    private async Task<ToolExecutionResult> ReadAttachmentAsync(
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (attachments is null || !TryString(args, "attachmentId", out var raw) || !Guid.TryParse(raw, out var attachmentId))
        {
            return TextResult(Error("invalid", "attachmentId is required."));
        }

        var record = await attachments.GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return TextResult(Error("notFound", "Attachment was not found."));
        }

        if (AttachmentMedia.IsImage(record.ContentType))
        {
            return await ReadHistoricalImageAsync(sessionId, record, remainingOutputBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(AttachmentMedia.NormalizeContentType(record.ContentType), "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (processor is null)
            {
                return TextResult(JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind = "pdf",
                    note = "PDF extraction is unavailable."
                }));
            }

            var processed = await processor.ProcessTurnAsync(sessionId, [attachmentId], cancellationToken)
                .ConfigureAwait(false);
            var extracted = processed.FirstOrDefault(item => item.Kind == AttachmentProcessKind.ExtractedText);
            if (extracted is null || string.IsNullOrEmpty(extracted.Text))
            {
                return TextResult(JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind = "pdf",
                    note = "PDF content could not be extracted."
                }));
            }

            return TextResult(ToolJsonResults.FitJsonWithContentField(
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
                })));
        }

        if (!AttachmentMedia.IsReadableText(record.ContentType))
        {
            return TextResult(JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                byteSize = record.ByteSize,
                kind = "binary",
                note = "This attachment type is not readable as UTF-8 text through attachments.read."
            }));
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
        return TextResult(ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            text,
            (content, truncated) => JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                truncated = byteTruncated || truncated,
                content
            })));
    }

    private async Task<ToolExecutionResult> ReadHistoricalImageAsync(
        Guid sessionId,
        AttachmentRecord record,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (processor is null)
        {
            return TextResult(AttachmentProcessingFailed());
        }

        var processed = await processor.ProcessTurnAsync(sessionId, [record.AttachmentId], cancellationToken)
            .ConfigureAwait(false);
        var image = processed.FirstOrDefault(item =>
            item.AttachmentId == record.AttachmentId && item.Kind == AttachmentProcessKind.Image);
        if (image?.StrippedImage is not { Length: > 0 } sanitized)
        {
            return TextResult(AttachmentProcessingFailed());
        }

        var metadata = JsonSerializer.Serialize(new
        {
            attachmentId = record.AttachmentId,
            displayName = record.DisplayName,
            contentType = image.ContentType,
            kind = "image",
            processorVersion = image.ProcessorVersion,
            contentProvided = true
        });
        if (Encoding.UTF8.GetByteCount(metadata) > Math.Max(0, remainingOutputBytes))
        {
            return TextResult(ToolJsonResults.FitToBudget(
                remainingOutputBytes,
                JsonSerializer.Serialize(new
                {
                    error = "output_limit",
                    message = "Image metadata could not fit the remaining tool output budget."
                })));
        }

        return new ToolExecutionResult(
            metadata,
            [new ModelImageContent(image.ContentType, sanitized, image.DisplayName)]);
    }

    private static string AttachmentProcessingFailed() =>
        JsonSerializer.Serialize(new
        {
            error = "attachment_processing_failed",
            message = "The image could not be prepared for model input."
        });

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

    private async Task<string> ListWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            return Error("unavailable", "Workspace is unavailable.");
        }

        var path = "/workspace/working";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            path = pathElement.GetString() ?? path;
        }

        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var nodes = await workspace.ListAsync(sessionId, definition, path, cancellationToken).ConfigureAwait(false);
        var truncated = nodes.Count > WorkspaceLimits.MaxListEntries;
        var slice = truncated ? nodes.Take(WorkspaceLimits.MaxListEntries).ToArray() : nodes;
        return JsonSerializer.Serialize(new
        {
            path,
            truncated,
            entries = slice.Select(node => new
            {
                path = node.LogicalPath,
                directory = node.Directory,
                byteSize = node.ByteSize,
                writable = node.Writable
            })
        });
    }

    private async Task<string> PatchWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null
            || !TryString(args, "path", out var path)
            || !TryString(args, "expectedSha256", out var expectedSha256)
            || !args.TryGetProperty("edits", out var editsElement)
            || editsElement.ValueKind != JsonValueKind.Array)
        {
            return Error("invalid", "path, expectedSha256, and edits are required.");
        }

        if (editsElement.GetArrayLength() > WorkspaceLimits.MaxPatchEdits)
        {
            return Error("invalid", "At most 32 edits are permitted per patch.");
        }

        var edits = new List<WorkspaceTextEdit>();
        foreach (var item in editsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryString(item, "oldText", out var oldText)
                || !TryString(item, "newText", out var newText))
            {
                return Error("invalid", "Each edit requires oldText and newText.");
            }

            if (string.IsNullOrEmpty(oldText))
            {
                return Error("invalid", "Each edit oldText must be non-empty.");
            }

            edits.Add(new WorkspaceTextEdit(oldText, newText));
        }

        if (edits.Count == 0)
        {
            return Error("invalid", "At least one edit is required.");
        }

        var result = await workspace.PatchTextAsync(
                sessionId,
                definition,
                path,
                expectedSha256,
                edits,
                cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            path = result.LogicalPath,
            previousSha256 = result.PreviousSha256Hex,
            newSha256 = result.NewSha256Hex,
            byteSize = result.ByteSize,
            editsApplied = result.EditsApplied
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
            contentType = record.ContentType,
            byteSize = record.ByteSize,
            sha256Hex = record.Sha256Hex,
            sourceAttachmentId = record.SourceAttachmentId,
            workspaceLogicalPath = record.WorkspaceLogicalPath,
            createdAt = record.CreatedAt
        });
    }

    private async Task<string> CreateArtifactFromWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null || artifacts is null || !TryString(args, "path", out var path) || !TryString(args, "displayName", out var displayName))
        {
            return Error("invalid", "path and displayName are required.");
        }

        if (!path.StartsWith("/workspace/", StringComparison.Ordinal))
        {
            return Error("forbidden", "Only session-local /workspace paths are permitted.");
        }

        TryString(args, "contentType", out var contentType);
        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var content = await workspace.ReadAsync(sessionId, definition, path, cancellationToken).ConfigureAwait(false);
        var created = await artifacts.CreateAsync(
                sessionId,
                displayName,
                string.IsNullOrWhiteSpace(contentType) ? content.ContentType : contentType,
                content.Bytes,
                sourceAttachmentId: null,
                workspaceLogicalPath: content.LogicalPath,
                cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            artifactId = created.ArtifactId,
            displayName = created.DisplayName,
            contentType = created.ContentType,
            byteSize = created.ByteSize,
            sha256Hex = created.Sha256Hex,
            workspaceLogicalPath = created.WorkspaceLogicalPath
        });
    }

    private async Task<string> SearchWebAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (webSearch is null || !webSearch.IsAvailable)
        {
            return Error("unavailable", "Web search is not configured.");
        }

        if (!TryString(args, "query", out var query))
        {
            return Error("invalid", "query is required.");
        }

        if (query.Length > WebToolLimits.MaxSearchQueryLength)
        {
            return Error("invalid", $"query must be at most {WebToolLimits.MaxSearchQueryLength} characters.");
        }

        var limit = WebToolLimits.DefaultSearchLimit;
        if (args.TryGetProperty("limit", out var limitProperty))
        {
            if (limitProperty.ValueKind != JsonValueKind.Number || !limitProperty.TryGetInt32(out limit))
            {
                return Error("invalid", "limit must be an integer.");
            }
        }

        limit = Math.Clamp(limit, 1, WebToolLimits.MaxSearchLimit);
        var started = Stopwatch.GetTimestamp();
        var result = await webSearch
            .SearchAsync(new WebSearchRequest(query, limit), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("web.search", RuntimeTelemetry.ElapsedMs(started));
        var items = result.Results.Select(item => new
        {
            title = item.Title,
            url = item.Url,
            snippet = item.Snippet
        }).ToArray();
        return ToolJsonResults.FitToBudget(
            remainingOutputBytes,
            JsonSerializer.Serialize(new
            {
                untrustedWebContent = true,
                query,
                results = items,
                truncated = result.Truncated
            }));
    }

    private async Task<string> FetchWebAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (publicWebFetcher is null)
        {
            return Error("unavailable", "Web fetch is not configured.");
        }

        if (!TryString(args, "url", out var urlText))
        {
            return Error("invalid", "url is required.");
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return Error("invalid", "url must be an absolute http or https URL.");
        }

        var started = Stopwatch.GetTimestamp();
        var result = await publicWebFetcher
            .FetchAsync(new PublicWebFetchRequest(url), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("web.fetch", RuntimeTelemetry.ElapsedMs(started));
        if (result.ErrorCode is not null)
        {
            return JsonSerializer.Serialize(new
            {
                untrustedWebContent = true,
                finalUrl = result.FinalUrl,
                error = result.ErrorCode,
                message = result.ErrorMessage
            });
        }

        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            result.Text,
            (text, truncated) => JsonSerializer.Serialize(new
            {
                untrustedWebContent = true,
                finalUrl = result.FinalUrl,
                contentType = result.ContentType,
                text,
                truncated = truncated || result.Truncated
            }));
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

    private static string ExecuteDemoSensitiveAction(
        Guid sessionId,
        JsonElement args,
        ToolApprovalGrant? approvalGrant)
    {
        if (approvalGrant is null)
        {
            return Error("approval_required", "Tool execution requires explicit approval.");
        }

        if (!TryString(args, "label", out var label))
        {
            return Error("invalid", "label is required.");
        }

        var actionHash = ToolActionHash.Compute(ToolCatalog.DemoSensitiveAction, args);
        if (!string.Equals(actionHash, approvalGrant.ActionHash, StringComparison.Ordinal))
        {
            return Error("stale_approval", "Approval no longer matches the requested action.");
        }

        return DemoSensitiveActionStore.TryExecute(sessionId, approvalGrant.ApprovalId, label, out var result)
            ? result
            : result;
    }

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
