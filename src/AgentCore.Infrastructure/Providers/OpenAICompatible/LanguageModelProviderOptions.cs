using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAI;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

public sealed class LanguageModelProviderOptions
{
    public string Adapter { get; set; } = "Scripted";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    public string? ReasoningEffort { get; set; }
    /// <summary>
    /// When true, send OpenRouter-style <c>reasoning</c> object (effort + exclude) instead of legacy <c>reasoning_effort</c> only.
    /// </summary>
    public bool ReasoningObjectWire { get; set; }
    /// <summary>
    /// When <see cref="ReasoningObjectWire"/> is enabled, request hidden reasoning output while preserving effort.
    /// </summary>
    public bool ExcludeVisibleReasoning { get; set; } = true;
    /// <summary>
    /// Map provider <c>delta.reasoning</c> / <c>reasoning_details</c> to <see cref="ModelReasoningDelta"/> instead of text.
    /// </summary>
    public bool MapSeparateReasoningDeltas { get; set; } = true;
    /// <summary>
    /// Opt-in dev/probe hook: records which SSE choice fields were present without logging secrets or user content.
    /// </summary>
    public Action<StreamChoiceDiagnostic>? StreamChoiceDiagnostic { get; set; }
    public bool Vision { get; set; }
    public bool Tools { get; set; }
    public bool StructuredOutput { get; set; }
    /// <summary>
    /// When true, include the provider's free-form error message in local logs after redaction.
    /// Disabled by default because that text can echo ordinary private user content.
    /// </summary>
    public bool LogProviderErrorMessages { get; set; }
    public Dictionary<string, string> AdditionalHeaders { get; set; } = [];
    public ProviderTimeoutOptions Timeouts { get; set; } = new();
}

public sealed class ProviderTimeoutOptions
{
    public int SetupSeconds { get; set; } = 10;
    public int StreamIdleSeconds { get; set; } = 20;
    public int TotalSeconds { get; set; } = 120;
}

public sealed class ProvidersOptions
{
    public Dictionary<string, LanguageModelProviderOptions> LanguageModels { get; set; } = [];
    public Dictionary<string, SpeechRecognitionProviderOptions> SpeechRecognizers { get; set; } = [];
    public Dictionary<string, SpeechSynthesisProviderOptions> SpeechSynthesizers { get; set; } = [];
    public SpeechProvidersOptions Speech { get; set; } = new();
}
