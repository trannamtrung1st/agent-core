using AgentCore.Domain.Models;

namespace AgentCore.Application.Models;

// Stable metadata only. These rows never create a connection, credential or tenant default.
public static class BuiltinModelPresets
{
    public static IReadOnlyList<ModelPreset> All { get; } = Array.AsReadOnly(new[]
    {
        Preset(1, ModelProviderKind.OpenAI, "gpt-6-luna", "GPT-6 Luna", ModelTransport.Responses, true, true, ["low", "medium", "high"], "medium"),
        Preset(2, ModelProviderKind.OpenAI, "gpt-6.1-sol", "GPT-6.1 Sol", ModelTransport.Responses, true, true, ["low", "medium", "high"], "medium"),
        Preset(3, ModelProviderKind.Anthropic, "claude-haiku-5-5", "Claude Haiku 5.5", ModelTransport.AnthropicMessages, true, true, ["low", "medium", "high", "xhigh", "max"], "medium"),
        Preset(4, ModelProviderKind.Anthropic, "claude-sonnet-5-5", "Claude Sonnet 5.5", ModelTransport.AnthropicMessages, true, false, ["low", "medium", "high", "xhigh", "max"], "high"),
        Preset(5, ModelProviderKind.Google, "gemini-3.5-flash-lite", "Gemini 3.5 Flash Lite", ModelTransport.GeminiContent, true, false, ["minimal", "low", "medium", "high"], "minimal"),
        Preset(6, ModelProviderKind.Google, "gemini-3.8-flash", "Gemini 3.8 Flash", ModelTransport.GeminiContent, true, false, ["low", "medium", "high"], "medium"),
        Preset(7, ModelProviderKind.OpenRouter, "deepseek/deepseek-v4.1-flash", "DeepSeek V4.1 Flash", ModelTransport.ChatCompletions, false, true, ["low", "medium", "high"], "medium")
    });
    private static ModelPreset Preset(int number, ModelProviderKind kind, string external, string label,
        ModelTransport transport, bool vision, bool forced, string[] efforts, string? effort) => ModelManagementRules.Freeze(new ModelPreset(
            Guid.Parse($"dca10000-0000-4000-8000-{number:000000000000}"), kind, external, label, transport,
            new(true, vision, kind is ModelProviderKind.Anthropic or ModelProviderKind.Google
                ? ModelInstructionHierarchy.OrderedTrustedSections : ModelInstructionHierarchy.NativeDeveloper,
                ModelSemanticMode.NativeStructuredOutput, true, forced, efforts, effort, ModelMetadataSource.VerifiedPreset), true, false, 1));
}
