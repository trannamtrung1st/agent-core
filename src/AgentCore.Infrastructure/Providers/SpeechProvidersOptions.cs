using AgentCore.Infrastructure.Providers.OpenAI;

namespace AgentCore.Infrastructure.Providers;

public sealed class SpeechProvidersOptions
{
    public SpeechRecognitionProviderOptions Recognition { get; set; } = new();
    public SpeechSynthesisProviderOptions Synthesis { get; set; } = new();
}

public sealed class SpeechProviderSelection
{
    public required SpeechProvidersOptions Options { get; init; }
    public bool RecognitionKnown { get; init; }
    public bool SynthesisKnown { get; init; }
    public bool RecognitionMissingHostedKey { get; init; }
    public bool SynthesisMissingHostedKey { get; init; }
}

public static class SpeechAdapterCatalog
{
    public const string Synthetic = "Synthetic";
    public const string Browser = "Browser";
    public const string OpenAI = "OpenAI";
    public const string OpenAICompatibleBatch = "OpenAICompatibleBatch";

    public static readonly string[] RecognitionAdapters =
    [
        Synthetic,
        Browser,
        OpenAI,
        OpenAICompatibleBatch
    ];

    public static readonly string[] SynthesisAdapters =
    [
        Synthetic,
        Browser,
        OpenAI
    ];

    public static bool EqualsName(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownRecognition(string? adapter) =>
        RecognitionAdapters.Any(name => EqualsName(adapter, name));

    public static bool IsKnownSynthesis(string? adapter) =>
        SynthesisAdapters.Any(name => EqualsName(adapter, name));

    public static bool IsBrowser(string? adapter) => EqualsName(adapter, Browser);

    public static bool RecognitionRequiresHostedApiKey(string? adapter) =>
        EqualsName(adapter, OpenAI) || EqualsName(adapter, OpenAICompatibleBatch);

    public static bool SynthesisRequiresHostedApiKey(string? adapter) =>
        EqualsName(adapter, OpenAI);
}
