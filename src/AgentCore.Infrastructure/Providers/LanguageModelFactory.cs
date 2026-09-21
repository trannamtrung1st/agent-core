using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Providers;

internal static class LanguageModelFactory
{
    public static ILanguageModel Create(
        IServiceProvider provider,
        string profile,
        LanguageModelProviderOptions? languageModel)
    {
        var options = languageModel ?? new LanguageModelProviderOptions { Adapter = "Scripted" };
        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Adapter, "Scripted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Adapter, "Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            return new ScriptedLanguageModel();
        }

        var http = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(OpenAICompatibleLanguageModel.HttpClientName);
        return new OpenAICompatibleLanguageModel(
            http,
            options,
            provider.GetRequiredService<TimeProvider>(),
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAICompatibleLanguageModel>());
    }
}
