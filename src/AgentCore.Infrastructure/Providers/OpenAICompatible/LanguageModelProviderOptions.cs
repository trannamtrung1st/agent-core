namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

public sealed class LanguageModelProviderOptions
{
    public string Adapter { get; set; } = "Scripted";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
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
}
