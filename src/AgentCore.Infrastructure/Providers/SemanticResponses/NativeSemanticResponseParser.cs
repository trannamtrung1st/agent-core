using System.Text.Json;
using AgentCore.Application.Memory;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Providers.SemanticResponses;

internal static class NativeSemanticResponseParser
{
    private static readonly ILogger SpeechNormalizationLogger =
        NullLogger.Instance;
    public static bool TryParse(
        string json,
        out ModelSemanticResponse? response,
        out string failureReason,
        ModelResponseContract? contract = null)
    {
        response = null;
        failureReason = ProviderFailureReason.InvalidJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            failureReason = ProviderFailureReason.ResponseFunctionArgumentsInvalid;
            return false;
        }

        if (json.Length > AssistantResponseSchema.MaxJsonCharacters)
        {
            failureReason = ProviderFailureReason.ResponseTooLarge;
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            failureReason = ProviderFailureReason.InvalidJson;
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureReason = ProviderFailureReason.InvalidJson;
                return false;
            }

            var root = document.RootElement;
            if (ContainsDestination(root))
            {
                failureReason = ProviderFailureReason.ModelSuppliedDestination;
                return false;
            }

            if (!TryDisposition(root, out var disposition, out failureReason))
            {
                return false;
            }

            if (!TryActionKind(root, out var actionKind, out var actionSpecified, out failureReason))
            {
                return false;
            }

            var noAction = actionSpecified && string.IsNullOrWhiteSpace(actionKind);
            if (!TryDisplay(root, noAction, out var display, out failureReason))
            {
                return false;
            }

            var speechWillBeUsed = contract?.SpeechWillBeUsed ?? true;
            if (!TrySpeech(root, noAction, speechWillBeUsed, out var speech, out failureReason))
            {
                return false;
            }

            if (!TryBlocks(root, out var blocks, out failureReason))
            {
                return false;
            }

            if (!TryMemory(root, out var memory, out failureReason))
            {
                return false;
            }

