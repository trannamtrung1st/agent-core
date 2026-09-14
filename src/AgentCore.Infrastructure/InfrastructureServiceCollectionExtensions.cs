using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAgentCoreInfrastructure(
        this IServiceCollection services,
        string agentDirectory)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdGenerator, SystemIdGenerator>();
        services.TryAddSingleton<IMemoryStore, InMemoryMemoryStore>();
        services.TryAddSingleton<ILanguageModel, ScriptedLanguageModel>();
        services.TryAddSingleton<IAgentDefinitionStore>(_ => new FileAgentDefinitionStore(agentDirectory));
        services.TryAddSingleton<VoiceAvailability>();
        services.TryAddSingleton<ISessionOutput, NoOpSessionOutput>();
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<SessionRuntimeFactory>();
        return services;
    }
}
