using System.Text.Json;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.SemanticResponses;

internal static class NativeSemanticResponseParser
{
    public static bool TryParse(string json, out ModelSemanticResponse? response, out string failureReason)
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
            if (!root.TryGetProperty("displayText", out var displayEl) || displayEl.ValueKind != JsonValueKind.String)
            {
                failureReason = ProviderFailureReason.MissingDisplayText;
                return false;
            }

            var display = displayEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(display))
            {
                failureReason = ProviderFailureReason.MissingDisplayText;
                return false;
            }

            if (display.Length > AssistantResponseSchema.MaxDisplayCharacters)
            {
                failureReason = ProviderFailureReason.ResponseTooLarge;
                return false;
            }

            if (!TrySpeech(root, out var speech, out failureReason))
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

            response = new ModelSemanticResponse(display, speech, blocks, memory);
            failureReason = string.Empty;
            return true;
        }
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

    private static bool TrySpeech(JsonElement root, out ModelSpeechProjection speech, out string failureReason)
    {
        speech = new ModelSpeechProjection(ModelSpeechMode.Same, null);
        failureReason = ProviderFailureReason.InvalidSpeech;
        if (!root.TryGetProperty("speech", out var speechEl) || speechEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!speechEl.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var modeText = modeEl.GetString();
        ModelSpeechMode mode;
        if (string.Equals(modeText, "same", StringComparison.OrdinalIgnoreCase))
        {
            mode = ModelSpeechMode.Same;
        }
        else if (string.Equals(modeText, "custom", StringComparison.OrdinalIgnoreCase))
        {
            mode = ModelSpeechMode.Custom;
        }
        else if (string.Equals(modeText, "none", StringComparison.OrdinalIgnoreCase))
        {
            mode = ModelSpeechMode.None;
        }
        else
        {
            return false;
        }

        string? text = null;
        if (speechEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            text = textEl.GetString();
        }
        else if (speechEl.TryGetProperty("text", out textEl) && textEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return false;
        }

        switch (mode)
        {
            case ModelSpeechMode.Same:
                if (text is not null)
                {
                    return false;
                }

                speech = new ModelSpeechProjection(ModelSpeechMode.Same, null);
                failureReason = string.Empty;
                return true;
            case ModelSpeechMode.Custom:
                if (string.IsNullOrWhiteSpace(text) || text.Length > AssistantResponseSchema.MaxSpeechCharacters)
                {
                    return false;
                }

                speech = new ModelSpeechProjection(ModelSpeechMode.Custom, text);
                failureReason = string.Empty;
                return true;
            case ModelSpeechMode.None:
                if (text is not null)
                {
                    return false;
                }

                speech = new ModelSpeechProjection(ModelSpeechMode.None, null);
                failureReason = string.Empty;
                return true;
            default:
                return false;
        }
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
