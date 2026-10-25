using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Providers.Native;

public sealed partial class NativeLanguageModel
{
    private JsonObject AnthropicBody(ModelRequest request)
    {
        var messages = Transcript(request, true);
        var body = new JsonObject { ["model"] = route.ExternalModelId, ["stream"] = true, ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = TrustedSections(request, true), ["messages"] = messages };
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        var output = new JsonObject();
        if (request.ReasoningEffort is { } effort)
        { output["effort"] = effort; body["thinking"] = new JsonObject { ["type"] = "adaptive" }; }
        if (request.ResponseContract is { } contract)
            output["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = OpenAiCompatibleToolSchema.ForNative(AssistantResponseSchema.JsonSchemaFor(contract), true) };
        if (output.Count > 0) body["output_config"] = output;
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = OpenAiCompatibleToolNames.ToWireName(t.Name), ["description"] = t.Description,
                ["input_schema"] = OpenAiCompatibleToolSchema.ForNative(t.ParametersJson, true)
            }).ToArray());
            body["tool_choice"] = request.ToolChoice switch
            {
                ModelToolChoice.Required => new JsonObject { ["type"] = "any" },
                ModelToolChoice.Named => new JsonObject { ["type"] = "tool", ["name"] = OpenAiCompatibleToolNames.ToWireName(request.ToolChoiceName!) },
                _ => new JsonObject { ["type"] = "auto" }
            };
        }
        return body;
    }
    private JsonObject GeminiBody(ModelRequest request)
    {
        var config = new JsonObject { ["maxOutputTokens"] = request.MaxOutputTokens, ["candidateCount"] = 1 };
        if (request.Temperature is { } temperature) config["temperature"] = temperature;
        if (request.ReasoningEffort is { } effort)
            config["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = effort.ToUpperInvariant(), ["includeThoughts"] = false };
        if (request.ResponseContract is { } contract)
            config["responseFormat"] = new JsonObject { ["text"] = new JsonObject { ["mimeType"] = "APPLICATION_JSON",
                ["schema"] = OpenAiCompatibleToolSchema.ForNative(AssistantResponseSchema.JsonSchemaFor(contract), false) } };
        var body = new JsonObject { ["systemInstruction"] = new JsonObject { ["parts"] = TrustedSections(request, false) },
            ["contents"] = Transcript(request, false), ["generationConfig"] = config };
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                { ["name"] = OpenAiCompatibleToolNames.ToWireName(t.Name), ["description"] = t.Description,
                    ["parametersJsonSchema"] = OpenAiCompatibleToolSchema.ForNative(t.ParametersJson, false) }).ToArray()) });
            var choice = new JsonObject { ["mode"] = request.ToolChoice == ModelToolChoice.Auto ? "AUTO" : "ANY" };
            if (request.ToolChoice == ModelToolChoice.Named) choice["allowedFunctionNames"] = new JsonArray(OpenAiCompatibleToolNames.ToWireName(request.ToolChoiceName!));
            body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = choice };
        }
        return body;
    }
    private static JsonArray TrustedSections(ModelRequest request, bool anthropic)
    {
        var sections = new JsonArray();
        foreach (var role in new[] { ModelRole.System, ModelRole.Developer })
        {
            var messages = request.Messages.Where(m => m.Role == role).ToArray();
            if (messages.Length == 0) continue;
            if (messages.Any(m => m.Parts?.Any(p => p is not ModelTextContent) == true)) throw new NativeRequestException("Trusted instructions must contain text only.");
            var text = string.Join("\n\n", messages.Select(m => m.Parts is { Count: > 0 } parts ? string.Join("\n", parts.OfType<ModelTextContent>().Select(p => p.Text)) : m.Text));
            var block = new JsonObject { ["text"] = (role == ModelRole.System ? "Core instructions (highest priority):\n" : "Runtime instructions (within Core constraints):\n") + text };
            if (anthropic) block["type"] = "text";
            sections.Add(block);
        }
        return sections;
    }
    private JsonArray Transcript(ModelRequest request, bool anthropic)
    {
        var result = new JsonArray(); var previousCalls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
        {
            if (message.Role is ModelRole.System or ModelRole.Developer) continue;
            if (!Enum.IsDefined(message.Role)) throw new NativeRequestException("Unsupported transcript role.");
            JsonArray parts; string role;
            if (message.Role == ModelRole.Tool)
            {
                if (message.ToolCallId is null || !previousCalls.TryGetValue(message.ToolCallId, out var call)) throw new NativeRequestException("A tool result requires its preceding assistant call.");
                parts = anthropic ? new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = call.Id, ["content"] = Content(message, true, tool: true) })
                    : new JsonArray(new JsonObject { ["functionResponse"] = new JsonObject { ["id"] = call.Id,
                        ["name"] = OpenAiCompatibleToolNames.ToWireName(call.Name), ["response"] = new JsonObject { ["result"] = message.Text } } });
                if (!anthropic && message.Parts?.OfType<ModelImageContent>().Any() == true)
                    foreach (var image in message.Parts.OfType<ModelImageContent>()) parts.Add(Image(image, false));
                role = "user";
            }
            else if (message.ToolCalls is { Count: > 0 } calls)
            {
                if (message.Role != ModelRole.Assistant) throw new NativeRequestException("Tool calls must belong to the assistant.");
                foreach (var call in calls)
                    if (!previousCalls.TryAdd(call.Id, call)) throw new NativeRequestException("Tool call identities must be unique.");
                parts = continuation.Decode(calls) ?? new JsonArray();
                if (parts.Count == 0)
                {
                    if (message.Text.Length > 0) parts.Add(Text(message.Text, anthropic));
                    foreach (var call in calls)
                    {
                        var arguments = JsonNode.Parse(call.ArgumentsJson) as JsonObject ?? throw new NativeRequestException("Tool arguments must be an object.");
                        parts.Add(anthropic ? new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = OpenAiCompatibleToolNames.ToWireName(call.Name), ["input"] = arguments }
                            : new JsonObject { ["functionCall"] = new JsonObject { ["id"] = call.Id, ["name"] = OpenAiCompatibleToolNames.ToWireName(call.Name), ["args"] = arguments } });
                    }
                }
                role = anthropic ? "assistant" : "model";
            }
            else { parts = Content(message, anthropic); role = message.Role == ModelRole.Assistant ? anthropic ? "assistant" : "model" : "user"; }
            var key = anthropic ? "content" : "parts";
            if (result.LastOrDefault() is JsonObject last && last["role"]?.GetValue<string>() == role)
                foreach (var part in parts.ToArray()) { parts.Remove(part); last[key]!.AsArray().Add(part); }
            else result.Add(new JsonObject { ["role"] = role, [key] = parts });
        }
        return result;
    }
    private static JsonArray Content(ModelMessage message, bool anthropic, bool tool = false)
    {
        var result = new JsonArray();
        if (tool || message.Parts is not { Count: > 0 }) result.Add(Text(message.Text, anthropic));
        foreach (var part in message.Parts ?? [])
            if (part is ModelImageContent image) result.Add(Image(image, anthropic));
            else if (part is ModelTextContent text && !tool) result.Add(Text(text.Text, anthropic));
            else if (part is not ModelTextContent) throw new NativeRequestException("Unsupported model content part.", true);
        return result;
    }
    private static JsonObject Text(string text, bool anthropic) => anthropic ? new() { ["type"] = "text", ["text"] = text } : new() { ["text"] = text };
    private static JsonObject Image(ModelImageContent image, bool anthropic)
    {
        if (image.Bytes.Length is < 1 or > 8 * 1024 * 1024 || image.ContentType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
            throw new NativeRequestException("Unsupported native image size or media type.", true);
        var data = Convert.ToBase64String(image.Bytes.AsSpan());
        return anthropic ? new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.ContentType, ["data"] = data } }
            : new() { ["inlineData"] = new JsonObject { ["mimeType"] = image.ContentType, ["data"] = data } };
    }
}
