using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAgentCoreInfrastructure(
        this IServiceCollection services,
        string agentDirectory,
        string profile = "Synthetic",
        LanguageModelProviderOptions? languageModel = null,
        PersistenceOptions? persistence = null)
    {
        persistence ??= new PersistenceOptions();
        services.TryAddSingleton(persistence);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(SyntheticProviderAliases.Default);
        services.TryAddSingleton<PromptContextBuilder>();
        services.TryAddSingleton<IAgentBrain, DefaultAgentBrain>();
        services.TryAddSingleton<IInterruptionClassifier, HeuristicInterruptionClassifier>();
        services.TryAddSingleton<IIdGenerator, SystemIdGenerator>();
        if (string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new SqlitePragmaInterceptor(Math.Max(1, persistence.BusyTimeoutMs)));
            services.AddDbContextFactory<AgentCoreDbContext>((provider, options) =>
            {
                options.UseSqlite(persistence.ConnectionString);
                options.AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>());
            });
            services.AddSingleton<IMemoryStore>(provider => new SqliteMemoryStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>()));
        }
        else
        {
            services.TryAddSingleton<IMemoryStore, InMemoryMemoryStore>();
        }
        services.AddHttpClient(OpenAICompatibleLanguageModel.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient(OpenAICompatibleBatchSpeechRecognizer.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.TryAddSingleton<ISpeechRecognizer>(_ => new SyntheticSpeechRecognizer());
        services.TryAddSingleton<ISpeechSynthesizer>(_ => new SyntheticSpeechSynthesizer());
        services.AddHttpClient(OpenAiSpeechSynthesizer.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.TryAddSingleton<ILanguageModel>(provider =>
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
            return new OpenAICompatibleLanguageModel(http, options, provider.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton<IAgentDefinitionStore>(provider =>
            new FileAgentDefinitionStore(agentDirectory, provider.GetRequiredService<ProviderAliasSet>()));
        services.TryAddSingleton(new VoiceAvailability
        {
            SpeechAdaptersResolved = string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
        });
        services.TryAddSingleton(new InteractionPolicy());
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<SessionRuntimeFactory>();
        return services;
    }
}
