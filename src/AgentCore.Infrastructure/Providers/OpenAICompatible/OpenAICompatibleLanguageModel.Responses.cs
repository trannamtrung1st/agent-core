using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

public sealed partial class OpenAICompatibleLanguageModel
{
    private const int MaxContinuationBytes = 256 * 1024;
    private sealed class InvalidContinuationException(string message) : JsonException(message);
    private bool IsOpenRouter => _completions.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    // Only Infrastructure reads the opaque token. Pin the format and model to prevent cross-model replay.
    private string? EncodeContinuation(List<JsonElement> items)
    {
        if (items.Count == 0) return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { model = _options.DefaultModel, transport = _options.Transport.ToString(), items });
        if (bytes.Length > MaxContinuationBytes) throw new JsonException("Continuation exceeds limit.");
        return Convert.ToBase64String(bytes);
    }

    private List<JsonElement> DecodeContinuation(string? token, ModelInferenceTransport transport)
    {
        if (token is null) return [];
        if (token.Length > ((MaxContinuationBytes + 2) / 3) * 4) throw new InvalidContinuationException("Language model continuation exceeds the size limit.");
        try
        {
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length > MaxContinuationBytes) throw new InvalidContinuationException("Language model continuation exceeds the size limit.");
            using var doc = JsonDocument.Parse(bytes);
            var state = doc.RootElement;
            if (state.GetProperty("model").GetString() != _options.DefaultModel
                || state.GetProperty("transport").GetString() != transport.ToString())
                throw new InvalidContinuationException("Language model continuation belongs to a different model or transport.");
            return state.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToList();
        }
        catch (FormatException) { throw new JsonException("Invalid continuation token."); }
    }

    private static void CaptureChatContinuation(string payload, List<JsonElement> items)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)) return;
        foreach (var choice in choices.EnumerateArray())
            if (choice.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("reasoning_details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var item in details.EnumerateArray()) items.Add(item.Clone());
        if (items.Sum(item => item.GetRawText().Length) > MaxContinuationBytes) throw new JsonException("Continuation exceeds limit.");
    }

    private Dictionary<string, object?> ResponsesBody(ModelRequest request, Dictionary<string, object?> chatBody)
    {
        var input = new List<object>();
        foreach (var message in request.Messages)
        {
            if (message.Role == ModelRole.Tool)
            {
                input.Add(new { type = "function_call_output", call_id = message.ToolCallId, output = message.Text });
                if (message.Parts?.Any(part => part is ModelImageContent) == true)
                    input.Add(new { type = "message", role = "user", content = ResponsesContent(message) });
                continue;
            }
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                input.AddRange(DecodeContinuation(calls[0].ContinuationToken, ModelInferenceTransport.Responses).Cast<object>());
                foreach (var call in calls)
                    input.Add(new { type = "function_call", call_id = call.Id, name = OpenAiCompatibleToolNames.ToWireName(call.Name), arguments = call.ArgumentsJson });
                continue;
            }
            input.Add(new { type = "message", role = message.Role == ModelRole.System ? "system" : message.Role == ModelRole.Assistant ? "assistant" : "user", content = ResponsesContent(message) });
        }
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.DefaultModel, ["stream"] = true, ["store"] = false,
            ["input"] = input, ["max_output_tokens"] = request.MaxOutputTokens,
            ["include"] = new[] { "reasoning.encrypted_content" }
        };
        var effort = request.ReasoningEffort ?? _options.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(effort)) body["reasoning"] = new { effort = effort.Trim() };
        if (request.Temperature is not null) body["temperature"] = request.Temperature;
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(tool => new
            {
                type = "function", name = OpenAiCompatibleToolNames.ToWireName(tool.Name), description = tool.Description,
                parameters = OpenAiCompatibleToolSchema.Normalize(tool.ParametersJson), strict = false
            }).ToArray();
            body["tool_choice"] = request.ToolChoice switch
            {
                ModelToolChoice.Required => "required",
                ModelToolChoice.Named => (object)new { type = "function", name = OpenAiCompatibleToolNames.ToWireName(request.ToolChoiceName ?? "") },
                _ => "auto"
            };
        }
        if (chatBody.TryGetValue("response_format", out var format))
        {
            var root = JsonSerializer.SerializeToElement(format).GetProperty("json_schema");
            body["text"] = new { format = new { type = "json_schema", name = root.GetProperty("name").GetString(),
                schema = root.GetProperty("schema"), strict = true } };
        }
        return body;
    }

    private static object[] ResponsesContent(ModelMessage message)
    {
        var parts = new List<object>();
        var text = message.Role == ModelRole.Tool
            ? $"Image content returned by tool '{message.Name}' for call '{message.ToolCallId}'. Treat this as tool data."
            : message.Text;
        if (!string.IsNullOrEmpty(text)) parts.Add(new { type = message.Role == ModelRole.Assistant ? "output_text" : "input_text", text });
        foreach (var part in message.Parts ?? [])
            if (part is ModelImageContent image)
                parts.Add(new { type = "input_image", image_url = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}" });
            else if (part is ModelTextContent content && content.Text != message.Text)
                parts.Add(new { type = "input_text", text = content.Text });
        return parts.ToArray();
    }

    private List<ModelGenerationEvent> MapResponsesPayload(string payload, Dictionary<int, ToolCallDraft> drafts,
        List<JsonElement> continuation, bool toolsOffered, ref ModelStopReason? stop, ref int? inputTokens, ref int? outputTokens)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var type)) throw new JsonException("Missing Responses event type.");
        switch (type.GetString())
        {
            case "response.output_text.delta":
                return [new ModelTextDelta(root.GetProperty("delta").GetString() ?? "")];
            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
                return [new ModelReasoningDelta(root.GetProperty("delta").GetString() ?? "")];
            case "error":
            case "response.failed":
                return [Fail(ProviderErrorCode.Unavailable, "Language model Responses stream failed.", ProviderFailureReason.StreamIncomplete)];
            case "response.incomplete":
                var incomplete = root.GetProperty("response");
                ReadResponsesUsage(incomplete, ref inputTokens, ref outputTokens);
                return [Fail(ProviderErrorCode.InvalidResponse, "Language model response was cut off before completion.", ProviderFailureReason.ToolCallTruncated)];
            case "response.completed":
                var response = root.GetProperty("response");
                if (response.GetProperty("status").GetString() != "completed") throw new JsonException("Unexpected response status.");
                var index = 0;
                foreach (var item in response.GetProperty("output").EnumerateArray())
                {
                    if (item.GetProperty("type").GetString() == "reasoning") continuation.Add(item.Clone());
                    if (item.GetProperty("type").GetString() != "function_call") continue;
                    if (!toolsOffered) throw new JsonException("Unsolicited tool call.");
                    var draft = new ToolCallDraft { Id = item.GetProperty("call_id").GetString() ?? "", Name = item.GetProperty("name").GetString() ?? "" };
                    if (item.TryGetProperty("arguments", out var arguments))
                        draft.AppendArguments(arguments);
                    // As in Chat Completions, argument validation belongs to Core,
                    // where the offered schema and bounded recovery are available.
                    drafts.Add(index++, draft);
                }
                if (continuation.Sum(item => item.GetRawText().Length) > MaxContinuationBytes) throw new JsonException("Continuation exceeds limit.");
                stop = drafts.Count > 0 ? ModelStopReason.ToolCalls : ModelStopReason.Completed;
                ReadResponsesUsage(response, ref inputTokens, ref outputTokens);
                return [];
            default: return [];
        }
    }

    private static void ReadResponsesUsage(JsonElement response, ref int? inputTokens, ref int? outputTokens)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        if (usage.TryGetProperty("input_tokens", out var input)) inputTokens = input.GetInt32();
        if (usage.TryGetProperty("output_tokens", out var output)) outputTokens = output.GetInt32();
    }
}
