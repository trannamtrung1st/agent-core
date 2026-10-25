using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Providers.Native;

public sealed partial class NativeLanguageModel
{
    private sealed class NativeRound(NativeLanguageModel owner, ModelRequest request)
    {
        private readonly SortedDictionary<int, JsonObject> blocks = [];
        private readonly Dictionary<int, StringBuilder> arguments = [];
        private readonly HashSet<int> closed = [];
        private readonly JsonArray geminiParts = [];
        private readonly List<ModelToolCall> calls = [];
        private ModelStopReason? stop;
        private ModelFailed? failure;
        private int? inputTokens, outputTokens;
        private int characters, wireBytes;
        private bool started;
        internal bool Terminal { get; private set; }

        internal IReadOnlyList<ModelGenerationEvent> Apply(JsonElement value)
        {
            wireBytes = checked(wireBytes + Encoding.UTF8.GetByteCount(value.GetRawText()));
            if (wireBytes > 16 * 1024 * 1024 || value.ValueKind != JsonValueKind.Object) throw new JsonException("Native stream exceeds its wire bound.");
            return owner.route.Kind == ModelProviderKind.Anthropic ? Anthropic(value) : Gemini(value);
        }
        private IReadOnlyList<ModelGenerationEvent> Anthropic(JsonElement value)
        {
            var type = value.GetProperty("type").GetString();
            if (type == "error")
            {
                var category = value.TryGetProperty("error", out var error) && error.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                failure = new(new(category switch { "authentication_error" or "permission_error" => ProviderErrorCode.Authentication,
                    "rate_limit_error" => ProviderErrorCode.RateLimited, "invalid_request_error" => ProviderErrorCode.InvalidRequest, _ => ProviderErrorCode.Unavailable },
                    "The native model stream failed.")); Terminal = true; return [];
            }
            if (type == "ping") return [];
            if (type == "message_start")
            {
                if (started) throw new JsonException("Repeated native message start."); started = true;
                var message = value.GetProperty("message"); if (message.GetProperty("role").GetString() != "assistant") throw new JsonException("Unexpected native role.");
                Usage(message); return [];
            }
            if (!started) throw new JsonException("Native content preceded its message.");
            if (type == "message_delta")
            {
                var reason = value.GetProperty("delta").GetProperty("stop_reason").GetString();
                if (stop is not null || blocks.Keys.Any(i => !closed.Contains(i))) throw new JsonException("Invalid native message termination.");
                stop = reason switch { "end_turn" or "stop_sequence" => ModelStopReason.Completed, "tool_use" => ModelStopReason.ToolCalls,
                    "max_tokens" => ModelStopReason.LengthLimit, "refusal" => ModelStopReason.ContentFiltered, _ => throw new JsonException("Unsupported native stop reason.") };
                Usage(value); return [];
            }
            if (type == "message_stop")
            {
                if (stop is null) throw new JsonException("Native stream ended without a reason."); Terminal = true; return [];
            }
            if (stop is not null) throw new JsonException("Content followed native termination.");
            var index = value.GetProperty("index").GetInt32();
            if (index is < 0 or >= 256) throw new JsonException("Native block index exceeds its bound.");
            if (type == "content_block_start")
            {
                if (index != blocks.Count) throw new JsonException("Native block order is invalid.");
                var block = JsonNode.Parse(value.GetProperty("content_block").GetRawText())!.AsObject();
                var blockType = block["type"]?.GetValue<string>();
                if (blockType is not ("text" or "thinking" or "redacted_thinking" or "tool_use")) throw new JsonException("Unsupported native content block.");
                blocks.Add(index, block);
                var text = block[blockType == "thinking" ? "thinking" : "text"]?.GetValue<string>();
                if (text is { Length: > 0 }) { Bound(text); return [blockType == "thinking" ? new ModelReasoningDelta(text) : new ModelTextDelta(text)]; }
                return [];
            }
            if (!blocks.TryGetValue(index, out var current) || closed.Contains(index)) throw new JsonException("Native delta has no open block.");
            if (type == "content_block_stop")
            {
                closed.Add(index);
                if (current["type"]!.GetValue<string>() == "tool_use")
                {
                    var input = arguments.TryGetValue(index, out var builder) ? JsonNode.Parse(builder.Length == 0 ? "{}" : builder.ToString()) : current["input"]?.DeepClone();
                    if (input is not JsonObject) throw new JsonException("Native tool arguments must be an object.");
                    current["input"] = input;
                }
                return [];
            }
            if (type != "content_block_delta") throw new JsonException("Unsupported native stream event.");
            var delta = value.GetProperty("delta"); var deltaType = delta.GetProperty("type").GetString();
            var field = deltaType switch { "text_delta" => "text", "thinking_delta" => "thinking", "signature_delta" => "signature", "input_json_delta" => "partial_json", _ => throw new JsonException("Unsupported native delta.") };
            var textDelta = delta.GetProperty(field).GetString() ?? throw new JsonException("Invalid native delta value."); Bound(textDelta);
            var expected = deltaType switch { "text_delta" => "text", "input_json_delta" => "tool_use", _ => "thinking" };
            if (current["type"]!.GetValue<string>() != expected) throw new JsonException("Native delta changed content kind.");
            if (deltaType == "input_json_delta")
            {
                if (current["input"] is JsonObject { Count: > 0 }) throw new JsonException("Native tool input has conflicting representations.");
                if (!arguments.TryGetValue(index, out var builder)) arguments[index] = builder = new(); builder.Append(textDelta);
            }
            else current[field] = (current[field]?.GetValue<string>() ?? "") + textDelta;
            return deltaType switch { "text_delta" => [new ModelTextDelta(textDelta)], "thinking_delta" => [new ModelReasoningDelta(textDelta)], _ => [] };
        }
        private IReadOnlyList<ModelGenerationEvent> Gemini(JsonElement value)
        {
            if (value.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var number) && number.TryGetInt32(out var status) ? status : 503;
                failure = HttpFailure((System.Net.HttpStatusCode)code, null); Terminal = true; return [];
            }
            if (value.TryGetProperty("usageMetadata", out var usage))
            {
                inputTokens = Count(usage, "promptTokenCount") ?? inputTokens;
                outputTokens = Count(usage, "candidatesTokenCount") ?? outputTokens;
            }
            if (value.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
            { stop = ModelStopReason.ContentFiltered; return []; }
            if (!value.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) return [];
            if (candidates.GetArrayLength() != 1 || stop is not null) throw new JsonException("Native stream has an ambiguous candidate or content after termination.");
            var candidate = candidates[0]; var events = new List<ModelGenerationEvent>();
            if (candidate.TryGetProperty("content", out var content))
            {
                if (content.TryGetProperty("role", out var role) && role.GetString() != "model") throw new JsonException("Unexpected native candidate role.");
                foreach (var part in content.GetProperty("parts").EnumerateArray())
                {
                    if (geminiParts.Count >= 4096) throw new JsonException("Native part count exceeds its bound.");
                    geminiParts.Add(JsonNode.Parse(part.GetRawText()));
                    if (part.TryGetProperty("functionCall", out var call))
                    {
                        var name = call.GetProperty("name").GetString() ?? throw new JsonException("Missing native function name.");
                        var args = call.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                        if (JsonNode.Parse(args) is not JsonObject) throw new JsonException("Native function arguments must be an object."); Bound(args);
                        var id = call.TryGetProperty("id", out var nativeId) ? nativeId.GetString()! : "gemini_" + owner.ids.NewId().ToString("N");
                        calls.Add(new(Token(id), Canonical(name), args));
                        // A generated id is required in the exact replayed native function call too.
                        geminiParts[^1]!["functionCall"]!["id"] = id;
                    }
                    else if (part.TryGetProperty("text", out var text))
                    {
                        var delta = text.GetString() ?? throw new JsonException("Invalid native text."); Bound(delta);
                        events.Add(part.TryGetProperty("thought", out var thought) && thought.GetBoolean() ? new ModelReasoningDelta(delta) : new ModelTextDelta(delta));
                    }
                    else if (!part.TryGetProperty("thoughtSignature", out _)) throw new JsonException("Unsupported native content part.");
                }
            }
            if (candidate.TryGetProperty("finishReason", out var finish))
                stop = finish.GetString() switch
                {
                    "STOP" => calls.Count > 0 ? ModelStopReason.ToolCalls : ModelStopReason.Completed,
                    "MAX_TOKENS" => ModelStopReason.LengthLimit,
                    "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" => ModelStopReason.ContentFiltered,
                    _ => throw new JsonException("Unsupported native finish reason.")
                };
            return events;
        }
        internal IReadOnlyList<ModelGenerationEvent> Complete()
        {
            if (failure is not null) return [failure];
            if (stop is null || owner.route.Kind == ModelProviderKind.Anthropic && !Terminal) throw new JsonException("Native stream is incomplete.");
            var content = owner.route.Kind == ModelProviderKind.Anthropic ? new JsonArray(blocks.Values.Select(b => (JsonNode)b.DeepClone()).ToArray()) : geminiParts;
            if (owner.route.Kind == ModelProviderKind.Anthropic)
                foreach (var block in blocks.Values.Where(b => b["type"]!.GetValue<string>() == "tool_use"))
                    calls.Add(new(Token(block["id"]!.GetValue<string>()), Canonical(block["name"]!.GetValue<string>()), block["input"]!.ToJsonString()));
            if (calls.Count > 64 || calls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count
                || calls.Count > 0 && stop != ModelStopReason.ToolCalls || calls.Count == 0 && stop == ModelStopReason.ToolCalls)
                throw new JsonException("Native tool round is incomplete or inconsistent.");
            if (request.ToolChoice != ModelToolChoice.Auto && (calls.Count == 0 || request.ToolChoice == ModelToolChoice.Named && calls.Any(c => c.Name != request.ToolChoiceName)))
                throw new JsonException("Native response did not honor the explicit tool choice.");
            var result = new List<ModelGenerationEvent>();
            if (calls.Count > 0)
            {
                if (blocks.Values.Any(b => b["type"]?.GetValue<string>() == "thinking" && string.IsNullOrEmpty(b["signature"]?.GetValue<string>())))
                    throw new JsonException("Native thinking continuation is missing its signature.");
                var token = owner.continuation.Encode(content, calls);
                calls[0] = calls[0] with { ContinuationToken = token };
                result.AddRange(calls.Select(c => new ModelToolCallEvent(c)));
            }
            result.Add(new ModelCompleted(stop.Value, inputTokens, outputTokens)); return result;
        }
        private string Canonical(string wireName)
        {
            var name = OpenAiCompatibleToolNames.ToCanonicalName(wireName, request.Tools);
            if (request.Tools?.Any(t => t.Name == name) != true) throw new JsonException("Native response called an unoffered tool."); return name;
        }
        private static string Token(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl)
            ? value : throw new JsonException("Native tool identity is invalid.");
        private void Bound(string value) { characters = checked(characters + value.Length); if (characters > NativeModelContinuation.MaxBytes) throw new JsonException("Native response exceeds its bound."); }
        private void Usage(JsonElement value)
        {
            if (!value.TryGetProperty("usage", out var usage)) return;
            inputTokens = Count(usage, "input_tokens") ?? inputTokens; outputTokens = Count(usage, "output_tokens") ?? outputTokens;
        }
        private static int? Count(JsonElement value, string name) => value.TryGetProperty(name, out var count) && count.TryGetInt32(out var number) && number >= 0 ? number : null;
    }
}
