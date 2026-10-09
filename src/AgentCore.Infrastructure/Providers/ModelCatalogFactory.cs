using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Providers;

internal static class ModelCatalogFactory
{
    public const string DeepSeekV41FlashKey = "deepseek-v41-flash";
    public const string DeepSeekV41FlashModelId = "deepseek/deepseek-v4.1-flash";
    public const string OpenRouterFreeKey = "openrouter-free";
    public const string OpenRouterFreeModelId = "openrouter/free";
    public const string Gpt6LunaKey = "gpt-6-luna";
    public const string Gpt6LunaModelId = "openai/gpt-6-luna";
    public const string ClaudeHaiku55Key = "claude-haiku-5.5";
    public const string ClaudeHaiku55ModelId = "anthropic/claude-haiku-5.5";
    public const string Gpt61SolKey = "gpt-6.1-sol";
    public const string Gpt61SolModelId = "openai/gpt-6.1-sol";
    public static readonly string[] Gpt6LunaReasoningEfforts = ["none", "low", "medium", "high", "xhigh", "max"];
    public static readonly string[] ReasoningEfforts = ["low", "medium", "high", "xhigh", "max"];
    public const string ScriptedAlphaKey = "scripted-alpha";
    public const string ScriptedBetaKey = "scripted-beta";
    public const string ScriptedVisionKey = "scripted-vision";

    public static IModelCatalog Create(
        string profile,
        LanguageModelProviderOptions? languageModel,
        IConfiguration? configuration)
    {
        var configured = Bind(configuration);
        if (IsRealProfile(profile))
        {
            if (configured.Models.Count > 0 && !IsSyntheticCatalog(configured))
            {
                return WithPrimaryDefault(FromOptions(configured, languageModel), languageModel);
            }

            return WithPrimaryDefault(Real(), languageModel);
        }

        if (configured.Models.Count > 0)
        {
            return FromOptions(configured, languageModel);
        }

        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(languageModel?.Adapter, "Scripted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(languageModel?.Adapter, "Synthetic", StringComparison.OrdinalIgnoreCase)
            || languageModel is null)
        {
            return Synthetic();
        }

        return FromPrimary(languageModel);
    }

    public static IModelCatalog Synthetic() =>
        new ConfigurationModelCatalog(
            ScriptedAlphaKey,
            [
                Descriptor(
                    ScriptedAlphaKey,
                    "Scripted Alpha",
                    "primary-llm",
                    ScriptedAlphaKey,
                    tools: true,
                    vision: false,
                    structuredOutput: false,
                    reasoning: true,
                    ["low", "medium", "high"],
                    "low"),
                Descriptor(
                    ScriptedBetaKey,
                    "Scripted Beta",
                    "primary-llm",
                    ScriptedBetaKey,
                    tools: true,
                    vision: false,
                    structuredOutput: true,
                    reasoning: false,
                    [],
                    null),
                Descriptor(
                    "scripted-vision",
                    "Scripted Vision",
                    "primary-llm",
                    "scripted-vision",
                    tools: true,
                    vision: true,
                    structuredOutput: false,
                    reasoning: false,
                    [],
                    null)
            ]);

    // Verified against OpenRouter /models and provider docs on 2026-10-09.
    public static IModelCatalog Real() =>
        new ConfigurationModelCatalog(DeepSeekV41FlashKey,
        [
            Descriptor(DeepSeekV41FlashKey, "DeepSeek V4.1 Flash", "primary-llm", DeepSeekV41FlashModelId,
                true, true, true, true, ["low", "medium", "high", "max"], "low", preferResponseFunction: true),
            Descriptor(Gpt6LunaKey, "GPT-6 Luna", "primary-llm", Gpt6LunaModelId,
                true, true, true, true, Gpt6LunaReasoningEfforts, "low", transport: ModelInferenceTransport.Responses),
            Descriptor(ClaudeHaiku55Key, "Claude Haiku 5.5", "primary-llm", ClaudeHaiku55ModelId,
                true, true, true, true, ReasoningEfforts, "low"),
            Descriptor(Gpt61SolKey, "GPT-6.1 Sol", "primary-llm", Gpt61SolModelId,
                true, true, true, true, ReasoningEfforts, "low", transport: ModelInferenceTransport.Responses),
            Descriptor(OpenRouterFreeKey, "OpenRouter Free (experimental)", "primary-llm", OpenRouterFreeModelId,
                true, false, false, false, [], null, costCategory: "free")
        ]);

