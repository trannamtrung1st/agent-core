using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Models;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Providers.Native;

// Only Infrastructure opens this state. Hidden reasoning/signatures never enter visible history.
internal sealed class NativeModelContinuation(ModelRoutePin route, ICredentialProtector protector)
{
    internal const int MaxBytes = 256 * 1024;
    private const int MaxTokenCharacters = AgentRunLimits.MaxCheckpointBytes / 2;
    private const int ProtectionPurpose = 101; // Distinct from the credential payload's supported protection version (1).
    private sealed record Call(string Id, string Name);
    private sealed record State(int Version, ModelRoutePin Route, IReadOnlyList<Call> Calls, JsonArray Content);
    private sealed record OpaqueState(int Version, ModelRoutePin Route, string Inner);
    internal string EncodeOpaque(string inner)
    {
        if (inner.Length > MaxBytes * 2) throw new JsonException("Provider continuation exceeds its bound.");
        var json = JsonSerializer.Serialize(new OpaqueState(2, route, inner));
        return Protect(json);
    }
    internal string DecodeOpaque(string token)
    {
        if (token.Length > MaxTokenCharacters) throw new JsonException("Provider continuation exceeds its checkpoint bound.");
        try
        {
            var json = protector.Unprotect(route.OfferingId, ProtectionPurpose, token);
            if (json.Length > MaxBytes * 3) throw new JsonException("Provider continuation exceeds its bound.");
            var state = JsonSerializer.Deserialize<OpaqueState>(json);
            if (state is null || state.Version != 2 || state.Route != route || state.Inner is null || state.Inner.Length > MaxBytes * 2)
                throw new JsonException("Continuation belongs to a different model route.");
            return state.Inner;
        }
        catch (AgentCore.Application.Sessions.AgentCoreException) { throw new JsonException("Provider continuation is invalid or unavailable."); }
    }
    internal string Encode(JsonArray content, IReadOnlyList<ModelToolCall> calls)
    {
        var json = JsonSerializer.Serialize(new State(1, route, calls.Select(c => new Call(c.Id, c.Name)).ToArray(), content));
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new JsonException("Native continuation exceeds its bound.");
        return Protect(json);
    }
    internal JsonArray? Decode(IReadOnlyList<ModelToolCall> calls)
    {
        var tokens = calls.Select(c => c.ContinuationToken).Where(t => t is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (tokens.Length == 0) return null;
        if (tokens.Length != 1 || tokens[0]!.Length > MaxTokenCharacters) throw new NativeRequestException("Invalid native continuation.");
        try
        {
            var json = protector.Unprotect(route.OfferingId, ProtectionPurpose, tokens[0]!);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new NativeRequestException("Native continuation exceeds its bound.");
            var state = JsonSerializer.Deserialize<State>(json);
            if (state is null || state.Version != 1 || state.Route != route || state.Calls is null || state.Content is null || state.Calls.Count != calls.Count || state.Content.Count > 4096)
                throw new NativeRequestException("Continuation belongs to a different route or tool round.");
            var arguments = route.Kind == ModelProviderKind.Anthropic
                ? state.Content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use").Select(b => b["input"]).ToArray()
                : state.Content.OfType<JsonObject>().Where(b => b["functionCall"] is not null).Select(b => b["functionCall"]!["args"]).ToArray();
            if (arguments.Length != calls.Count) throw new NativeRequestException("Continuation does not match the retained tool round.");
            for (var i = 0; i < calls.Count; i++)
                if (state.Calls[i].Id != calls[i].Id || state.Calls[i].Name != calls[i].Name
                    || !JsonNode.DeepEquals(arguments[i] ?? new JsonObject(), JsonNode.Parse(calls[i].ArgumentsJson)))
                    throw new NativeRequestException("Continuation does not match the retained tool round.");
            return state.Content.DeepClone().AsArray();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or AgentCore.Application.Sessions.AgentCoreException)
        { throw new NativeRequestException("Native continuation is invalid or unavailable."); }
    }
    private string Protect(string json)
    {
        var token = protector.Protect(route.OfferingId, ProtectionPurpose, json);
        if (token.Length > MaxTokenCharacters) throw new JsonException("Provider continuation exceeds its reserved checkpoint bound.");
        return token;
    }
}

internal sealed class NativeRequestException(string message, bool unsupported = false) : Exception(message)
{
    internal bool Unsupported { get; } = unsupported;
}