            response = new ModelSemanticResponse(
                display,
                speech,
                blocks,
                memory,
                disposition,
                actionKind,
                actionSpecified);
            failureReason = string.Empty;
            return true;
        }
    }

    private static bool ContainsDestination(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "sessionId" or "destination" or "profileId" or "tenant" or "recipient")
                    {
                        return true;
                    }

                    if (ContainsDestination(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsDestination(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool TryDisposition(JsonElement root, out string? disposition, out string failureReason)
    {
        disposition = null;
        failureReason = string.Empty;
        if (!root.TryGetProperty("disposition", out var dispositionEl))
        {
            return true;
        }

        if (dispositionEl.ValueKind != JsonValueKind.String)
        {
            failureReason = ProviderFailureReason.UnknownDisposition;
            return false;
        }

        var name = dispositionEl.GetString();
        if (name is "Continue" or "Wait" or "Complete" or "Blocked")
        {
            disposition = name;
            return true;
        }

        failureReason = ProviderFailureReason.UnknownDisposition;
        return false;
    }

    private static bool TryDisplay(JsonElement root, bool noAction, out string display, out string failureReason)
    {
        display = string.Empty;
        failureReason = ProviderFailureReason.MissingDisplayText;
        if (!root.TryGetProperty("displayText", out var displayEl))
        {
            return noAction;
        }

        if (displayEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        display = displayEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(display))
        {
            display = string.Empty;
            if (!noAction)
            {
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        if (display.Length > AssistantResponseSchema.MaxDisplayCharacters)
        {
            failureReason = ProviderFailureReason.ResponseTooLarge;
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool TryActionKind(
        JsonElement root,
        out string? actionKind,
        out bool actionSpecified,
        out string failureReason)
    {
        actionKind = null;
        actionSpecified = false;
        failureReason = string.Empty;
        string? single = null;
        var explicitNone = false;
        if (root.TryGetProperty("action", out var action))
        {
            actionSpecified = true;
            if (action.ValueKind == JsonValueKind.Null)
            {
                explicitNone = true;
            }
            else if (!TryReadKind(action, out single, out failureReason))
            {
                return false;
            }
            else if (single is null)
            {
                explicitNone = true;
            }
        }

        if (root.TryGetProperty("actions", out var actions))
        {
            actionSpecified = true;
            if (actions.ValueKind != JsonValueKind.Array)
            {
                failureReason = ProviderFailureReason.UnknownAction;
                return false;
            }

            var count = 0;
            foreach (var item in actions.EnumerateArray())
            {
                count++;
                if (!TryReadKind(item, out var kind, out failureReason))
                {
                    return false;
                }

                if (kind is null)
                {
                    explicitNone = true;
                }
                else if (single is not null && !string.Equals(single, kind, StringComparison.Ordinal))
                {
                    failureReason = ProviderFailureReason.UnknownAction;
                    return false;
                }
                else
                {
                    single = kind;
                }
            }

            if (count > 1)
            {
                failureReason = ProviderFailureReason.UnknownAction;
                return false;
            }

            if (count == 0 && single is null)
            {
                explicitNone = true;
            }
        }

        if (explicitNone && single is not null)
        {
            failureReason = ProviderFailureReason.UnknownAction;
            return false;
        }

        actionKind = single;
        return true;
    }

    private static bool TryReadKind(JsonElement action, out string? kind, out string failureReason)
    {
        kind = null;
        failureReason = ProviderFailureReason.UnknownAction;
        if (action.ValueKind == JsonValueKind.Null)
        {
            failureReason = string.Empty;
            return true;
        }

        if (action.ValueKind == JsonValueKind.String)
        {
            kind = action.GetString();
        }
        else if (action.ValueKind == JsonValueKind.Object
            && action.TryGetProperty("kind", out var kindEl)
            && kindEl.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            kind = kindEl.ValueKind == JsonValueKind.Null ? null : kindEl.GetString();
        }
        else
        {
            return false;
        }

        if (kind is null)
        {
            failureReason = string.Empty;
            return true;
        }

        if (!string.Equals(kind, "chat.respond", StringComparison.Ordinal))
        {
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool TryMemory(JsonElement root, out IReadOnlyList<MemoryProposal> memory, out string failureReason)
    {
        memory = [];
        failureReason = ProviderFailureReason.InvalidMemory;
        if (!root.TryGetProperty("memory", out var memoryEl))
        {
            failureReason = string.Empty;
            return true;
        }

        if (memoryEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var proposals = new List<MemoryProposal>();
        foreach (var item in memoryEl.EnumerateArray())
        {
            if (!MemoryProposalCodec.TryRead(item, out var proposal) || proposal is null)
            {
                failureReason = ProviderFailureReason.InvalidMemoryProposal;
                return false;
            }

            if (!MemoryProposalCodec.IsConversationalSource(proposal.Source))
            {
                continue;
            }

            proposals.Add(proposal);
        }

        memory = proposals;
        failureReason = string.Empty;
        return true;
    }

    private static bool TrySpeech(
        JsonElement root,
        bool noAction,
        bool speechWillBeUsed,
        out ModelSpeechProjection speech,
        out string failureReason)
    {
        speech = new ModelSpeechProjection(ModelSpeechMode.Same, null);
        if (!speechWillBeUsed)
        {
            if (TryRecognizeSpeech(root, out var recognized, out var customText)
                && (recognized != ModelSpeechMode.Custom || !string.IsNullOrWhiteSpace(customText))
                && (customText is null || customText.Length <= AssistantResponseSchema.MaxSpeechCharacters))
            {
                speech = new ModelSpeechProjection(
                    recognized,
                    recognized == ModelSpeechMode.Custom ? customText : null);
            }

            failureReason = string.Empty;
            return true;
        }

        failureReason = ProviderFailureReason.InvalidSpeech;
        if (!root.TryGetProperty("speech", out var speechEl))
        {
            if (noAction)
            {
                speech = new ModelSpeechProjection(ModelSpeechMode.None, null);
                failureReason = string.Empty;
                return true;
            }

            speech = new ModelSpeechProjection(ModelSpeechMode.Same, null);
            failureReason = string.Empty;
            NoteSpeechNormalized(ProviderFailureReason.SpeechOmitted);
            return true;
        }

        if (speechEl.ValueKind == JsonValueKind.Null)
        {
            speech = new ModelSpeechProjection(ModelSpeechMode.None, null);
            failureReason = string.Empty;
            NoteSpeechNormalized(ProviderFailureReason.SpeechMalformed);
            return true;
        }

        if (speechEl.ValueKind != JsonValueKind.Object
            || !speechEl.TryGetProperty("mode", out var modeEl)
            || modeEl.ValueKind != JsonValueKind.String)
        {
            speech = new ModelSpeechProjection(ModelSpeechMode.None, null);
            failureReason = string.Empty;
            NoteSpeechNormalized(ProviderFailureReason.SpeechMalformed);
            return true;
        }

        var modeText = modeEl.GetString();
        if (string.Equals(modeText, "same", StringComparison.OrdinalIgnoreCase))
        {
            speech = new ModelSpeechProjection(ModelSpeechMode.Same, null);
            failureReason = string.Empty;
            return true;
        }

        if (string.Equals(modeText, "none", StringComparison.OrdinalIgnoreCase))
        {
            speech = new ModelSpeechProjection(ModelSpeechMode.None, null);
            failureReason = string.Empty;
            return true;
        }

        if (!string.Equals(modeText, "custom", StringComparison.OrdinalIgnoreCase)
            || !TrySpeechText(speechEl, out var text)
            || string.IsNullOrWhiteSpace(text)
            || text.Length > AssistantResponseSchema.MaxSpeechCharacters)
        {
            speech = new ModelSpeechProjection(ModelSpeechMode.None, null);
            failureReason = string.Empty;
            NoteSpeechNormalized(ProviderFailureReason.SpeechMalformed);
            return true;
        }

        speech = new ModelSpeechProjection(ModelSpeechMode.Custom, text);
        failureReason = string.Empty;
        return true;
    }

    private static void NoteSpeechNormalized(string reason)
    {
        DiagnosticLog.Warning(
            SpeechNormalizationLogger,
            new InvalidOperationException("Assistant speech projection was normalized."),
            Guid.NewGuid(),
            "Assistant speech projection was normalized.",
            new DiagnosticContext(FailureReason: reason, ProviderResponseChannel: ProviderResponseChannel.ResponseFunction));
    }

    private static bool TryRecognizeSpeech(JsonElement root, out ModelSpeechMode mode, out string? customText)
    {
        mode = ModelSpeechMode.Same;
        customText = null;
        if (!root.TryGetProperty("speech", out var speechEl) || speechEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!speechEl.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var modeText = modeEl.GetString();
        if (string.Equals(modeText, "same", StringComparison.OrdinalIgnoreCase))
        {
            mode = ModelSpeechMode.Same;
            return true;
        }

        if (string.Equals(modeText, "none", StringComparison.OrdinalIgnoreCase))
        {
            mode = ModelSpeechMode.None;
            return true;
        }

        if (!string.Equals(modeText, "custom", StringComparison.OrdinalIgnoreCase)
            || !TrySpeechText(speechEl, out customText))
        {
            return false;
        }

        mode = ModelSpeechMode.Custom;
        return true;
    }

    private static bool TrySpeechText(JsonElement speechEl, out string? text)
    {
        text = null;
        if (!speechEl.TryGetProperty("text", out var textEl)
            || textEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (textEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = textEl.GetString();
        return true;
    }

    private static bool TryBlocks(JsonElement root, out IReadOnlyList<ModelResponseBlock> blocks, out string failureReason)
    {
        blocks = [];
        failureReason = ProviderFailureReason.InvalidBlocks;
        if (!root.TryGetProperty("blocks", out var blocksEl))
        {
            failureReason = string.Empty;
            return true;
        }

        if (blocksEl.ValueKind == JsonValueKind.Null)
        {
            failureReason = string.Empty;
            return true;
        }

        if (blocksEl.ValueKind != JsonValueKind.Array || blocksEl.GetArrayLength() > AssistantResponseSchema.MaxBlocks)
        {
            return false;
        }

        var listed = new List<ModelResponseBlock>(blocksEl.GetArrayLength());
        foreach (var item in blocksEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("kind", out var kindEl)
                || kindEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var kindText = kindEl.GetString();
            var text = OptionalString(item, "text");
            var attachmentId = OptionalString(item, "attachmentId");
            var artifactId = OptionalString(item, "artifactId");
            if (text is { Length: > AssistantResponseSchema.MaxBlockCharacters }
                || attachmentId is { Length: > AssistantResponseSchema.MaxBlockCharacters }
                || artifactId is { Length: > AssistantResponseSchema.MaxBlockCharacters })
            {
                failureReason = ProviderFailureReason.ResponseTooLarge;
                return false;
            }

            if (string.Equals(kindText, "markdown", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                listed.Add(new ModelResponseBlock(ModelResponseBlockKind.Markdown, text));
            }
            else if (string.Equals(kindText, "attachmentReference", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(attachmentId))
                {
                    return false;
                }

                listed.Add(new ModelResponseBlock(ModelResponseBlockKind.AttachmentReference, text, attachmentId));
            }
            else if (string.Equals(kindText, "artifactReference", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(artifactId))
                {
                    return false;
                }

                listed.Add(new ModelResponseBlock(ModelResponseBlockKind.ArtifactReference, text, ArtifactId: artifactId));
            }
            else
            {
                return false;
            }
        }

        blocks = listed;
        failureReason = string.Empty;
        return true;
    }

    private static string? OptionalString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