    public static IModelCatalog FromPrimary(LanguageModelProviderOptions languageModel)
    {
        var modelId = string.IsNullOrWhiteSpace(languageModel.DefaultModel)
            ? DeepSeekV41FlashModelId
            : languageModel.DefaultModel.Trim();
        var key = string.Equals(modelId, DeepSeekV41FlashModelId, StringComparison.Ordinal)
            ? DeepSeekV41FlashKey
            : Slug(modelId);
        var reasoning = !string.IsNullOrWhiteSpace(languageModel.ReasoningEffort)
            || string.Equals(modelId, DeepSeekV41FlashModelId, StringComparison.Ordinal);
        var defaultEffort = string.IsNullOrWhiteSpace(languageModel.ReasoningEffort)
            ? (reasoning ? "low" : null)
            : languageModel.ReasoningEffort.Trim();
        var efforts = reasoning ? new[] { "low", "medium", "high" } : Array.Empty<string>();
        var display = string.Equals(key, DeepSeekV41FlashKey, StringComparison.Ordinal)
            ? "DeepSeek V4.1 Flash"
            : modelId;
        return new ConfigurationModelCatalog(
            key,
            [
                Descriptor(
                    key,
                    display,
                    "primary-llm",
                    modelId,
                    languageModel.Tools,
                    languageModel.Vision,
                    structuredOutput: languageModel.StructuredOutput,
                    reasoning,
                    efforts,
                    defaultEffort,
                    transport: languageModel.Transport)
            ]);
    }

    private static IModelCatalog FromOptions(ModelCatalogOptions options, LanguageModelProviderOptions? languageModel)
    {
        var models = options.Models.Select(entry => ToDescriptor(entry, languageModel)).ToArray();
        var defaultKey = string.IsNullOrWhiteSpace(options.DefaultKey) ? models[0].Key : options.DefaultKey.Trim();
        return new ConfigurationModelCatalog(defaultKey, models);
    }

    private static ModelDescriptor ToDescriptor(ModelCatalogEntryOptions entry, LanguageModelProviderOptions? languageModel)
    {
        var provider = string.IsNullOrWhiteSpace(entry.ProviderAlias) ? "primary-llm" : entry.ProviderAlias.Trim();
        if (!string.Equals(provider, "primary-llm", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Model catalog entry '{entry.Key}' uses unsupported ProviderAlias '{provider}'. "
                + "Only 'primary-llm' is supported until multi-provider model routing is implemented.");
        }
        var modelId = string.IsNullOrWhiteSpace(entry.ModelId)
            ? languageModel?.DefaultModel ?? entry.Key
            : entry.ModelId.Trim();
        var efforts = entry.SupportedReasoningEfforts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        var reasoning = entry.Reasoning || efforts.Length > 0;
        var defaultEffort = string.IsNullOrWhiteSpace(entry.DefaultReasoningEffort)
            ? (reasoning ? languageModel?.ReasoningEffort ?? "low" : null)
            : entry.DefaultReasoningEffort.Trim();
        return Descriptor(
            entry.Key.Trim(),
            string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Key.Trim() : entry.DisplayName.Trim(),
            provider,
            modelId,
            entry.Tools,
            entry.Vision,
            entry.StructuredOutput,
            reasoning,
            efforts,
            defaultEffort,
            entry.ContextCategory,
            entry.CostCategory, entry.Transport, entry.PreferResponseFunction);
    }

    private static ModelDescriptor Descriptor(
        string key,
        string displayName,
        string providerAlias,
        string modelId,
        bool tools,
        bool vision,
        bool structuredOutput,
        bool reasoning,
        IReadOnlyList<string> efforts,
        string? defaultEffort,
        string? contextCategory = null,
        string? costCategory = null,
        ModelInferenceTransport transport = ModelInferenceTransport.ChatCompletions,
        bool preferResponseFunction = false) =>
        new(
            key,
            displayName,
            providerAlias,
            modelId,
            tools,
            vision,
            structuredOutput,
            reasoning,
            efforts,
            defaultEffort,
            contextCategory,
            costCategory, transport, preferResponseFunction);

    private static IModelCatalog WithPrimaryDefault(IModelCatalog catalog, LanguageModelProviderOptions? languageModel)
    {
        var modelId = languageModel?.DefaultModel?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return catalog;
        }

        var matches = catalog.Models
            .Where(model => string.Equals(model.ModelId, modelId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"Configured DefaultModel '{modelId}' is not in the model catalog. Select an available model or provide an explicit catalog override.");
        }

        var match = matches.Length == 1
            ? matches[0]
            : matches.FirstOrDefault(model =>
                  !string.Equals(model.Key, catalog.DefaultKey, StringComparison.Ordinal))
              ?? matches[0];
        if (string.Equals(match.Key, catalog.DefaultKey, StringComparison.Ordinal))
        {
            return catalog;
        }

        return new ConfigurationModelCatalog(match.Key, catalog.Models);
    }

    private static bool IsRealProfile(string profile) =>
        string.Equals(profile, "Real", StringComparison.OrdinalIgnoreCase);

    private static bool IsSyntheticCatalog(ModelCatalogOptions options) =>
        options.Models.Count > 0
        && options.Models.All(entry => IsBuiltInScriptedCatalogKey(entry.Key));

    private static bool IsBuiltInScriptedCatalogKey(string key) =>
        string.Equals(key, ScriptedAlphaKey, StringComparison.Ordinal)
        || string.Equals(key, ScriptedBetaKey, StringComparison.Ordinal)
        || string.Equals(key, ScriptedVisionKey, StringComparison.Ordinal);

    private static ModelCatalogOptions Bind(IConfiguration? configuration)
    {
        var options = new ModelCatalogOptions();
        configuration?.GetSection("Providers:ModelCatalog").Bind(options);
        return options;
    }

    private static string Slug(string modelId)
    {
        var chars = modelId
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length == 0 ? "primary" : slug;
    }
}
